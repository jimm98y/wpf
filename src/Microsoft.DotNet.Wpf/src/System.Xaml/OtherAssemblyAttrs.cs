// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

//
// This file specifies various assembly level attributes.
//

using System.Runtime.CompilerServices;
using System.Windows.Markup;

[assembly:Dependency("mscorlib,", LoadHint.Always)]
[assembly:Dependency("System,", LoadHint.Always)]
[assembly:Dependency("System.Xml,", LoadHint.Sometimes)]

// NOTE: XamlAccessLevel is now implemented in this assembly (System/Xaml/Permissions/XamlAccessLevel.cs)
// instead of being forwarded to the Windows-only System.Windows.Extensions, so XAML with internal-type
// access loads cross-platform. XamlLoadPermission (obsolete, unused off-Windows) stays forwarded.
#pragma warning disable SYSLIB0003 // Type or member is obsolete
[assembly: TypeForwardedTo(typeof(System.Xaml.Permissions.XamlLoadPermission))]
#pragma warning restore SYSLIB0003 // Type or member is obsolete
[assembly: TypeForwardedTo(typeof(System.Windows.Markup.ValueSerializerAttribute))]

[assembly:XmlnsDefinition("http://schemas.microsoft.com/winfx/2006/xaml", "System.Windows.Markup")]
