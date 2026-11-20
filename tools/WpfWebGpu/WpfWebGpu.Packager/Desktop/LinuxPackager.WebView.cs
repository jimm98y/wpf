// The installation's web content on Linux: WPE WebKit, which the stack's WebView2 runs on there (LinuxWebViewBackend),
// installed by the system's package manager as Edge's WebView2 Runtime is by its own installer on Windows -- and
// updated by it from then on, never bundled.
//
// Ubuntu packages no WPE; Debian does. The .deb therefore brings Debian's archive as an apt source pinned to WPE's
// packages and the few libraries they need that Ubuntu lacks (nothing else is ever taken from it):
//
//   /etc/apt/sources.list.d/<pkg>-wpewebkit.sources   Debian stable, its updates and security archives
//   /etc/apt/preferences.d/<pkg>-wpewebkit            every Debian package at -1, but those
//
// A package's dependencies are resolved before its files exist, so WPE cannot be a Depends of the package that
// brings its source: it is a Recommends (apt passes over it while it cannot be had), and postinst has apt install it
// as soon as dpkg is done -- a transient systemd unit, waiting for the package manager's locks. install.sh, for a
// Linux without dpkg, asks that Linux's own package manager (Fedora, Arch and openSUSE package WPE themselves).
//
// Only an installation with web content gets any of this: one that carries WebView2's assemblies.
//
// What the installation's install adds of these packages (not one the machine had already) is listed beside its
// state (wpewebkit-installed): a purge (uninstall.sh --all) removes them again, where no other installation's source
// of WPE is left.

namespace WpfWebGpu.Packager.Desktop;

public static partial class LinuxPackager
{
    /// <summary>WPE WebKit's packages, as Debian names them.</summary>
    internal const string WpePackages = "libwpewebkit-2.0-1 libwpebackend-fdo-1.0-1";

    /// <summary>Whether the installation hosts web content: WebView2's assemblies in it.</summary>
    internal static bool UsesWebView(PackageManifest manifest) =>
        Directory.EnumerateFiles(manifest.Inputs.InstallDir, "Microsoft.Web.WebView2.*.dll", SearchOption.AllDirectories).Any();

    internal static string WebViewSourcesPath(string pkg) => $"/etc/apt/sources.list.d/{pkg}-wpewebkit.sources";

    internal static string WebViewPinPath(string pkg) => $"/etc/apt/preferences.d/{pkg}-wpewebkit";

    internal const string WebViewSources = """
        # Debian, for WPE WebKit alone (see the pin beside it in /etc/apt/preferences.d)
        Types: deb
        URIs: http://deb.debian.org/debian
        Suites: trixie trixie-updates
        Components: main
        Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg

        Types: deb
        URIs: http://security.debian.org/debian-security
        Suites: trixie-security
        Components: main
        Signed-By: /usr/share/keyrings/debian-archive-keyring.gpg

        """;

    // WPE, its two libwpe parts, and what Ubuntu does not have of their dependencies (Debian's ICU, libjpeg-turbo's
    // 6.2 ABI, WOFF, libxml2's 2.9 ABI: each a library package of its own name, beside Ubuntu's)
    private const string PinnedPackages = "libwpewebkit-2.0-1 libwpe-1.0-1 libwpebackend-fdo-1.0-1 libicu76 libjpeg62-turbo libwoff1 libxml2";

    internal const string WebViewPin = $"""
        # Debian is a source of WPE WebKit and the few libraries it needs that Ubuntu does not have -- nothing else
        Package: *
        Pin: origin "deb.debian.org"
        Pin-Priority: -1

        Package: *
        Pin: origin "security.debian.org"
        Pin-Priority: -1

        Package: {PinnedPackages}
        Pin: origin "deb.debian.org"
        Pin-Priority: 500

        Package: {PinnedPackages}
        Pin: origin "security.debian.org"
        Pin-Priority: 500

        """;

    /// <summary>What the installation may bring of apt's: the keyring, WPE and the libraries pinned for it.</summary>
    private const string AptTracked = "debian-archive-keyring " + PinnedPackages;

    /// <summary>The file listing the packages the installation's install added, beside its state.</summary>
    internal static string WebViewRecord(string state) => Path.GetDirectoryName(state) + "/wpewebkit-installed";

    /// <summary>The apt commands installing WPE once the package manager is free: Debian's keys (the source's signature),
    /// the lists refreshed (retried while another apt holds them), then the packages -- each waiting for dpkg's lock --
    /// and those of them the machine did not have before listed in <paramref name="record"/>. (No single quote: it runs
    /// in one.)</summary>
    private static string AptInstallWpe(string record) =>
        $"had=; for p in {AptTracked}; do dpkg-query -W -f=\\${{db:Status-Abbrev}} $p 2>/dev/null | grep -q ^ii && had=\"$had $p\"; done; "
        + "DEBIAN_FRONTEND=noninteractive apt-get -q -y -o DPkg::Lock::Timeout=1800 install debian-archive-keyring >/dev/null; "
        + "for i in $(seq 120); do apt-get -q update >/dev/null 2>&1 && break; sleep 10; done; "
        + $"DEBIAN_FRONTEND=noninteractive apt-get -q -y -o DPkg::Lock::Timeout=1800 install --no-install-recommends {WpePackages}; r=$?; "
        + $"mkdir -p {Path.GetDirectoryName(record)}; for p in {AptTracked}; do case \" $had \" in *\" $p \"*) ;; *) dpkg-query -W -f=\\${{db:Status-Abbrev}} $p 2>/dev/null | grep -q ^ii && echo apt $p >> {record};; esac; done; exit $r";

    /// <summary>A purge's part: the packages the install added (the record's), removed by the package manager that
    /// added them, where no other installation's WPE source is left; <paramref name="background"/>: from a maintainer
    /// script, once dpkg is done (a transient unit waiting for its lock).</summary>
    internal static string WebViewPurge(string pkg, string record, bool background) => $$"""
        if [ -f "{{record}}" ] && ! ls /etc/apt/sources.list.d/*-wpewebkit.sources >/dev/null 2>&1; then
          apt_pk=$(sed -n 's/^apt //p' "{{record}}" | tr '\n' ' ')
          rpm_pk=$(sed -n 's/^rpm //p' "{{record}}" | tr '\n' ' ')
          pac_pk=$(sed -n 's/^pacman //p' "{{record}}" | tr '\n' ' ')
          if [ -n "$apt_pk" ] && command -v apt-get >/dev/null 2>&1; then
        {{(background
            ? $"    systemd-run --quiet --collect --unit={pkg}-wpewebkit-remove --description=\"WPE WebKit of {pkg} removed\" --on-active=2 /bin/sh -c \"DEBIAN_FRONTEND=noninteractive apt-get -q -y -o DPkg::Lock::Timeout=1800 purge $apt_pk\" >/dev/null 2>&1 || true"
            : "    DEBIAN_FRONTEND=noninteractive apt-get -q -y purge $apt_pk >/dev/null 2>&1 || true")}}
          fi
          if [ -n "$rpm_pk" ]; then
            if command -v dnf >/dev/null 2>&1; then dnf -y remove $rpm_pk >/dev/null 2>&1 || true
            elif command -v zypper >/dev/null 2>&1; then zypper -n remove $rpm_pk >/dev/null 2>&1 || true; fi
          fi
          [ -n "$pac_pk" ] && command -v pacman >/dev/null 2>&1 && { pacman -R --noconfirm $pac_pk >/dev/null 2>&1 || true; }
        fi

        """;

    /// <summary>postinst: WPE installed by apt after this package's own installation, where it is not yet.</summary>
    internal static string WebViewPostInst(string pkg, string record) => $$"""
        # web content: WPE WebKit from the source this package brings, installed once dpkg is done with this one
        if ! dpkg-query -W -f='${Status}' libwpewebkit-2.0-1 2>/dev/null | grep -q 'ok installed'; then
          if [ -d /run/systemd/system ] && command -v systemd-run >/dev/null 2>&1; then
            systemctl reset-failed {{pkg}}-wpewebkit.service >/dev/null 2>&1 || true
            systemd-run --quiet --collect --unit={{pkg}}-wpewebkit --description="WPE WebKit for {{pkg}}" --on-active=2 \
              /bin/sh -c '{{AptInstallWpe(record)}}' || true
            echo "WPE WebKit (web content) is installed in the background: journalctl -u {{pkg}}-wpewebkit"
          else
            echo "Web content needs WPE WebKit: apt-get update && apt-get install {{WpePackages}}" >&2
          fi
        fi

        """;

    /// <summary>install.sh: WPE from this Linux's own package manager, or on a Debian/Ubuntu through the pinned
    /// source (installed with the files), straight away -- no dpkg holds the lock here.</summary>
    internal static string WebViewInstallStep(string pkg, string record) => WebViewInstallStepText(WebViewSourcesPath(pkg), WebViewPinPath(pkg), record);

    // (rpm's and pacman's packages: those of them not there before, listed as apt's are)
    private static string Added(string kind, string query, string packages, string record) =>
        $"for p in {packages}; do case \" $had \" in *\" $p \"*) ;; *) {query} $p >/dev/null 2>&1 && echo {kind} $p >> \"{record}\";; esac; done";

    private static string Had(string query, string packages) =>
        $"had=; for p in {packages}; do {query} $p >/dev/null 2>&1 && had=\"$had $p\"; done";

    private static string WebViewInstallStepText(string webSources, string webPin, string record) => $$"""
        # web content: WPE WebKit, from this Linux's package manager
        mkdir -p "{{Path.GetDirectoryName(record)}}"
        if command -v apt-get >/dev/null 2>&1; then
          sh -c '{{AptInstallWpe(record)}}' || echo "WPE WebKit could not be installed: web content will not show" >&2
        else
          # the apt source the files carry is the .deb's: of no use here
          rm -f "{{webSources}}" "{{webPin}}"
        fi
        if command -v apt-get >/dev/null 2>&1; then :
        elif command -v dnf >/dev/null 2>&1; then
          {{Had("rpm -q", "wpewebkit wpebackend-fdo")}}
          dnf -y install wpewebkit wpebackend-fdo || echo "WPE WebKit could not be installed: web content will not show" >&2
          {{Added("rpm", "rpm -q", "wpewebkit wpebackend-fdo", record)}}
        elif command -v pacman >/dev/null 2>&1; then
          {{Had("pacman -Q", "wpewebkit wpebackend-fdo")}}
          pacman -S --needed --noconfirm wpewebkit wpebackend-fdo || echo "WPE WebKit could not be installed: web content will not show" >&2
          {{Added("pacman", "pacman -Q", "wpewebkit wpebackend-fdo", record)}}
        elif command -v zypper >/dev/null 2>&1; then
          {{Had("rpm -q", "libWPEWebKit-2_0-1 libWPEBackend-fdo-1_0-1")}}
          zypper -n install libWPEWebKit-2_0-1 libWPEBackend-fdo-1_0-1 || echo "WPE WebKit could not be installed: web content will not show" >&2
          {{Added("rpm", "rpm -q", "libWPEWebKit-2_0-1 libWPEBackend-fdo-1_0-1", record)}}
        else
          echo "Web content needs WPE WebKit 2.x (libWPEWebKit-2.0.so.1) and WPEBackend-fdo: install them with this Linux's package manager" >&2
        fi

        """;
}
