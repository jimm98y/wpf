using System.Reflection;
using System.Runtime.CompilerServices;

namespace LocationAskerFixture
{
    public static class Asker
    {
        /// <summary>Where the caller is, as IcoTrace's AssembliesLocation asks it.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static string CallersCodeBase() => Assembly.GetCallingAssembly().CodeBase ?? "";
    }
}
