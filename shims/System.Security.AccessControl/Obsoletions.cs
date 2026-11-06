// The runtime's obsoletion messages the sources (Runtime/) name, as dotnet/runtime's Common/src/System/Obsoletions.cs has them.
namespace System
{
    internal static class Obsoletions
    {
        internal const string SharedUrlFormat = "https://aka.ms/dotnet-warnings/{0}";
        internal const string LegacyFormatterImplMessage = "This API supports obsolete formatter-based serialization. It should not be called or extended by application code.";
        internal const string LegacyFormatterImplDiagId = "SYSLIB0051";
    }
}
