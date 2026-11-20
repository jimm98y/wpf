# Uninstalling what the Packager installs

Every desktop installer the Packager writes can be removed again. By default, an uninstall removes everything the
installation put on the machine; asked to, it keeps the installation's data (its ProgramData and registry settings)
for a later install. SQL Server is the exception: it stays, as on Windows (see [SQL Server](#sql-server)).

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
- **The SQL Server Browser** (macOS, Linux), the installation's: it answers UDP 1434 as Windows' SQL Server Browser
  does, so a connection string naming an instance (`(local)\SQLEXPRESS`) finds its port. It runs as `nobody`. Every
  installation's browser answers for every instance; the one holding the port answers.

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
- **WPE WebKit on Linux:** the packages the install added are removed by the package manager that added them (apt,
  dnf, zypper or pacman), where no other installation's WPE source is left. That means WPE itself, Debian's keyring,
  and the libraries pinned for it; packages the machine had before are not touched.
- **The inotify limits** (`/etc/sysctl.d/60-<product>.conf`) on Linux.

## SQL Server

An installation that brings SQL Server brings an instance of the name its configuration gives (`SQLEXPRESS2025`,
say). There is one instance of a name on a machine, shared by every installation that names it, as on Windows: a second
product's setup finds the instance there and uses it. Uninstalling an installation leaves the instance and its
databases, as uninstalling a product on Windows leaves the SQL Server Express it brought. The instance has an
uninstall of its own:

| | The instance | Removed by |
|---|---|---|
| Windows | Microsoft's SQL Server Express, the named instance | its entry in Apps & features (Microsoft SQL Server) |
| macOS | the daemon `com.wpfwebgpu.sqlserver.<instance>`, running the container `sqlserver-<instance>` in Apple's `container`; its files in `/Library/Application Support/WpfWebGpu/SqlServer/<INSTANCE>/` | `Uninstall SQL Server.app` beside each installation's own uninstaller in `/Applications/<Product>/`; `Uninstall SQL Server <INSTANCE>.app` in that folder, next to what it removes (it stays when the installations have gone); or `sudo '<that folder>/uninstall.sh'` |
| Linux | the package `wpfwebgpu-sqlserver-<instance>` (the unit, the container `sqlserver-<instance>` in Podman), its password, port and volume in `/etc/wpfwebgpu/sqlserver/<instance>.env` | `apt remove` (the databases kept) or `apt purge`; from the `.tar.gz`, `/etc/wpfwebgpu/sqlserver/<instance>-uninstall.sh` |

- **The port** is the one the installation's configuration gives (1433) where it is free, else the next free one, as a
  named instance on Windows takes a port of its own. Clients naming the instance find it through the SQL Server
  Browser; the port is in the instance's `sqlserver.txt` (macOS, readable by administrators) or `.env` (Linux, root).
- **Its own uninstall** removes the databases and the password too, unless `--keep-data` is given (on Linux, unless
  it is an `apt remove`). The image goes where no other instance needs it, and on macOS Apple's `container` goes too,
  where nothing else uses it (no other instance, no container, no image left).
- **Earlier versions** brought an installation's own SQL Server (`<id>-sqlserver` on macOS, `<pkg>-sqlserver` on
  Linux). Upgrading takes it over as the instance, with its password and databases.

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
