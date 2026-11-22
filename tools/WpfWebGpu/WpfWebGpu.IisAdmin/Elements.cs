// IIS's configuration as its clients read and write it (IAppHostElement and the rest), over the configuration file's XML:
// an element is an XML element of a section, its properties the element's attributes read as their schema's types (their
// defaults where the file does not set them), its child elements and its collection's items the XML element's children.
// An element the file does not have yet (a site's bindings before it has any) is still there, with its defaults, and is
// written into the file when it is first changed; an element a collection makes (CreateNewElement) is written where it
// is added. The file itself is written when the client commits (the admin manager's CommitChanges).

using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using WpfWebGpu.ComInterop;

namespace WpfWebGpu.IisAdmin;

internal static class HResults
{
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int ERROR_NOT_FOUND = unchecked((int)0x80070490);
    public const int ERROR_ALREADY_EXISTS = unchecked((int)0x800700B7);
    public const int ERROR_FILE_NOT_FOUND = unchecked((int)0x80070002);

    public static COMException Error(int hr, string message) => new(message, hr);

    /// <summary>A collection's index: a position, or a name.</summary>
    public static object Index(object cIndex) => cIndex switch
    {
        int i => i, uint u => (int)u, short s => (int)s, ushort us => (int)us, long l => (int)l, ulong ul => (int)ul, byte b => (int)b, sbyte sb => (int)sb,
        double d => (int)d, float f => (int)f, string name => name,
        _ => throw Error(E_INVALIDARG, $"not an index: {cIndex}"),
    };
}

public sealed class AppHostElement : IAppHostElement, IComServerObject
{
    internal readonly ConfigFile File;
    internal readonly ElementDef Def;
    private XElement? _node;
    // where it goes when first written: its parent element, unless a collection adds it
    private readonly AppHostElement? _parent;
    // a <location> the file does not have yet: its path, written when it is made
    private readonly string? _locationPath;

    internal AppHostElement(ConfigFile file, ElementDef def, XElement? node, AppHostElement? parent, string? locationPath = null)
    {
        File = file;
        Def = def;
        _node = node;
        _parent = parent;
        _locationPath = locationPath;
    }

    /// <summary>The element of an XML element: the same object each time (a client compares the elements it is given).</summary>
    internal static AppHostElement For(ConfigFile file, ElementDef def, XElement? node, AppHostElement? parent) =>
        node == null ? new AppHostElement(file, def, null, parent) : file.Elements.GetValue(node, n => new AppHostElement(file, def, n, parent));

    internal XElement? Node => _node;
    internal bool IsPresent => _node != null && (_node.Parent != null || _node.Document != null);

    /// <summary>The element's XML, made (with its ancestors) where the file does not have it yet.</summary>
    internal XElement EnsureNode()
    {
        if (_node != null) return _node;
        if (_parent == null) throw HResults.Error(HResults.E_INVALIDARG, $"{Def.Name}: not in the configuration");
        _node = _locationPath != null ? new XElement(Def.Name, new XAttribute("path", _locationPath)) : new XElement(Def.Name);
        _parent.EnsureNode().Add(_node);
        File.Dirty = true;
        return _node;
    }

    public string get_Name() => Def.Name;

    public IAppHostElementCollection get_Collection() =>
        Def.Collection != null ? new AppHostElementCollection(this) : throw HResults.Error(HResults.E_INVALIDARG, $"{Def.Name} is not a collection");

    public IAppHostPropertyCollection get_Properties() => new AppHostPropertyCollection(Properties());

    internal List<AppHostProperty> Properties()
    {
        var list = Def.Properties.Select(p => new AppHostProperty(this, p)).ToList();
        if (Def.AllowUnschematized && _node != null)
            foreach (var a in _node.Attributes())
                if (Def.Property(a.Name.LocalName) == null && !a.IsNamespaceDeclaration)
                    list.Add(new AppHostProperty(this, new PropertyDef { Name = a.Name.LocalName }));
        return list;
    }

    public IAppHostChildElementCollection get_ChildElements() => new AppHostChildElementCollection(Children());

    internal List<AppHostElement> Children()
    {
        var list = new List<AppHostElement>();
        foreach (var c in Def.Children)
            list.Add(For(File, c, _node?.Elements().FirstOrDefault(x => Same(x, c.Name)), this));
        if (Def.AllowUnschematized && _node != null)
        {
            var itemNames = Def.Collection?.Add.Select(a => a.Name).Concat(new[] { "remove", "clear" }).ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new HashSet<string>();
            foreach (var x in _node.Elements())
            {
                string n = x.Name.LocalName;
                if (Def.Child(n) != null || itemNames.Contains(n) || list.Any(l => l.Def.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(For(File, Unschematized(x), x, this));
            }
        }
        return list;
    }

    /// <summary>A schema for an element the schema does not know: a collection where its children are add, remove and clear.</summary>
    internal static ElementDef Unschematized(XElement x) =>
        x.Elements().Any(c => c.Name.LocalName is "add" or "remove" or "clear") ? ElementDef.UnschematizedCollection(x.Name.LocalName) : ElementDef.Unschematized(x.Name.LocalName);

    internal static bool Same(XElement x, string name) => x.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

    public object? GetMetadata(string bstrMetadataType) => bstrMetadataType switch
    {
        "isPresent" => IsPresent,
        "deepestPathSet" or "collectionItemFileConfigPath" or "configPath" => File.ConfigPath,
        "isLocked" or "childIsLocked" or "isLockedItem" => false,
        "overrideMode" => "Inherit",
        "effectiveOverrideMode" or "overrideModeDefault" => "Allow",
        "configSource" or "lockAllAttributesExcept" or "lockAllElementsExcept" or "lockAttributes" or "lockElements" => "",
        _ => null,
    };

    public void SetMetadata(string bstrMetadataType, object Value)
    {
        // locks and override modes are kept as Windows' file keeps them
        if (bstrMetadataType is "lockAttributes" or "lockElements" or "lockAllAttributesExcept" or "lockAllElementsExcept" or "lockItem" or "overrideMode" or "configSource")
        {
            EnsureNode().SetAttributeValue(bstrMetadataType, Value?.ToString());
            File.Dirty = true;
            return;
        }
        throw HResults.Error(HResults.E_INVALIDARG, $"metadata {bstrMetadataType} is not set here");
    }

    public IAppHostElementSchema get_Schema() => new AppHostElementSchema(Def);

    public IAppHostElement GetElementByName(string bstrSubName) =>
        Children().FirstOrDefault(c => c.Def.Name.Equals(bstrSubName, StringComparison.OrdinalIgnoreCase))
            ?? (Def.AllowUnschematized ? new AppHostElement(File, ElementDef.Unschematized(bstrSubName), null, this)
                : throw HResults.Error(HResults.E_INVALIDARG, $"{Def.Name} has no element {bstrSubName}"));

    public IAppHostProperty GetPropertyByName(string bstrSubName) =>
        Properties().FirstOrDefault(p => p.Def.Name.Equals(bstrSubName, StringComparison.OrdinalIgnoreCase))
            ?? (Def.AllowUnschematized ? new AppHostProperty(this, new PropertyDef { Name = bstrSubName })
                : throw HResults.Error(HResults.E_INVALIDARG, $"Unrecognized attribute '{bstrSubName}' of {Def.Name}"));

    public void Clear()
    {
        if (_node == null) return;
        _node.RemoveAttributes();
        _node.RemoveNodes();
        File.Dirty = true;
    }

    public IAppHostMethodCollection get_Methods() => new AppHostMethodCollection(this);
}

public sealed class AppHostProperty : IAppHostProperty, IComServerObject
{
    private readonly AppHostElement _element;
    internal readonly PropertyDef Def;

    internal AppHostProperty(AppHostElement element, PropertyDef def)
    {
        _element = element;
        Def = def;
    }

    private XAttribute? Attribute => _element.Node?.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(Def.Name, StringComparison.OrdinalIgnoreCase));

    public string get_Name() => Def.Name;

    public object get_Value() => Def.Computed != null ? Def.Computed(_element) ?? Def.DefaultValue
        : Attribute is { } a ? Values.Parse(Def, a.Value) : Def.DefaultValue;

    public void set_Value(object value)
    {
        if (Def.Computed != null) return;   // the configuration system's, not the file's (a binding's certificate: its method sets it)
        string text = Values.Format(Def, value);
        var node = _element.EnsureNode();
        if (Attribute is { } a) a.Value = text;
        else node.Add(new XAttribute(Def.Name, text));
        _element.File.Dirty = true;
    }

    public void Clear()
    {
        if (Attribute is { } a)
        {
            a.Remove();
            _element.File.Dirty = true;
        }
    }

    public string get_StringValue() => Def.Computed != null ? Values.Format(Def, get_Value()) : Attribute?.Value ?? Values.Format(Def, Def.DefaultValue);

    public IAppHostPropertyException? get_Exception() => null;

    public object? GetMetadata(string bstrMetadataType) => bstrMetadataType switch
    {
        "isDefaultValue" => Def.Computed != null ? Def.Computed(_element) == null : Attribute == null,
        "isEncrypted" or "isLocked" or "isInherited" => false,
        "isPresent" => Attribute != null,
        _ => null,
    };

    public void SetMetadata(string bstrMetadataType, object Value) =>
        throw HResults.Error(HResults.E_INVALIDARG, $"metadata {bstrMetadataType} is not set here");

    public IAppHostPropertySchema get_Schema() => new AppHostPropertySchema(Def);
}

/// <summary>An attribute's text read as its schema's type, and a value written as that type's text, as IIS's file has them.</summary>
internal static class Values
{
    public static object Parse(PropertyDef def, string text)
    {
        try
        {
            switch (def.Type)
            {
                case "bool": return text.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                case "uint": return uint.Parse(text.Trim(), CultureInfo.InvariantCulture);
                case "int": return int.Parse(text.Trim(), CultureInfo.InvariantCulture);
                case "int64": return long.Parse(text.Trim(), CultureInfo.InvariantCulture);
                case "timeSpan": return (ulong)TimeSpan.Parse(text.Trim(), CultureInfo.InvariantCulture).Ticks;
                case "enum":
                    if (uint.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out uint n)) return n;
                    return def.Values.FirstOrDefault(v => v.Name.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase)) is { Name: not null } e ? e.Value : 0u;
                case "flags":
                    uint flags = 0;
                    foreach (string part in text.Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (uint.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint p)) flags |= p;
                        else if (def.Values.FirstOrDefault(v => v.Name.Equals(part, StringComparison.OrdinalIgnoreCase)) is { Name: not null } f) flags |= f.Value;
                    }
                    return flags;
                default: return text;
            }
        }
        catch (FormatException) { return def.DefaultValue; }
        catch (OverflowException) { return def.DefaultValue; }
    }

    public static string Format(PropertyDef def, object? value)
    {
        switch (def.Type)
        {
            case "bool": return Convert.ToBoolean(value is string s ? s.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) : value, CultureInfo.InvariantCulture) ? "true" : "false";
            case "timeSpan":
                long ticks = value switch { TimeSpan t => t.Ticks, string str => TimeSpan.Parse(str, CultureInfo.InvariantCulture).Ticks, _ => Convert.ToInt64(value, CultureInfo.InvariantCulture) };
                return new TimeSpan(ticks).ToString("c", CultureInfo.InvariantCulture);
            case "enum":
            {
                uint n = value is string name ? (uint)Parse(def, name) : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
                return def.Values.FirstOrDefault(v => v.Value == n) is { Name: not null } e ? e.Name : n.ToString(CultureInfo.InvariantCulture);
            }
            case "flags":
            {
                uint n = value is string names ? (uint)Parse(def, names) : Convert.ToUInt32(value, CultureInfo.InvariantCulture);
                if (n == 0) return def.Values.FirstOrDefault(v => v.Value == 0) is { Name: not null } none ? none.Name : "0";
                var parts = def.Values.Where(v => v.Value != 0 && (n & v.Value) == v.Value).ToList();
                uint covered = parts.Aggregate(0u, (a, v) => a | v.Value);
                return covered == n ? string.Join(", ", parts.Select(v => v.Name)) : n.ToString(CultureInfo.InvariantCulture);
            }
            case "uint": return Convert.ToUInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case "int": return Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            case "int64": return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            default: return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }
}

public sealed class AppHostPropertyCollection : IAppHostPropertyCollection, IComServerObject
{
    private readonly List<AppHostProperty> _items;
    internal AppHostPropertyCollection(List<AppHostProperty> items) => _items = items;

    public uint get_Count() => (uint)_items.Count;

    public IAppHostProperty get_Item(object cIndex) => HResults.Index(cIndex) switch
    {
        int i when i >= 0 && i < _items.Count => _items[i],
        string name when _items.FirstOrDefault(p => p.Def.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } p => p,
        _ => throw HResults.Error(HResults.E_INVALIDARG, $"no property {cIndex}"),
    };
}

public sealed class AppHostChildElementCollection : IAppHostChildElementCollection, IComServerObject
{
    private readonly List<AppHostElement> _items;
    internal AppHostChildElementCollection(List<AppHostElement> items) => _items = items;

    public uint get_Count() => (uint)_items.Count;

    public IAppHostElement get_Item(object cIndex) => HResults.Index(cIndex) switch
    {
        int i when i >= 0 && i < _items.Count => _items[i],
        string name when _items.FirstOrDefault(e => e.Def.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } e => e,
        _ => throw HResults.Error(HResults.E_INVALIDARG, $"no element {cIndex}"),
    };
}

/// <summary>A collection's items: its element's children by the names it adds, from the last clear on.</summary>
public sealed class AppHostElementCollection : IAppHostElementCollection, IComServerObject
{
    private readonly AppHostElement _owner;
    internal AppHostElementCollection(AppHostElement owner) => _owner = owner;

    private CollectionDef Def => _owner.Def.Collection!;

    private List<XElement> ItemNodes()
    {
        if (_owner.Node == null) return new List<XElement>();
        var nodes = _owner.Node.Elements().ToList();
        int clear = nodes.FindLastIndex(x => Def.Clear != null && AppHostElement.Same(x, Def.Clear.Name));
        return nodes.Skip(clear + 1).Where(x => Def.Add.Any(a => AppHostElement.Same(x, a.Name))).ToList();
    }

    private AppHostElement Item(XElement x) =>
        AppHostElement.For(_owner.File, _owner.Def.AddElement(x.Name.LocalName) ?? AppHostElement.Unschematized(x), x, _owner);

    public uint get_Count() => (uint)ItemNodes().Count;

    public IAppHostElement get_Item(object cIndex)
    {
        var items = ItemNodes();
        // an element as the index: the item with its key (a client fills an element with a key's values to find one)
        if (cIndex is AppHostElement key)
            return items.FirstOrDefault(x => SameKey(key, x)) is { } found ? Item(found)
                : throw HResults.Error(HResults.ERROR_NOT_FOUND, $"no item of {_owner.Def.Name} has that key");
        return HResults.Index(cIndex) switch
        {
            int i when i >= 0 && i < items.Count => Item(items[i]),
            _ => throw HResults.Error(HResults.ERROR_NOT_FOUND, $"no item {cIndex} in {_owner.Def.Name}"),
        };
    }

    // the key's attributes (its unique key, else its combined key) the same in both, as the schema compares them
    private static bool SameKey(AppHostElement key, XElement item)
    {
        var keys = key.Def.Properties.Where(p => p.UniqueKey).ToList();
        if (keys.Count == 0) keys = key.Def.Properties.Where(p => p.CombinedKey).ToList();
        if (keys.Count == 0 || key.Node == null) return false;
        foreach (var k in keys)
        {
            string? a = key.Node.Attributes().FirstOrDefault(x => x.Name.LocalName.Equals(k.Name, StringComparison.OrdinalIgnoreCase))?.Value;
            string? b = item.Attributes().FirstOrDefault(x => x.Name.LocalName.Equals(k.Name, StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.Equals(a ?? "", b ?? "", k.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public void AddElement(IAppHostElement pElement, int cPosition)
    {
        if (pElement is not AppHostElement element || element.Node == null)
            throw HResults.Error(HResults.E_INVALIDARG, "an element not made by this collection (CreateNewElement)");
        var node = element.Node;
        if (node.Parent != null) node.Remove();
        if (!Def.AllowDuplicates && Duplicate(element) != null)
            throw HResults.Error(HResults.ERROR_ALREADY_EXISTS, $"Cannot add duplicate collection entry of type '{node.Name.LocalName}'");
        var items = ItemNodes();
        if (cPosition >= 0 && cPosition < items.Count) items[cPosition].AddBeforeSelf(node);
        else if (items.Count > 0) items[^1].AddAfterSelf(node);
        else _owner.EnsureNode().Add(node);
        _owner.File.Dirty = true;
    }

    // an item with the same key: its unique key, else its combined key's attributes together
    private XElement? Duplicate(AppHostElement element)
    {
        var keys = element.Def.Properties.Where(p => p.UniqueKey).ToList();
        if (keys.Count == 0) keys = element.Def.Properties.Where(p => p.CombinedKey).ToList();
        if (keys.Count == 0) return null;
        string Key(XElement x) => string.Join("\u0001", keys.Select(k =>
        {
            string v = x.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(k.Name, StringComparison.OrdinalIgnoreCase))?.Value ?? "";
            return k.CaseSensitive ? v : v.ToLowerInvariant();
        }));
        string mine = Key(element.Node!);
        return ItemNodes().FirstOrDefault(x => x != element.Node && Key(x) == mine);
    }

    public void DeleteElement(object cIndex)
    {
        var items = ItemNodes();
        if (HResults.Index(cIndex) is not int i || i < 0 || i >= items.Count)
            throw HResults.Error(HResults.ERROR_NOT_FOUND, $"no item {cIndex} in {_owner.Def.Name}");
        items[i].Remove();
        _owner.File.Dirty = true;
    }

    public void Clear()
    {
        foreach (var x in ItemNodes()) x.Remove();
        _owner.File.Dirty = true;
    }

    public IAppHostElement CreateNewElement(string bstrElementName)
    {
        string name = string.IsNullOrEmpty(bstrElementName) ? Def.Add[0].Name : bstrElementName;
        var def = _owner.Def.AddElement(name) ?? (_owner.Def.AllowUnschematized ? ElementDef.Unschematized(name)
            : throw HResults.Error(HResults.E_INVALIDARG, $"{_owner.Def.Name} adds no element {name}"));
        return new AppHostElement(_owner.File, def, new XElement(def.Name), null);
    }

    public IAppHostCollectionSchema get_Schema() => new AppHostCollectionSchema(Def);
}

public sealed class AppHostMethodCollection : IAppHostMethodCollection, IComServerObject
{
    private readonly AppHostElement _element;
    internal AppHostMethodCollection(AppHostElement element) => _element = element;

    public uint get_Count() => (uint)_element.Def.Methods.Count;

    public IAppHostMethod get_Item(object cIndex) => HResults.Index(cIndex) switch
    {
        int i when i >= 0 && i < _element.Def.Methods.Count => new AppHostMethod(_element, _element.Def.Methods[i]),
        string name when _element.Def.Methods.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } m => new AppHostMethod(_element, m),
        _ => throw HResults.Error(HResults.ERROR_NOT_FOUND, $"{_element.Def.Name} has no method {cIndex}"),
    };
}

/// <summary>A configuration method of an element (a binding's AddSslCertificate).</summary>
public sealed class AppHostMethod : IAppHostMethod, IComServerObject
{
    private readonly AppHostElement _element;
    private readonly MethodDef _def;
    internal AppHostMethod(AppHostElement element, MethodDef def) { _element = element; _def = def; }

    public string get_Name() => _def.Name;
    public IAppHostMethodSchema get_Schema() => new AppHostMethodSchema(_def);
    public IAppHostMethodInstance CreateInstance() => new AppHostMethodInstance(_element, _def);
}

public sealed class AppHostMethodInstance : IAppHostMethodInstance, IComServerObject
{
    private readonly AppHostElement _element, _input, _output;
    private readonly MethodDef _def;

    internal AppHostMethodInstance(AppHostElement element, MethodDef def)
    {
        _element = element;
        _def = def;
        _input = new AppHostElement(element.File, def.Input, new XElement(def.Input.Name), null);
        _output = new AppHostElement(element.File, def.Output, new XElement(def.Output.Name), null);
    }

    public IAppHostElement get_Input() => _input;
    public IAppHostElement get_Output() => _output;
    public void Execute() => _def.Execute(_element, _input, _output);
    public object? GetMetadata(string bstrMetadataType) => null;
    public void SetMetadata(string bstrMetadataType, object Value) { }
}

public sealed class AppHostMethodSchema : IAppHostMethodSchema, IComServerObject
{
    private readonly MethodDef _def;
    internal AppHostMethodSchema(MethodDef def) => _def = def;

    public string get_Name() => _def.Name;
    public IAppHostElementSchema get_InputSchema() => new AppHostElementSchema(_def.Input);
    public IAppHostElementSchema get_OutputSchema() => new AppHostElementSchema(_def.Output);
    public object? GetMetadata(string bstrMetadataType) => null;
}

// ---- the schema as the configuration system answers it

public sealed class AppHostElementSchema : IAppHostElementSchema, IComServerObject
{
    private readonly ElementDef _def;
    internal AppHostElementSchema(ElementDef def) => _def = def;

    public string get_Name() => _def.Name;
    public bool get_DoesAllowUnschematizedProperties() => _def.AllowUnschematized;
    public object? GetMetadata(string bstrMetadataType) => null;
    public IAppHostCollectionSchema? get_CollectionSchema() => _def.Collection is { } c ? new AppHostCollectionSchema(c) : null;
    public IAppHostElementSchemaCollection get_ChildElementSchemas() => new AppHostElementSchemaCollection(_def.Children);
    public IAppHostPropertySchemaCollection get_PropertySchemas() => new AppHostPropertySchemaCollection(_def.Properties);
    public bool get_IsCollectionDefault() => _def.IsCollectionDefault;
}

public sealed class AppHostCollectionSchema : IAppHostCollectionSchema, IComServerObject
{
    private readonly CollectionDef _def;
    internal AppHostCollectionSchema(CollectionDef def) => _def = def;

    public string get_AddElementNames() => _def.AddNames;
    public IAppHostElementSchema GetAddElementSchema(string bstrElementName) =>
        _def.Add.FirstOrDefault(a => a.Name.Equals(bstrElementName, StringComparison.OrdinalIgnoreCase)) is { } a ? new AppHostElementSchema(a)
            : throw HResults.Error(HResults.E_INVALIDARG, $"the collection adds no element {bstrElementName}");
    public IAppHostElementSchema? get_RemoveElementSchema() => _def.Remove is { } r ? new AppHostElementSchema(r) : null;
    public IAppHostElementSchema? get_ClearElementSchema() => _def.Clear is { } c ? new AppHostElementSchema(c) : null;
    public bool get_IsMergeAppend() => _def.MergeAppend;
    public object? GetMetadata(string bstrMetadataType) => null;
    public bool get_DoesAllowDuplicates() => _def.AllowDuplicates;
}

public sealed class AppHostElementSchemaCollection : IAppHostElementSchemaCollection, IComServerObject
{
    private readonly List<ElementDef> _items;
    internal AppHostElementSchemaCollection(List<ElementDef> items) => _items = items;
    public uint get_Count() => (uint)_items.Count;
    public IAppHostElementSchema get_Item(object cIndex) => HResults.Index(cIndex) switch
    {
        int i when i >= 0 && i < _items.Count => new AppHostElementSchema(_items[i]),
        string name when _items.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } e => new AppHostElementSchema(e),
        _ => throw HResults.Error(HResults.E_INVALIDARG, $"no element schema {cIndex}"),
    };
}

public sealed class AppHostPropertySchema : IAppHostPropertySchema, IComServerObject
{
    private readonly PropertyDef _def;
    internal AppHostPropertySchema(PropertyDef def) => _def = def;

    public string get_Name() => _def.Name;
    public string get_Type() => _def.Type;
    public object get_DefaultValue() => _def.DefaultValue;
    public bool get_IsRequired() => _def.Required;
    public bool get_IsUniqueKey() => _def.UniqueKey;
    public bool get_IsCombinedKey() => _def.CombinedKey;
    public bool get_IsExpanded() => _def.Expanded;
    public string get_ValidationType() => "";
    public string get_ValidationParameter() => "";
    public object? GetMetadata(string bstrMetadataType) => null;
    public bool get_IsCaseSensitive() => _def.CaseSensitive;
    public IAppHostConstantValueCollection get_PossibleValues() => new AppHostConstantValueCollection(_def.Values);
    public bool get_DoesAllowInfinite() => false;
    public bool get_IsEncrypted() => _def.Encrypted;
    public string get_TimeSpanFormat() => _def.TimeSpanFormat;
}

public sealed class AppHostPropertySchemaCollection : IAppHostPropertySchemaCollection, IComServerObject
{
    private readonly List<PropertyDef> _items;
    internal AppHostPropertySchemaCollection(List<PropertyDef> items) => _items = items;
    public uint get_Count() => (uint)_items.Count;
    public IAppHostPropertySchema get_Item(object cIndex) => HResults.Index(cIndex) switch
    {
        int i when i >= 0 && i < _items.Count => new AppHostPropertySchema(_items[i]),
        string name when _items.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } p => new AppHostPropertySchema(p),
        _ => throw HResults.Error(HResults.E_INVALIDARG, $"no property schema {cIndex}"),
    };
}

public sealed class AppHostConstantValueCollection : IAppHostConstantValueCollection, IComServerObject
{
    private readonly (string Name, uint Value)[] _items;
    internal AppHostConstantValueCollection((string, uint)[] items) => _items = items;
    public uint get_Count() => (uint)_items.Length;
    public IAppHostConstantValue get_Item(object cIndex) => HResults.Index(cIndex) switch
    {
        int i when i >= 0 && i < _items.Length => new AppHostConstantValue(_items[i]),
        string name when _items.FirstOrDefault(v => v.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } v => new AppHostConstantValue(v),
        _ => throw HResults.Error(HResults.E_INVALIDARG, $"no value {cIndex}"),
    };
}

public sealed class AppHostConstantValue : IAppHostConstantValue, IComServerObject
{
    private readonly (string Name, uint Value) _value;
    internal AppHostConstantValue((string, uint) value) => _value = value;
    public string get_Name() => _value.Name;
    public uint get_Value() => _value.Value;
}
