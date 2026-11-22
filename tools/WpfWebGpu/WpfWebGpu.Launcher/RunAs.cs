// wpfrun as <user> [<group>] -- <program> [arguments]: the program run as an account, in a group, as launchd and systemd
// start a service under its account (UserName and GroupName, User= and Group=). The web server starts an application
// pool's worker so: its pool's account, in the group the installation's services run in, through which the machine's
// state is every account's. Root only: the groups, the group and the user set, in that order, then the program in this
// process's place (execv), with this process's environment.

using System.Runtime.InteropServices;

namespace WpfWebGpu;

internal static partial class RunAs
{
    public static int Run(string[] args)
    {
        int dash = Array.IndexOf(args, "--");
        if (dash < 1 || dash > 2 || dash == args.Length - 1)
            throw new UserError("usage: wpfrun as <user> [<group>] -- <program> [arguments]");
        string user = args[0];
        string? group = dash == 2 ? args[1] : null;
        string[] command = args[(dash + 1)..];

        if (getpwnam(user) is var pw && pw == IntPtr.Zero) throw new UserError($"as: no account {user}");
        // struct passwd: pw_name, pw_passwd (pointers), then pw_uid and pw_gid
        uint uid = (uint)Marshal.ReadInt32(pw, 2 * IntPtr.Size);
        uint gid = (uint)Marshal.ReadInt32(pw, 2 * IntPtr.Size + 4);
        if (group != null)
        {
            IntPtr gr = getgrnam(group);
            if (gr == IntPtr.Zero) throw new UserError($"as: no group {group}");
            // struct group: gr_name, gr_passwd (pointers), then gr_gid
            gid = (uint)Marshal.ReadInt32(gr, 2 * IntPtr.Size);
        }
        uint[] groups = { gid };
        if (setgroups((nint)groups.Length, groups) != 0 || setgid(gid) != 0 || setuid(uid) != 0)
            throw new UserError($"as: cannot become {user}{(group != null ? ":" + group : "")} (errno {Marshal.GetLastPInvokeError()}); root only");

        var argv = command.Select(Marshal.StringToCoTaskMemUTF8).Append(IntPtr.Zero).ToArray();
        execv(command[0], argv);
        throw new UserError($"as: {command[0]} could not be started (errno {Marshal.GetLastPInvokeError()})");
    }

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr getpwnam(string name);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr getgrnam(string name);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setgroups(nint size, uint[] list);   // size_t on Linux, int on macOS: either read whole

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setgid(uint gid);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int setuid(uint uid);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int execv(string path, IntPtr[] argv);
}
