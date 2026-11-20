# Uninstalling what the Packager installs

Every desktop installer the Packager writes can be removed again, in two ways:
- **Ordinary uninstall:** keeps the installation's data, as Windows keeps an application's ProgramData and registry
  settings, so a later install finds them.
- **Full uninstall:** removes everything the installation put on the machine.

Phone, tablet and headset apps live in their sandbox, and the system removes them with their data.

| | Uninstall, data kept | Everything |
|---|---|---|
| **Windows** (`Setup.exe`) | Apps & features, or `uninstall.exe /uninstall`: asks whether the data goes too | `uninstall.exe /uninstall /all` (`/quiet` for no questions) |
| **macOS** (`.pkg`) | `/Applications/<Product>/Uninstall <Product>.app`, Keep Data | the same, Remove Everything, or `sudo "/Library/Application Support/<Product>/uninstall.sh" --all` |
| **Linux** (`.deb`) | `apt remove <pkg>` | `apt purge <pkg>` |
| **Linux** (`.tar.gz`) | `sudo /opt/<Product>/uninstall.sh` | `sudo /opt/<Product>/uninstall.sh --all` |

## Every uninstall

- **The services:**
  - Those the installation registered, and those its programs created since: on Windows, every service whose program
    is in the installation's folder; on Linux, its `<pkg>-*.service` units in `/etc/systemd/system`.
  - They are stopped and removed.
- **Programs still running** from the installation are ended. On Windows, a file still in use goes when Windows
  restarts, rather than the uninstall stopping half done.
- **The installation's own files and entries:** its files, its shortcuts (Start menu entries, `.app` stubs, `.desktop`
  entries and their icon caches), its Apps & features entry, its pkgutil receipt, and Linux's masked units' masks.
- **Caches and logs:** every user's, and the machine's.
  - The launcher of each installation reads `runtime/launcher/wpfrun.env`, which names a folder of the product's own
    (`%LOCALAPPDATA%\<Product>`, `~/Library/Caches/<Product>`). What it starts writes there, not into wpfrun's shared
    folders, so the uninstaller finds it.
  - On macOS, the startup agent is booted out of every logged-in session.
- **SQL Server's container** (macOS, Linux) is removed. Its databases stay.

## The full uninstall, besides

- **The machine's state:**
  - **Windows:** the registry values the installation set and the keys it made, the ProgramData files it wrote and the
    folders it made, and the access rules it added for the service accounts.
  - **macOS and Linux:** `state/`, the stand-in registry and ProgramData, with the http.sys reservations kept there.
- **The service accounts** it made on macOS and Linux (`_wpfnetworkservice`, `wpf-networkservice` and the others),
  where no other installation's service still runs as one. They stay on an ordinary uninstall: the state they own
  stays too.
  - **macOS** deletes a user record only for a process with Full Disk Access, or one using an administrator's own
    credentials. Root alone, the installer's scripts included, gets `eDSPermissionError` (checked on macOS 26).
    `uninstall.sh --all` run with sudo in a terminal asks for the administrator's password (`sysadminctl
    -deleteUser`). Otherwise it leaves the accounts, hidden, of no password and no shell, and prints the command
    that removes them.
- **SQL Server:**
  - **Windows:** the instance the installation installed is uninstalled by SQL Server's own setup, with its databases.
    An instance that was there before is left alone.
  - **macOS:** the databases' volume and the image go. Apple's `container` goes too, where nothing else uses it (no
    other installation's database, no container, no image left).
  - **Linux:** the volume, the image and the password go. The SQL Server package depends on the installation's, so
    `apt remove` or `apt purge` of the installation takes it along.
- **WPE WebKit on Linux:** the packages the install added are removed by the package manager that added them (apt,
  dnf, zypper or pacman), where no other installation's WPE source is left. That means WPE itself, Debian's keyring,
  and the libraries pinned for it; packages the machine had before are not touched.
- **The inotify limits** (`/etc/sysctl.d/60-<product>.conf`) on Linux.

### What the Windows installer records

A record of what it put on the machine is kept in `%ProgramData%\WpfWebGpu\installations\<id>.json`. It outlives an
ordinary uninstall, so a full one after a reinstall still knows what the first install made. An upgrade's record grows
and never claims what the machine had before it.

## Left on purpose

- On Windows, values and files that were on the machine before the installation, and HKEY_CURRENT_USER values of a
  user whose profile is not loaded.
- On macOS, each user's data protection key in the login keychain ("WpfWebGpu Data Protection"): it is every
  installation's, as Windows' DPAPI keys outlive any one application, and removing it would leave other installations'
  protected data unreadable. Rosetta stays too.
- On Linux, Podman, which the SQL Server package depends on: `apt autoremove` takes it once nothing needs it.
