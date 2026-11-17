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

    /// <summary>The apt commands installing WPE once the package manager is free: Debian's keys (the source's signature),
    /// the lists refreshed (retried while another apt holds them), then the packages -- each waiting for dpkg's lock.</summary>
    private static string AptInstallWpe() =>
        "DEBIAN_FRONTEND=noninteractive apt-get -q -y -o DPkg::Lock::Timeout=1800 install debian-archive-keyring >/dev/null; "
        + "for i in $(seq 120); do apt-get -q update >/dev/null 2>&1 && break; sleep 10; done; "
        + $"DEBIAN_FRONTEND=noninteractive apt-get -q -y -o DPkg::Lock::Timeout=1800 install --no-install-recommends {WpePackages}";

    /// <summary>postinst: WPE installed by apt after this package's own installation, where it is not yet.</summary>
    internal static string WebViewPostInst(string pkg) => $$"""
        # web content: WPE WebKit from the source this package brings, installed once dpkg is done with this one
        if ! dpkg-query -W -f='${Status}' libwpewebkit-2.0-1 2>/dev/null | grep -q 'ok installed'; then
          if [ -d /run/systemd/system ] && command -v systemd-run >/dev/null 2>&1; then
            systemctl reset-failed {{pkg}}-wpewebkit.service >/dev/null 2>&1 || true
            systemd-run --quiet --collect --unit={{pkg}}-wpewebkit --description="WPE WebKit for {{pkg}}" --on-active=2 \
              /bin/sh -c '{{AptInstallWpe()}}' || true
            echo "WPE WebKit (web content) is installed in the background: journalctl -u {{pkg}}-wpewebkit"
          else
            echo "Web content needs WPE WebKit: apt-get update && apt-get install {{WpePackages}}" >&2
          fi
        fi

        """;

    /// <summary>install.sh: WPE from this Linux's own package manager, or on a Debian/Ubuntu through the pinned
    /// source (installed with the files), straight away -- no dpkg holds the lock here.</summary>
    internal static string WebViewInstallStep(string pkg) => WebViewInstallStepText(WebViewSourcesPath(pkg), WebViewPinPath(pkg));

    private static string WebViewInstallStepText(string webSources, string webPin) => $$"""
        # web content: WPE WebKit, from this Linux's package manager
        if command -v apt-get >/dev/null 2>&1; then
          sh -c '{{AptInstallWpe()}}' || echo "WPE WebKit could not be installed: web content will not show" >&2
        else
          # the apt source the files carry is the .deb's: of no use here
          rm -f "{{webSources}}" "{{webPin}}"
        fi
        if command -v apt-get >/dev/null 2>&1; then :
        elif command -v dnf >/dev/null 2>&1; then
          dnf -y install wpewebkit wpebackend-fdo || echo "WPE WebKit could not be installed: web content will not show" >&2
        elif command -v pacman >/dev/null 2>&1; then
          pacman -S --needed --noconfirm wpewebkit wpebackend-fdo || echo "WPE WebKit could not be installed: web content will not show" >&2
        elif command -v zypper >/dev/null 2>&1; then
          zypper -n install libWPEWebKit-2_0-1 libWPEBackend-fdo-1_0-1 || echo "WPE WebKit could not be installed: web content will not show" >&2
        else
          echo "Web content needs WPE WebKit 2.x (libWPEWebKit-2.0.so.1) and WPEBackend-fdo: install them with this Linux's package manager" >&2
        fi

        """;
}
