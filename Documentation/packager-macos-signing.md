# Signing the macOS package

The Packager signs a macOS installation the way `codesign` and `productsign` do, in managed code. A signed `.pkg`
is made the same way on a Mac, on Windows, and on Linux, with nothing installed.

| Manifest (`signing.`) | What it signs | Its password |
|---|---|---|
| `developerIdCertificate` | Every Mach-O file of the installation (the .NET runtime, wpfrun, an app's own native libraries) | `WPFPKG_DEVELOPERID_PASSWORD` |
| `developerIdInstaller` | The package itself | `WPFPKG_DEVELOPERID_INSTALLER_PASSWORD` |

Each is a `.p12`: the "Developer ID Application" or "Developer ID Installer" certificate with its private key. To
get one, export it from Keychain Access (the certificate with its key, as Personal Information Exchange). Passwords
are never stored in the manifest.

## What is written

- **Code.** Each Mach-O file is signed with the hardened runtime and a secure timestamp from Apple's server
  (`timestamp.apple.com`), as notarization requires. The signature has codesign's layout: CodeDirectory version
  0x20500, with the SDK the code was built with as its runtime version. Programs get the entitlements .NET needs
  under the hardened runtime: JIT, unsigned executable memory, libraries not signed by the publisher, and dyld
  environment variables. A universal file is signed slice by slice. Microsoft's own signatures on the runtime are
  replaced, as `codesign --force` does.
- **The package.** The xar's table of contents is signed as productsign signs it: an RSA signature of its
  checksum, and a CMS signature with Apple's timestamp. The certificates go into the table of contents.
- **Not signed: the app stubs.** These are `/Applications/<Product>/<App>.app`, shell scripts that start the signed
  runtime. A script's signature is kept in extended attributes, which a package's payload does not carry. Installer
  does not quarantine what it installs, so Gatekeeper does not check them.

Signing needs a connection to Apple's timestamp server. An unsigned package needs nothing.

## Notarization

`signing.notaryKey` (an App Store Connect API key, `AuthKey_<id>.p8`, with `notaryKeyId` and `notaryIssuer`)
has Apple notarize the signed package. The ticket is then stapled to the package. This is done by Apple's
`notarytool` and `stapler`, so only on a Mac. Elsewhere, leave `notaryKey` out: the package is signed but not
notarized.

## Checking a package

```
pkgutil --check-signature Product-1.0.0-macos-arm64.pkg      the package's signature and timestamp
pkgutil --expand-full Product-1.0.0-macos-arm64.pkg x        then, for each program and library:
codesign --verify --strict -vv x/component.pkg/Payload/...
spctl -a -vv -t install Product-1.0.0-macos-arm64.pkg        Gatekeeper's verdict (needs Apple's certificates)
```
