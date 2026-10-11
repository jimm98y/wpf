// The identity itself. GenerateAssemblyInfo is off (the forwards file is generated and this project
// compiles nothing else), so the version has to be stated rather than derived from the property.

using System.Reflection;

[assembly: AssemblyVersion("4.0.0.0")]
// The FILE version, which is not part of the identity and is read by one thing only: the SDK's
// package-conflict resolution. The platform ships a System.Drawing facade with the same ASSEMBLY
// version as ours (4.0.0.0, the Framework identity), and on that tie the resolver keeps whichever
// has the higher file version -- so at 4.0.0.0 ours was dropped from the app's closure, the app
// bound to the platform's, and every System.Drawing type came from System.Drawing.Common instead.
// This has to outrank whatever the installed runtime carries, now and later.
[assembly: AssemblyFileVersion("99.0.0.0")]
[assembly: AssemblyTitle("System.Drawing")]
[assembly: AssemblyDescription("Forwards the System.Drawing identity to the assembly that implements it.")]
