// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The two control assemblies need the engine seam that lives here (IWebViewBackend,
// WebViewBackendFactory, WebViewHostWindow) in order to create and place an engine, but none of
// that is public API -- it is this fork's internal machinery, and the real
// Microsoft.Web.WebView2.Core exposes nothing equivalent. Friend access keeps it that way.
//
// The full public key, not just its token, is required: these assemblies are strong-named, and a
// strong-named assembly only accepts strong-named friends identified by their whole key.
//

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Microsoft.Web.WebView2.Wpf, PublicKey=00240000048000009400000006020000002400005253413100040000010001005dba207a79aa19b084a6b574c6f8945bd211921eaefa27a8031dd74b31af8094b3945f00288bc93f0751ecc62d3f5ca42681f09ed4ff1716aefc600bee20b042828dd897db022a07459a20a1042d0fcdd4a19610e838f5cfd992bcad1499861e24a28e5884452ce638803b5fac55a9967d92794723d2f4a0b49cc1148ae03dc7")]
[assembly: InternalsVisibleTo("Microsoft.Web.WebView2.WinForms, PublicKey=00240000048000009400000006020000002400005253413100040000010001005dba207a79aa19b084a6b574c6f8945bd211921eaefa27a8031dd74b31af8094b3945f00288bc93f0751ecc62d3f5ca42681f09ed4ff1716aefc600bee20b042828dd897db022a07459a20a1042d0fcdd4a19610e838f5cfd992bcad1499861e24a28e5884452ce638803b5fac55a9967d92794723d2f4a0b49cc1148ae03dc7")]
