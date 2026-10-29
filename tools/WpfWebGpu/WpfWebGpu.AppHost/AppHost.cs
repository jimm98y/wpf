// The entry point of every app wpfrun runs. The app's own entry assembly is loaded by name, so the startup
// hook's loader -- which rewrites the assemblies the launcher chose as they load (P/Invokes, paths, COM) --
// takes it like any other; it is made the process's entry assembly, as when its own .exe starts it, and its
// Main is called with the arguments, its exceptions and exit code its own.

using System.Reflection;
using System.Runtime.Loader;

internal static class AppHost
{
    // The apartment WPF and WinForms want on Windows; the app's Main asks for it the same way.
    [STAThread]
    private static int Main(string[] args)
    {
        string name = AppContext.GetData("WpfWebGpu.EntryAssembly") as string
            ?? throw new InvalidOperationException("wpfrun: no app to run (WpfWebGpu.EntryAssembly is not set)");
        Assembly app = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name));
        Assembly.SetEntryAssembly(app);
        MethodInfo main = app.EntryPoint ?? throw new InvalidOperationException($"wpfrun: {name} has no entry point");
        object?[]? arguments = main.GetParameters().Length == 1 ? new object[] { args } : null;
        // Not wrapped in a TargetInvocationException: an exception Main does not handle is the process's
        // unhandled exception, as it is when the app is started directly.
        object? result = main.Invoke(null, BindingFlags.DoNotWrapExceptions, null, arguments, null);
        return result switch
        {
            int code => code,
            Task<int> task => task.GetAwaiter().GetResult(),
            Task task => Await(task),
            _ => 0,
        };
    }

    private static int Await(Task task)
    {
        task.GetAwaiter().GetResult();
        return 0;
    }
}
