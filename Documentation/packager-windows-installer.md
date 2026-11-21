# The Packager's Windows installers

The Windows target writes two installers for each Windows architecture there is a Setup pack for (`win-arm64`,
`win-x64`). Both install the same thing: the installation's files under `Program Files\<Product>`, its ProgramData,
its registry, its services and its Start menu entries. Every program runs on the stack's runtime.

| | `<Product>-<version>-windows-<arch>-Setup.exe` | `<Product>-<version>-windows-<arch>.msi` |
|---|---|---|
| What it is | Our own setup program with the installation appended to it | A Windows Installer package |
| For | Installing by hand: it asks its questions in a console window | Installing by hand, with Windows Installer's wizard; deployment tools (Group Policy, Intune, Configuration Manager), `msiexec /qn`, repair and rollback |
| SQL Server | Brings SQL Server Express and installs it, unless told not to | Not included: install it first (see [SQL Server](#sql-server)) |
| Uninstall | Apps & features, or `uninstall.exe /uninstall` | Apps & features, or `msiexec /x <product code>` |

The manifest's `output.windowsFormats` chooses which ones are written: `["setup", "msi"]` by default.

Both are written in managed code (no WiX, no signtool), so they can be built on macOS and Linux as well as Windows.

## The wizard

Opened by double-click, the MSI shows the standard installer wizard:

| | Pages |
|---|---|
| First install | Welcome → License Agreement → Destination Folder (with **Change...**, a folder browser) → Ready to Install → progress → Completed |
| Upgrade | Welcome ("will update … to version …") → License Agreement → Ready to Install. The folder stays the one installed to, since the installation's settings and data point there. |
| Already installed | **Repair** or **Remove** → Ready to Repair / Ready to Remove → progress → Completed. Ready to Remove has **Keep the settings and data**, which is `KEEPDATA=1`. |

The other pages are the usual ones: the "are you sure" box when Cancel is clicked, the error box, out of disk space,
and the pages for an installation that was interrupted or ended by an error. Windows asks for an administrator's
consent (UAC) when Install is clicked.

- **The license:** the License Agreement page is there when the manifest's `product.license` names a file, relative
  to the manifest. Use an `.rtf` as it is, or a text file, which is shown as plain text. Next stays disabled until
  the license is accepted.
- **The pictures:** the white banner and the welcome page's band carry the product's icon: `product.icon`, else the
  first window app's.
- **The folder installed to** is kept in `HKLM\SOFTWARE\<id>\InstallDir`. An upgrade finds it there, and
  `msiexec /i … INSTALLDIR=D:\Apps\Product\ /qn` chooses one without the wizard.
- **Repair** writes the installation's files, shortcuts and registry values again, but not values or ProgramData
  files the machine already has. It stops the services and the programs first, then starts the services again.

## Installing and removing the MSI

```
msiexec /i Product-1.0.0-windows-arm64.msi /qn /l*v install.log      install, or upgrade
msiexec /x Product-1.0.0-windows-arm64.msi /qn                       remove everything
msiexec /x {product code} KEEPDATA=1 /qn                               remove it, but keep its data
```

- **Running apps:** apps still open when it is removed are ended first, including the copies of the apps that run from
  each user's cache.
- **Keeping data:** `KEEPDATA=1` keeps the ProgramData files and the registry values the installation added, for a
  later install. Without it, they are removed (see [Uninstalling](packager-uninstall.md)).

## What is in it

| Table | What it holds |
|---|---|
| `File`, `Component`, `Directory` | The installation folder: `app\`, the runtimes, `setup.json` and `wpfrun.env`, with one component per folder. Then ProgramData, with one component per file. |
| `Media` and cabinets | The files, MSZIP-compressed. The cabinets are inside the package when they fit in 1.5 GB; otherwise they are `<name>-1.cab`, `-2.cab`, … beside the package, and must stay with it. |
| `Registry` | The installation's registry, with one component per value. Install paths are written as `[WPFAPPDIR]` and `[WPFPROGRAMDATA]`, so they follow the folders chosen at install time. |
| `Shortcut`, `Icon` | A Start menu entry for each window app, started through `wpfrunw.exe`, with the app's own icon. |
| `Upgrade` | A newer package replaces an older one. A rebuild of the same version replaces it too, since every build has its own product code. An older package is refused: "A newer version of … is installed already". |
| `CustomAction`, `Binary` | Setup.exe, run as SYSTEM for what the tables cannot express (below). |

Never written over what the machine already has:
- ProgramData files and registry values are skipped where the machine already has them, so an upgrade keeps the
  settings a user changed.
- Windows Installer never removes them itself. The uninstall's clean-up removes only those the installation added, as
  the [record](packager-uninstall.md#what-the-windows-installer-records) lists them.

### The custom actions

They are the same code Setup.exe runs, called with `/msi <verb>`.

| When | What |
|---|---|
| Before the files are written | Records which ProgramData files, registry values and keys are new to the machine |
| After the registry is written | Registers the services with the command line `wpfrun prepare` makes for each one, grants their accounts access to their data, writes the registry values the Registry table cannot hold (REG_QWORD), and starts the automatic services |
| On rollback | Removes the services again |
| At the start of an uninstall | Stops the services and ends the installation's running programs. Then it removes the services, except during an upgrade. |
| At the end of an uninstall | Removes every user's caches and logs, whatever is left in the folder, and, unless `KEEPDATA=1`, the recorded data |

They log to `%WINDIR%\SystemTemp\<Product>-msi.log`. Windows Installer's own log is the one `/l*v` asks for.

## SQL Server

Windows Installer runs one installation at a time, and SQL Server Express's setup is a Windows Installer installation
of its own, so the MSI cannot carry it. When the manifest includes SQL Server, install the instance first:
- with Setup.exe, which installs it;
- or as a separate step of the deployment, using Microsoft's SQL Server Express setup with the instance name the
  installation expects (`sqlServer.instance`).

## Signing

With `signing.authenticodeCertificate` set, the package is signed with Windows Installer's digital signature, and so
are Setup.exe and the runtime's programs. Set `signing.timestampUrl` to add an RFC 3161 time stamp, which keeps the
signature valid after the certificate expires. External cabinets are not covered by the package's signature.

## Limits

- A single file is under 2 GB (Windows Installer's `File` table). A cabinet holds at most 65535 files and is kept
  under 1.6 GB of uncompressed data, so a large installation is split across several cabinets.
- The product version is `major.minor.build`, at most `255.255.65535`. A fourth field is kept, but Windows Installer
  ignores it when comparing versions.
- The wizard is in English.
