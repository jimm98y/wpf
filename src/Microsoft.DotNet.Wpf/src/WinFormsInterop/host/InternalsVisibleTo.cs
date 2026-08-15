// ElementHost (WindowsFormsIntegration) plugs a hosted WPF scene into this assembly's present path
// through EmbeddedScenes, and needs the driver's window/scale to place its HwndSource. That seam is
// internal on purpose: it is how the two halves of the stack meet, not public API.

using System.Runtime.CompilerServices;

// The public key is the ECMA one both assemblies are public-signed with: a strong-named assembly
// may only befriend a named public key, so this has to move in lockstep with SignAssembly there.
[assembly: InternalsVisibleTo("WindowsFormsIntegration, PublicKey=00000000000000000400000000000000")]

// The WinForms WebView2 control needs the same seam for the same reason ElementHost does, and needs
// it more acutely: a web engine must be parented to a REAL native window, and this driver's
// Control.Handle is a managed counter (XplatUIWebGpu mints them with next_handle++), not an HWND.
// EmbeddedScenes.HostWindow is the only real OS window in the process.
//
// Its public key is the WebView2 one -- these assemblies carry the real package's identity so an
// application's existing PackageReference binds to them -- so it differs from the ECMA key above.
[assembly: InternalsVisibleTo("Microsoft.Web.WebView2.WinForms, PublicKey=00240000048000009400000006020000002400005253413100040000010001005dba207a79aa19b084a6b574c6f8945bd211921eaefa27a8031dd74b31af8094b3945f00288bc93f0751ecc62d3f5ca42681f09ed4ff1716aefc600bee20b042828dd897db022a07459a20a1042d0fcdd4a19610e838f5cfd992bcad1499861e24a28e5884452ce638803b5fac55a9967d92794723d2f4a0b49cc1148ae03dc7")]
