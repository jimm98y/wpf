// The schema of IIS's configuration, as the configuration system answers it (IAppHostElementSchema and the rest): what
// each element of a section holds -- its attributes with their types and defaults, its child elements, the collection it
// is (the names of the elements it adds, removes and clears) -- so that a client (Microsoft.Web.Administration) reads an
// attribute as its type, creates a collection's element by its name, and knows an element's key.
//
// Windows keeps it in inetsrv\config\schema (IIS_schema.xml and the rest); this is our own account of the sections an
// installation's programs configure: the sites with their bindings, applications and virtual directories, the
// application pools, and the configuration's redirection. Any other section is read without a schema: its attributes as
// strings, an element's repeated children as a collection of add, remove and clear.

namespace WpfWebGpu.IisAdmin;

internal sealed class PropertyDef
{
    public string Name = "";
    /// <summary>IIS's type name: string, bool, uint, int, int64, enum, flags, timeSpan.</summary>
    public string Type = "string";
    public object? Default;
    public bool Required, UniqueKey, CombinedKey, CaseSensitive, Expanded, Encrypted;
    /// <summary>An enum's or a flags' names and values.</summary>
    public (string Name, uint Value)[] Values = Array.Empty<(string, uint)>();
    public string TimeSpanFormat = "string";
    /// <summary>An attribute the configuration system answers rather than the file (a binding's certificate: http.sys's).</summary>
    public Func<AppHostElement, object?>? Computed;

    /// <summary>The type's default where the schema names none: as IIS's configuration answers it.</summary>
    public object DefaultValue => Default ?? Type switch
    {
        "bool" => false,
        "uint" or "enum" or "flags" => 0u,
        "int" => 0,
        "int64" => 0L,
        "timeSpan" => 0UL,
        _ => "",
    };
}

internal sealed class CollectionDef
{
    /// <summary>The element names it adds (IIS lists them comma-separated), each with its schema.</summary>
    public readonly List<ElementDef> Add = new();
    public ElementDef? Remove, Clear;
    public bool AllowDuplicates, MergeAppend = true;
    public string AddNames => string.Join(",", Add.Select(a => a.Name));
}

/// <summary>A configuration method of an element (IIS's schema's &lt;method&gt;): its input and output, and what it does.</summary>
internal sealed class MethodDef
{
    public string Name = "";
    public ElementDef Input = new() { Name = "input" };
    public ElementDef Output = new() { Name = "output" };
    public Action<AppHostElement, AppHostElement, AppHostElement> Execute = (_, _, _) => { };
}

internal sealed class ElementDef
{
    public string Name = "";
    public readonly List<MethodDef> Methods = new();
    public readonly List<PropertyDef> Properties = new();
    public readonly List<ElementDef> Children = new();
    public CollectionDef? Collection;
    /// <summary>Attributes and children the schema does not list are kept, as strings (a section read without schema).</summary>
    public bool AllowUnschematized;
    public bool IsCollectionDefault;

    public PropertyDef? Property(string name) => Properties.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public ElementDef? Child(string name) => Children.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    public ElementDef? AddElement(string name) => Collection?.Add.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A schema for an element the schema does not know: its attributes as strings, its children likewise, and
    /// an element whose children are add, remove and clear a collection of them.</summary>
    public static ElementDef Unschematized(string name)
    {
        var e = new ElementDef { Name = name, AllowUnschematized = true };
        return e;
    }

    public static ElementDef UnschematizedCollection(string name)
    {
        var e = Unschematized(name);
        e.Collection = new CollectionDef
        {
            AllowDuplicates = false,
            Remove = Unschematized("remove"),
            Clear = Unschematized("clear"),
        };
        e.Collection.Add.Add(Unschematized("add"));
        return e;
    }
}

/// <summary>The sections this configuration system knows the schema of.</summary>
internal static class IisSchema
{
    private static readonly Dictionary<string, ElementDef> s_sections = Build();

    /// <summary>A section's schema by its path (system.applicationHost/sites); null where it is read without one.</summary>
    public static ElementDef? Section(string path) => s_sections.TryGetValue(path, out var e) ? e : null;

    // ---- the definitions

    private static PropertyDef P(string name, string type = "string", object? def = null, bool required = false, bool key = false,
        bool combined = false, bool expanded = false, bool encrypted = false, (string, uint)[]? values = null) => new()
        {
            Name = name, Type = type, Default = def, Required = required, UniqueKey = key, CombinedKey = combined,
            Expanded = expanded, Encrypted = encrypted, Values = values ?? Array.Empty<(string, uint)>(),
        };

    private static ulong Time(int h, int m, int s) => (ulong)new TimeSpan(h, m, s).Ticks;

    private static ElementDef E(string name, IEnumerable<PropertyDef>? properties = null, IEnumerable<ElementDef>? children = null, CollectionDef? collection = null)
    {
        var e = new ElementDef { Name = name, Collection = collection };
        if (properties != null) e.Properties.AddRange(properties);
        if (children != null) e.Children.AddRange(children);
        return e;
    }

    private static CollectionDef C(ElementDef add, bool clear = false, bool remove = false, bool duplicates = false)
    {
        var c = new CollectionDef { AllowDuplicates = duplicates };
        c.Add.Add(add);
        if (clear) c.Clear = E("clear");
        if (remove) c.Remove = E("remove");
        return c;
    }

    private static readonly (string, uint)[] s_sslFlags =
    {
        ("None", 0), ("Sni", 1), ("CentralCertStore", 2), ("DisableHTTP2", 4), ("DisableOCSPStapling", 8), ("DisableQUIC", 16),
        ("DisableTLS13", 32), ("DisableLegacyTLS", 64),
    };

    private static readonly (string, uint)[] s_logonMethod = { ("Interactive", 0), ("Batch", 1), ("Network", 2), ("ClearText", 3) };

    private static IEnumerable<PropertyDef> VirtualDirectoryProperties(bool withKey) => new[]
    {
        withKey ? P("path", required: true, key: true) : null,
        withKey ? P("physicalPath", required: true, expanded: true) : P("physicalPath", expanded: true),
        P("userName"), P("password", encrypted: true),
        P("logonMethod", "enum", 3u, values: s_logonMethod),
        P("allowSubDirConfig", "bool", true),
    }.Where(p => p != null)!;

    private static IEnumerable<PropertyDef> ApplicationProperties(bool withKey) => new[]
    {
        withKey ? P("path", required: true, key: true) : null,
        P("applicationPool"), P("enabledProtocols", def: "http"),
        P("serviceAutoStartEnabled", "bool", false), P("serviceAutoStartProvider"), P("preloadEnabled", "bool", false),
    }.Where(p => p != null)!;

    private static ElementDef Limits() => E("limits", new[]
    {
        P("maxBandwidth", "uint", 4294967295u), P("maxConnections", "uint", 4294967295u),
        P("connectionTimeout", "timeSpan", Time(0, 2, 0)), P("maxUrlSegments", "uint", 32u),
    });

    private static ElementDef LogFile() => E("logFile", new[]
    {
        P("logExtFileFlags", "flags", 2478031u, values: new (string, uint)[]
        {
            ("Date", 1), ("Time", 2), ("ClientIP", 4), ("UserName", 8), ("SiteName", 16), ("ComputerName", 32), ("ServerIP", 64),
            ("Method", 128), ("UriStem", 256), ("UriQuery", 512), ("HttpStatus", 1024), ("Win32Status", 2048), ("BytesSent", 4096),
            ("BytesRecv", 8192), ("TimeTaken", 16384), ("ServerPort", 32768), ("UserAgent", 65536), ("Cookie", 131072),
            ("Referer", 262144), ("ProtocolVersion", 524288), ("Host", 1048576), ("HttpSubStatus", 2097152),
        }),
        P("customLogPluginClsid"),
        P("logFormat", "enum", 2u, values: new (string, uint)[] { ("IIS", 0), ("NCSA", 1), ("W3C", 2), ("Custom", 3) }),
        P("logTargetW3C", "flags", 1u, values: new (string, uint)[] { ("File", 1), ("ETW", 2) }),
        P("directory", def: @"%SystemDrive%\inetpub\logs\LogFiles", expanded: true),
        P("period", "enum", 1u, values: new (string, uint)[] { ("MaxSize", 0), ("Daily", 1), ("Weekly", 2), ("Monthly", 3), ("Hourly", 4) }),
        P("truncateSize", "int64", 20971520L), P("localTimeRollover", "bool", false), P("enabled", "bool", true),
        P("logSiteId", "bool", true), P("flushByEntryCountW3CLog", "uint", 0u), P("maxLogLineLength", "uint", 65536u),
    });

    private static ElementDef TraceFailedRequestsLogging() => E("traceFailedRequestsLogging", new[]
    {
        P("enabled", "bool", false), P("directory", def: @"%SystemDrive%\inetpub\logs\FailedReqLogFiles", expanded: true),
        P("maxLogFiles", "uint", 50u), P("maxLogFileSizeKB", "uint", 1024u), P("customActionsEnabled", "bool", false),
    });

    private static Dictionary<string, ElementDef> Build()
    {
        var virtualDirectory = E("virtualDirectory", VirtualDirectoryProperties(withKey: true));
        var application = E("application", ApplicationProperties(withKey: true),
            new[] { E("virtualDirectoryDefaults", VirtualDirectoryProperties(withKey: false)) },
            C(virtualDirectory, clear: false));
        var binding = E("binding", new[]
        {
            P("protocol", required: true, combined: true),
            P("bindingInformation", required: true, combined: true),
            P("sslFlags", "flags", 0u, values: s_sslFlags),
            // http.sys's: the certificate the binding's address and port (or host name) is served with
            new PropertyDef { Name = "certificateHash", Computed = e => SslBindings.Read(e)?.Hash },
            new PropertyDef { Name = "certificateStoreName", Computed = e => SslBindings.Read(e)?.Store },
        });
        binding.Methods.Add(new MethodDef
        {
            Name = "AddSslCertificate",
            Input = E("input", new[] { P("certificateHash", required: true), P("certificateStoreName", def: "MY") }),
            Execute = (element, input, _) => SslBindings.Add(element, (string)input.GetPropertyByName("certificateHash").get_Value(),
                (string)input.GetPropertyByName("certificateStoreName").get_Value()),
        });
        binding.Methods.Add(new MethodDef { Name = "RemoveSslCertificate", Execute = (element, _, _) => SslBindings.Remove(element) });
        var bindings = E("bindings", collection: C(binding, clear: true));
        var siteProperties = new[] { P("name", required: true, key: true), P("id", "uint", required: true), P("serverAutoStart", "bool", true) };
        var site = E("site", siteProperties,
            new[]
            {
                bindings, Limits(), LogFile(), TraceFailedRequestsLogging(),
                E("applicationDefaults", ApplicationProperties(withKey: false)),
                E("virtualDirectoryDefaults", VirtualDirectoryProperties(withKey: false)),
            },
            C(application));
        var sites = E("sites", children: new[]
        {
            E("siteDefaults", new[] { P("serverAutoStart", "bool", true) }, new[] { E("bindings", collection: C(E("binding", binding.Properties), clear: true)), Limits(), LogFile(), TraceFailedRequestsLogging() }),
            E("applicationDefaults", ApplicationProperties(withKey: false)),
            E("virtualDirectoryDefaults", VirtualDirectoryProperties(withKey: false)),
        }, collection: C(site, clear: true, remove: true));

        var identity = new (string, uint)[] { ("LocalSystem", 0), ("LocalService", 1), ("NetworkService", 2), ("SpecificUser", 3), ("ApplicationPoolIdentity", 4) };
        IEnumerable<ElementDef> PoolChildren() => new[]
        {
            E("processModel", new[]
            {
                P("identityType", "enum", 4u, values: identity), P("userName"), P("password", encrypted: true),
                P("loadUserProfile", "bool", true), P("setProfileEnvironment", "bool", true),
                P("logonType", "enum", 0u, values: new (string, uint)[] { ("LogonBatch", 0), ("LogonService", 1) }),
                P("manualGroupMembership", "bool", false), P("idleTimeout", "timeSpan", Time(0, 20, 0)),
                P("idleTimeoutAction", "enum", 0u, values: new (string, uint)[] { ("Terminate", 0), ("Suspend", 1) }),
                P("maxProcesses", "uint", 1u), P("shutdownTimeLimit", "timeSpan", Time(0, 1, 30)), P("startupTimeLimit", "timeSpan", Time(0, 1, 30)),
                P("pingingEnabled", "bool", true), P("pingInterval", "timeSpan", Time(0, 0, 30)), P("pingResponseTime", "timeSpan", Time(0, 1, 30)),
            }),
            E("recycling", new[] { P("disallowOverlappingRotation", "bool", false), P("disallowRotationOnConfigChange", "bool", false), P("logEventOnRecycle", "flags", 137u) },
                new[] { E("periodicRestart", new[] { P("memory", "uint", 0u), P("privateMemory", "uint", 0u), P("requests", "uint", 0u), P("time", "timeSpan", Time(29, 0, 0)) }) }),
            E("failure", new[] { P("rapidFailProtection", "bool", true), P("rapidFailProtectionInterval", "timeSpan", Time(0, 5, 0)), P("rapidFailProtectionMaxCrashes", "uint", 5u) }),
            E("cpu", new[] { P("limit", "uint", 0u), P("action", "enum", 0u, values: new (string, uint)[] { ("NoAction", 0), ("KillW3wp", 1), ("Throttle", 2), ("ThrottleUnderLoad", 3) }), P("resetInterval", "timeSpan", Time(0, 5, 0)) }),
        };
        PropertyDef[] PoolProperties(bool withKey) => new[]
        {
            withKey ? P("name", required: true, key: true) : null,
            P("queueLength", "uint", 1000u), P("autoStart", "bool", true), P("enable32BitAppOnWin64", "bool", false),
            P("managedRuntimeVersion", def: "v4.0"), P("managedRuntimeLoader", def: "webengine4.dll"), P("enableConfigurationOverride", "bool", true),
            P("managedPipelineMode", "enum", 0u, values: new (string, uint)[] { ("Integrated", 0), ("Classic", 1) }),
            P("CLRConfigFile"), P("passAnonymousToken", "bool", true),
            P("startMode", "enum", 0u, values: new (string, uint)[] { ("OnDemand", 0), ("AlwaysRunning", 1) }),
        }.Where(p => p != null).ToArray()!;
        var pool = E("add", PoolProperties(withKey: true), PoolChildren());
        var pools = E("applicationPools", children: new[] { E("applicationPoolDefaults", PoolProperties(withKey: false), PoolChildren()) },
            collection: C(pool, clear: true, remove: true));

        var redirection = E("configurationRedirection", new[] { P("enabled", "bool", false), P("path", expanded: true), P("userName"), P("password", encrypted: true) });

        return new Dictionary<string, ElementDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["system.applicationHost/sites"] = sites,
            ["system.applicationHost/applicationPools"] = pools,
            ["configurationRedirection"] = redirection,
        };
    }
}
