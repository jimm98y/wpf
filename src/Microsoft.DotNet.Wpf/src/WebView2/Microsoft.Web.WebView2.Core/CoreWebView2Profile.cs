// The browser profile surface of the real package (CoreWebView2.Profile and the types it brings in),
// for applications built against it. One profile per environment here, named after its user data
// folder; what it controls that the engine has (colour scheme) is honoured where the backend can, the
// rest is kept.

using System;
using System.IO;
using System.Threading.Tasks;

namespace Microsoft.Web.WebView2.Core
{
    public enum CoreWebView2PreferredColorScheme
    {
        Auto = 0,
        Light = 1,
        Dark = 2,
    }

    public enum CoreWebView2MemoryUsageTargetLevel
    {
        Normal = 0,
        Low = 1,
    }

    [Flags]
    public enum CoreWebView2ReleaseChannels
    {
        None = 0,
        Stable = 1,
        Beta = 2,
        Dev = 4,
        Canary = 8,
    }

    public enum CoreWebView2ChannelSearchKind
    {
        MostStable = 0,
        LeastStable = 1,
    }

    /// <summary>A custom URL scheme to register with the environment.</summary>
    public class CoreWebView2CustomSchemeRegistration
    {
        public CoreWebView2CustomSchemeRegistration(string schemeName)
        {
            SchemeName = schemeName ?? throw new ArgumentNullException(nameof(schemeName));
        }

        public string SchemeName { get; }
        public bool TreatAsSecure { get; set; }
        public bool HasAuthorityComponent { get; set; }
        public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
    }

    /// <summary>The profile a CoreWebView2 browses with.</summary>
    public class CoreWebView2Profile
    {
        internal CoreWebView2Profile(string userDataFolder)
        {
            ProfilePath = string.IsNullOrEmpty(userDataFolder) ? string.Empty : Path.Combine(userDataFolder, "EBWebView", "Default");
            DefaultDownloadFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        public string ProfileName => "Default";
        public bool IsInPrivateModeEnabled => false;
        public string ProfilePath { get; }
        public string DefaultDownloadFolderPath { get; set; }
        public CoreWebView2PreferredColorScheme PreferredColorScheme { get; set; }
        public bool IsPasswordAutosaveEnabled { get; set; }
        public bool IsGeneralAutofillEnabled { get; set; } = true;

        public Task ClearBrowsingDataAsync() => Task.CompletedTask;
    }

    public partial class CoreWebView2
    {
        private CoreWebView2Profile _profile;

        public CoreWebView2Profile Profile => _profile ??= new CoreWebView2Profile(null);

        public CoreWebView2MemoryUsageTargetLevel MemoryUsageTargetLevel { get; set; }

        /// <summary>Suspended means: not running script or timers while hidden. Hidden web views here already idle.</summary>
        public bool IsSuspended { get; private set; }

        public Task<bool> TrySuspendAsync()
        {
            IsSuspended = true;
            return Task.FromResult(true);
        }

        public void Resume() => IsSuspended = false;
    }
}
