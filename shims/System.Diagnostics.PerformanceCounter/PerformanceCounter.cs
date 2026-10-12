// PerformanceCounter, PerformanceCounterCategory and the creation data, over Categories.cs.
// Messages are Windows' own, so an app that matches on them behaves as it does there.

using System.Collections;
using System.ComponentModel;
using System.Linq;

namespace System.Diagnostics
{
    public sealed partial class PerformanceCounter : Component, ISupportInitialize
    {
        [Obsolete("PerformanceCounter.DefaultFileMappingSize has been deprecated and is not used. Use machine.config or an application configuration file to set the size of the PerformanceCounter file mapping instead.")]
        public static int DefaultFileMappingSize = 524288;

        private string _category = "";
        private string _counter = "";
        private string _instance = "";
        private string _machine = ".";
        private bool _readOnly = true;
        private bool _initializing;
        private PerformanceCounterInstanceLifetime _lifetime;
        private Category _resolved;
        private (string Name, string Help, PerformanceCounterType Type) _definition;
        private CounterSample _previous = CounterSample.Empty;

        public PerformanceCounter() { }

        public PerformanceCounter(string categoryName, string counterName) : this(categoryName, counterName, true) { }

        public PerformanceCounter(string categoryName, string counterName, bool readOnly) : this(categoryName, counterName, "", readOnly) { }

        public PerformanceCounter(string categoryName, string counterName, string instanceName) : this(categoryName, counterName, instanceName, true) { }

        public PerformanceCounter(string categoryName, string counterName, string instanceName, bool readOnly)
        {
            if (!readOnly) VerifyWriteableName(categoryName, counterName, instanceName);
            CategoryName = categoryName;
            CounterName = counterName;
            InstanceName = instanceName;
            _readOnly = readOnly;
            Initialize();
        }

        public PerformanceCounter(string categoryName, string counterName, string instanceName, string machineName)
        {
            MachineName = machineName;
            CategoryName = categoryName;
            CounterName = counterName;
            InstanceName = instanceName;
            Initialize();
        }

        public string CategoryName
        {
            get => _category;
            set { ArgumentNullException.ThrowIfNull(value); _category = value; Reset(); }
        }

        public string CounterHelp
        {
            get { Initialize(); return _definition.Help; }
        }

        public string CounterName
        {
            get => _counter;
            set { ArgumentNullException.ThrowIfNull(value); _counter = value; Reset(); }
        }

        public PerformanceCounterType CounterType
        {
            get { Initialize(); return _definition.Type; }
        }

        public PerformanceCounterInstanceLifetime InstanceLifetime
        {
            get => _lifetime;
            set
            {
                if (value < PerformanceCounterInstanceLifetime.Global || value > PerformanceCounterInstanceLifetime.Process) throw new ArgumentOutOfRangeException(nameof(value));
                if (_resolved != null) throw new InvalidOperationException("The InstanceLifetime cannot be set after the instance has been initialized.  You must use the default constructor and set the CategoryName, InstanceName, CounterName, InstanceLifetime and ReadOnly properties manually before setting the RawValue.");
                _lifetime = value;
            }
        }

        public string InstanceName
        {
            get => _instance;
            set
            {
                if (value != null && value.Length > 127) throw new ArgumentException("Instance names used for writing to custom counters must be 127 characters or less.", nameof(value));
                _instance = value ?? "";
                Reset();
            }
        }

        public string MachineName
        {
            get => _machine;
            set
            {
                if (string.IsNullOrEmpty(value)) throw new ArgumentException($"Invalid value '{value}' for parameter 'machineName'.", nameof(value));
                _machine = value;
                Reset();
            }
        }

        public long RawValue
        {
            get => _readOnly ? NextSample().RawValue : Custom().Get(_definition.Name, _instance);
            set => Write(_ => value);
        }

        public bool ReadOnly
        {
            get => _readOnly;
            set { _readOnly = value; Reset(); }
        }

        public void BeginInit()
        {
            Close();
            _initializing = true;
        }

        public void Close()
        {
            _resolved = null;
            _previous = CounterSample.Empty;
        }

        public static void CloseSharedResources() { }

        public long Decrement() => Write(v => v - 1);

        protected override void Dispose(bool disposing)
        {
            if (disposing) Close();
            base.Dispose(disposing);
        }

        public void EndInit()
        {
            _initializing = false;
            if (_category.Length > 0 && _counter.Length > 0) Initialize();
        }

        public long Increment() => Write(v => v + 1);

        public long IncrementBy(long value) => Write(v => v + value);

        public CounterSample NextSample()
        {
            Initialize();
            if (_resolved.Type == PerformanceCounterCategoryType.MultiInstance && _resolved.IsCustom == false
                && !_resolved.InstanceNames().Contains(_instance, StringComparer.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Instance '{_instance}' does not exist in the specified Category.");
            }
            return _resolved.Read(_definition.Name, _instance);
        }

        public float NextValue()
        {
            CounterSample next = NextSample();
            float value = CounterSampleCalculator.ComputeCounterValue(_previous, next);
            _previous = next;
            return value;
        }

        public void RemoveInstance()
        {
            if (_readOnly) throw new InvalidOperationException("Cannot update Performance Counter, this object has been initialized as ReadOnly.");
            Custom().RemoveInstance(_instance);
        }

        private void Reset()
        {
            if (!_initializing) Close();
        }

        private void Initialize()
        {
            if (_resolved != null || _initializing) return;
            if (_category.Length == 0) throw new InvalidOperationException("Failed to initialize because CategoryName is missing.");
            if (_counter.Length == 0) throw new InvalidOperationException("Failed to initialize because CounterName is missing.");
            Categories.RequireLocal(_machine);
            Category category = Categories.Find(_category) ?? throw new InvalidOperationException("Category does not exist.");
            var definition = category.Counter(_counter)
                ?? throw new InvalidOperationException($"Could not locate Performance Counter with specified category name '{_category}', counter name '{_counter}'.");
            if (!_readOnly && !category.IsCustom) throw new InvalidOperationException("The requested Performance Counter is not a custom counter, it has to be initialized as ReadOnly.");
            if (category.Type == PerformanceCounterCategoryType.SingleInstance && _instance.Length > 0)
                throw new InvalidOperationException($"Counter is single instance, instance name '{_instance}' is not valid for this counter category.");
            if (category.Type == PerformanceCounterCategoryType.MultiInstance && _instance.Length == 0)
                throw new InvalidOperationException("Counter is not single instance, an instance name needs to be specified.");
            _resolved = category;
            _definition = definition;
        }

        private CustomCategory Custom()
        {
            Initialize();
            return (CustomCategory)_resolved;
        }

        private long Write(Func<long, long> change)
        {
            if (_readOnly)
            {
                // Windows names the reason only once it has looked the counter up.
                Initialize();
                throw new InvalidOperationException("Cannot update Performance Counter, this object has been initialized as ReadOnly.");
            }
            return Custom().Update(_definition.Name, _instance, change);
        }

        private static void VerifyWriteableName(string category, string counter, string instance)
        {
            ArgumentNullException.ThrowIfNull(category, "categoryName");
            ArgumentNullException.ThrowIfNull(counter, "counterName");
            if (instance != null && instance.Length > 127) throw new InvalidOperationException("Instance names used for writing to custom counters must be 127 characters or less.");
        }
    }

    public sealed partial class PerformanceCounterCategory
    {
        private string _name;
        private string _machine = ".";

        public PerformanceCounterCategory() { }

        public PerformanceCounterCategory(string categoryName) : this(categoryName, ".") { }

        public PerformanceCounterCategory(string categoryName, string machineName)
        {
            ArgumentNullException.ThrowIfNull(categoryName);
            if (categoryName.Length == 0) throw new ArgumentException($"Invalid value '{categoryName}' for parameter 'categoryName'.", nameof(categoryName));
            if (string.IsNullOrEmpty(machineName)) throw new ArgumentException($"Invalid value '{machineName}' for parameter 'machineName'.", nameof(machineName));
            _name = categoryName;
            _machine = machineName;
        }

        public string CategoryHelp => Resolve().Help;

        public string CategoryName
        {
            get => _name;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (value.Length == 0) throw new ArgumentException($"Invalid value {value} for property CategoryName.", nameof(value));
                _name = value;
            }
        }

        public PerformanceCounterCategoryType CategoryType => Resolve().Type;

        public string MachineName
        {
            get => _machine;
            set
            {
                if (string.IsNullOrEmpty(value)) throw new ArgumentException($"Invalid value {value} for property MachineName.", nameof(value));
                _machine = value;
            }
        }

        public bool CounterExists(string counterName)
        {
            ArgumentNullException.ThrowIfNull(counterName);
            return Resolve().Counter(counterName) != null;
        }

        public static bool CounterExists(string counterName, string categoryName) => CounterExists(counterName, categoryName, ".");

        public static bool CounterExists(string counterName, string categoryName, string machineName)
        {
            ArgumentNullException.ThrowIfNull(counterName);
            return new PerformanceCounterCategory(categoryName, machineName).CounterExists(counterName);
        }

        [Obsolete("This overload of PerformanceCounterCategory.Create has been deprecated. Use System.Diagnostics.PerformanceCounterCategory.Create(string categoryName, string categoryHelp, PerformanceCounterCategoryType categoryType, CounterCreationDataCollection counterData) instead.")]
        public static PerformanceCounterCategory Create(string categoryName, string categoryHelp, CounterCreationDataCollection counterData) =>
            Create(categoryName, categoryHelp, PerformanceCounterCategoryType.Unknown, counterData);

        public static PerformanceCounterCategory Create(string categoryName, string categoryHelp, PerformanceCounterCategoryType categoryType, CounterCreationDataCollection counterData)
        {
            ArgumentNullException.ThrowIfNull(categoryName);
            ArgumentNullException.ThrowIfNull(counterData);
            if (categoryType < PerformanceCounterCategoryType.Unknown || categoryType > PerformanceCounterCategoryType.MultiInstance) throw new ArgumentOutOfRangeException(nameof(categoryType));
            if (categoryName.Length == 0 || categoryName.Length > 80) throw new ArgumentException($"Invalid value '{categoryName}' for parameter 'categoryName'.", nameof(categoryName));
            if (counterData.Count == 0) throw new ArgumentException("Cannot create a category with no counters.", nameof(counterData));

            var category = new CustomCategory
            {
                Name = categoryName,
                Help = string.IsNullOrEmpty(categoryHelp) ? "Help not available." : categoryHelp,
                Type = categoryType == PerformanceCounterCategoryType.Unknown ? PerformanceCounterCategoryType.SingleInstance : categoryType,
            };
            foreach (CounterCreationData d in counterData)
            {
                if (string.IsNullOrEmpty(d.CounterName)) throw new ArgumentException("Invalid empty or null string for counter name.");
                if (category.Definitions.Any(x => string.Equals(x.Name, d.CounterName, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"Cannot create Performance Category with counter name {d.CounterName} because the name is a duplicate.");
                category.Definitions.Add((d.CounterName, string.IsNullOrEmpty(d.CounterHelp) ? "Help not available." : d.CounterHelp, d.CounterType));
            }
            Categories.Add(category);
            return new PerformanceCounterCategory(categoryName);
        }

        public static PerformanceCounterCategory Create(string categoryName, string categoryHelp, PerformanceCounterCategoryType categoryType, string counterName, string counterHelp) =>
            Create(categoryName, categoryHelp, categoryType, new CounterCreationDataCollection { new CounterCreationData(counterName, counterHelp, PerformanceCounterType.NumberOfItems32) });

        [Obsolete("This overload of PerformanceCounterCategory.Create has been deprecated. Use System.Diagnostics.PerformanceCounterCategory.Create(string categoryName, string categoryHelp, PerformanceCounterCategoryType categoryType, string counterName, string counterHelp) instead.")]
        public static PerformanceCounterCategory Create(string categoryName, string categoryHelp, string counterName, string counterHelp) =>
            Create(categoryName, categoryHelp, PerformanceCounterCategoryType.Unknown, counterName, counterHelp);

        public static void Delete(string categoryName)
        {
            ArgumentNullException.ThrowIfNull(categoryName);
            Categories.Delete(categoryName);
        }

        public static bool Exists(string categoryName) => Exists(categoryName, ".");

        public static bool Exists(string categoryName, string machineName)
        {
            ArgumentNullException.ThrowIfNull(categoryName);
            if (categoryName.Length == 0) throw new ArgumentException($"Invalid value '{categoryName}' for parameter 'categoryName'.", nameof(categoryName));
            if (string.IsNullOrEmpty(machineName)) throw new ArgumentException($"Invalid value '{machineName}' for parameter 'machineName'.", nameof(machineName));
            Categories.RequireLocal(machineName);
            return Categories.Find(categoryName) != null;
        }

        public static PerformanceCounterCategory[] GetCategories() => GetCategories(".");

        public static PerformanceCounterCategory[] GetCategories(string machineName)
        {
            if (string.IsNullOrEmpty(machineName)) throw new ArgumentException($"Invalid value '{machineName}' for parameter 'machineName'.", nameof(machineName));
            Categories.RequireLocal(machineName);
            return Categories.All().Select(c => new PerformanceCounterCategory(c.Name, machineName)).ToArray();
        }

        public PerformanceCounter[] GetCounters() => GetCounters("");

        public PerformanceCounter[] GetCounters(string instanceName)
        {
            ArgumentNullException.ThrowIfNull(instanceName);
            Category c = Resolve();
            if (instanceName.Length > 0 && !InstanceExists(instanceName))
                throw new InvalidOperationException($"Instance {instanceName} does not exist in category {_name}.");
            if (c.Type == PerformanceCounterCategoryType.MultiInstance && instanceName.Length == 0)
                throw new ArgumentException("Counter is not single instance, an instance name needs to be specified.");
            return c.Counters.Select(d => new PerformanceCounter(c.Name, d.Name, instanceName, _machine)).ToArray();
        }

        public string[] GetInstanceNames() => Resolve().InstanceNames();

        public bool InstanceExists(string instanceName)
        {
            ArgumentNullException.ThrowIfNull(instanceName);
            return Resolve().InstanceNames().Contains(instanceName, StringComparer.OrdinalIgnoreCase);
        }

        public static bool InstanceExists(string instanceName, string categoryName) => InstanceExists(instanceName, categoryName, ".");

        public static bool InstanceExists(string instanceName, string categoryName, string machineName) =>
            new PerformanceCounterCategory(categoryName, machineName).InstanceExists(instanceName);

        // Reading a whole category at once is not implemented.
        public InstanceDataCollectionCollection ReadCategory() => throw new PlatformNotSupportedException("System.Diagnostics.PerformanceCounter is not supported on this platform.");

        private Category Resolve()
        {
            if (_name == null) throw new InvalidOperationException("Category name property has not been set.");
            Categories.RequireLocal(_machine);
            return Categories.Find(_name) ?? throw new InvalidOperationException("Category does not exist.");
        }
    }

    public partial class CounterCreationData
    {
        private PerformanceCounterType _type = PerformanceCounterType.NumberOfItems32;
        private string _name = "";
        private string _help = "";

        public CounterCreationData() { }

        public CounterCreationData(string counterName, string counterHelp, PerformanceCounterType counterType)
        {
            CounterType = counterType;
            CounterName = counterName;
            CounterHelp = counterHelp;
        }

        public string CounterHelp
        {
            get => _help;
            set => _help = value;
        }

        public string CounterName
        {
            get => _name;
            set => _name = value;
        }

        public PerformanceCounterType CounterType
        {
            get => _type;
            set
            {
                if (!Enum.IsDefined(value)) throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(PerformanceCounterType));
                _type = value;
            }
        }
    }

    public partial class CounterCreationDataCollection : CollectionBase
    {
        public CounterCreationDataCollection() { }

        public CounterCreationDataCollection(CounterCreationDataCollection value) => AddRange(value);

        public CounterCreationDataCollection(CounterCreationData[] value) => AddRange(value);

        public CounterCreationData this[int index]
        {
            get => (CounterCreationData)List[index];
            set => List[index] = value;
        }

        public int Add(CounterCreationData value) => List.Add(value);

        public void AddRange(CounterCreationDataCollection value)
        {
            ArgumentNullException.ThrowIfNull(value);
            int count = value.Count;
            for (int i = 0; i < count; i++) Add(value[i]);
        }

        public void AddRange(CounterCreationData[] value)
        {
            ArgumentNullException.ThrowIfNull(value);
            foreach (CounterCreationData d in value) Add(d);
        }

        public bool Contains(CounterCreationData value) => List.Contains(value);

        public void CopyTo(CounterCreationData[] array, int index) => List.CopyTo(array, index);

        public int IndexOf(CounterCreationData value) => List.IndexOf(value);

        public void Insert(int index, CounterCreationData value) => List.Insert(index, value);

        protected override void OnValidate(object value)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value is not CounterCreationData) throw new ArgumentException("Must add value of type CounterCreationData.");
        }

        public virtual void Remove(CounterCreationData value) => List.Remove(value);
    }
}
