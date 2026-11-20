# Uninstalling what the Packager installs

Every desktop installer the Packager writes can be removed again. By default, an uninstall removes everything the
installation put on the machine; asked to, it keeps the installation's data (its ProgramData and registry settings, and
SQL Server's databases) for a later install.

Phone, tablet and headset apps live in their sandbox, and the system removes them with their data.

| | Everything (the default) | Data kept |
|---|---|---|
| **Windows** (`Setup.exe`) | Apps & features, or `uninstall.exe /uninstall`: it asks, yes the default (`/quiet` for no questions) | `uninstall.exe /uninstall /keepdata` |
| **macOS** (`.pkg`) | `/Applications/<Product>/Uninstall <Product>.app`: Remove Everything, the default; or `sudo "/Library/Application Support/<Product>/uninstall.sh"` | the same app, Keep Data; or `uninstall.sh --keep-data` |
| **Linux** (`.deb`) | `apt purge <pkg>` | `apt remove <pkg>` (Debian's own distinction: remove keeps, purge removes all) |
| **Linux** (`.tar.gz`) | `sudo /opt/<Product>/uninstall.sh` | `sudo /opt/<Product>/uninstall.sh --keep-data` |

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

## The full uninstall (the default), besides

- **The machine's state:**
  - **Windows:** the registry values the installation set and the keys it made, the ProgramData files it wrote and the
    folders it made, and the access rules it added for the service accounts.
  - **macOS and Linux:** `state/`, the stand-in registry and ProgramData, with the http.sys reservations kept there.
- **The service accounts** it made on Linux (`wpf-networkservice`, `wpf-localservice`), where no other installation's
  service still runs as one. They stay when the data is kept: the state they own stays too.
  - **macOS** makes none. Its NETWORK SERVICE and LOCAL SERVICE services run as the system's own `nobody`, in the
    group `staff` that the state is writable by. A daemon standing for LOCAL SERVICE is told so in its environment
    (`WPFWEBGPU_SERVICE_ACCOUNT=LocalService`), and the stack gives it that Windows identity.
  - This is because macOS deletes a user record only for a process with Full Disk Access, or one using an
    administrator's own credentials. Root alone, the installer's scripts included, gets `eDSPermissionError`
    (checked on macOS 26), so an account an installer made could never be removed by its uninstaller.
  - Earlier versions made `_wpfnetworkservice` and `_wpflocalservice`. An upgrade gives what they owned of the
    installation to `nobody`. The uninstall deletes them where no daemon still runs as one and macOS lets it: in a
    terminal, `sysadminctl` asks for the administrator's password. Otherwise they are left hidden, of no password
    and no shell, and the command that removes them is printed.
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
uninstall that keeps the data, so a full one after a reinstall still knows what the first install made. An upgrade's record grows
and never claims what the machine had before it.

## Left on purpose

- On Windows, values and files that were on the machine before the installation, and HKEY_CURRENT_USER values of a
  user whose profile is not loaded.
- On macOS, each user's data protection key in the login keychain ("WpfWebGpu Data Protection"): it is every
  installation's, as Windows' DPAPI keys outlive any one application, and removing it would leave other installations'
  protected data unreadable. Rosetta stays too.
- On Linux, Podman, which the SQL Server package depends on: `apt autoremove` takes it once nothing needs it.
