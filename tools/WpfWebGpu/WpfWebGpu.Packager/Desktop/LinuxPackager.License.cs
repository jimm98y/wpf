// The license agreement (the manifest's product.license) in the Linux packages:
//
//   .deb      a debconf question (templates, config): apt asks it before unpacking anything, dpkg -i in preinst; the
//             package installs only when it is accepted. Unattended (debconf's noninteractive frontend, where the
//             question cannot be shown) it is accepted, as msiexec /quiet and macOS's installer accept it. The answer is
//             kept, so an upgrade asks again only when the text changed (the question is named by the text's hash);
//             purge forgets it. The text is also the package's /usr/share/doc/<pkg>/copyright, as Debian keeps it.
//   .tar.gz   install.sh shows license.txt and asks [y/N], as Setup.exe does; --accept-license accepts it.

using System.Security.Cryptography;
using System.Text;

namespace WpfWebGpu.Packager.Desktop;

public static partial class LinuxPackager
{
    /// <summary>The debconf question the license is: the package's, named by the text so a changed text is asked again.</summary>
    internal static string LicenseQuestion(string pkg, string text) =>
        $"{pkg}/license-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12]}";

    /// <summary>The .deb's templates: the license as a yes/no question, its text the question's long description.</summary>
    internal static string LicenseTemplates(string question, string product, string text)
    {
        var sb = new StringBuilder();
        sb.Append($"Template: {question}\nType: boolean\nDefault: false\n");
        sb.Append($"Description: Do you accept the terms of the license agreement of {product}?\n");
        // a description's lines: one space and the text (rewrapped as a paragraph), " ." an empty line, and a line of
        // the text's own indentation kept as it is (two spaces or more); a list's item (1. a) - *) a paragraph of its
        // own, not run into the line before
        bool blank = true;
        foreach (string line in text.Replace("\t", "    ").Split('\n'))
        {
            if (line.Trim().Length == 0) { sb.Append(" .\n"); blank = true; continue; }
            if (!blank && s_listItem.IsMatch(line)) sb.Append(" .\n");
            sb.Append(' ').Append(line.TrimEnd()).Append('\n');
            blank = false;
        }
        sb.Append(" .\n").Append($" {product} is installed only if you accept the agreement.\n");
        return sb.ToString();
    }

    private static readonly System.Text.RegularExpressions.Regex s_listItem =
        new(@"^(\d{1,3}[.)]|[a-zA-Z][.)]|\(\w{1,4}\)|[-*\u2022])\s");

    // asks the question where it was not answered yet (debconf's confmodule sourced): accepted where it cannot be shown
    // (the noninteractive frontend: db_input's 30, unattended)
    private static string LicenseAsk(string question) => $$"""
        Q={{question}}
        db_get $Q || RET=
        if [ "$RET" != true ]; then
          db_fget $Q seen || RET=
          if [ "$RET" != true ]; then
            rc=0; db_input critical $Q || rc=$?
            if [ "$rc" = 30 ]; then db_set $Q true; fi
            db_go || true
          fi
        fi
        """;

    /// <summary>The .deb's config: apt asks the question before it unpacks anything (preinst decides).</summary>
    internal static string LicenseConfig(string question) => $"""
        #!/bin/sh
        # the license agreement, asked before the package is unpacked; preinst installs nothing unless it was accepted
        . /usr/share/debconf/confmodule
        {LicenseAsk(question)}
        exit 0

        """;

    /// <summary>The start of the .deb's preinst: the question asked where apt did not (dpkg -i), and nothing installed
    /// unless it was accepted.</summary>
    internal static string LicensePreInst(string question, string product) => $$"""
        if [ "$1" = install ] || [ "$1" = upgrade ]; then
          . /usr/share/debconf/confmodule
          # dpkg -i of the package, not configured before: its question from its templates
          db_get {{question}} >/dev/null 2>&1 || db_x_loadtemplatefile "$(dirname "$0")/templates" "${DPKG_MAINTSCRIPT_PACKAGE:-{{question.Split('/')[0]}}}" || true
        {{Indent(LicenseAsk(question), "  ")}}
          db_get $Q || RET=
          if [ "$RET" != true ]; then
            # asked again the next time
            db_fset $Q seen false || true
            db_stop
            echo "{{product}} was not installed: its license agreement was not accepted." >&2
            exit 1
          fi
          db_stop
        fi
        """;

    /// <summary>In the .deb's postrm on purge: the answer forgotten.</summary>
    internal static string LicensePurge(string pkg) => $"""
          command -v debconf-communicate >/dev/null 2>&1 && echo PURGE | debconf-communicate {pkg} >/dev/null 2>&1 || true

        """;

    /// <summary>The start of install.sh: license.txt shown and accepted, before anything is asked of sudo.</summary>
    internal static string LicenseInstallStep(string product) => $$"""
        ACCEPTED=; for a in "$@"; do [ "$a" = --accept-license ] && ACCEPTED=1; done
        if [ -z "$ACCEPTED" ]; then
          LICENSE="$(dirname "$0")/license.txt"
          if [ -t 0 ] && [ -t 1 ] && command -v more >/dev/null 2>&1; then more "$LICENSE"; else cat "$LICENSE"; fi
          printf '\nDo you accept the terms of the license agreement of %s? [y/N] ' "{{product}}"
          read answer || answer=
          case "$answer" in
            [Yy]*) ;;
            *) echo "Cancelled: {{product}} is installed only if you accept its license agreement (or with --accept-license)."; exit 1 ;;
          esac
          set -- --accept-license "$@"
        fi

        """;
}
