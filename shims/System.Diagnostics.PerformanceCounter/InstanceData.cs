// A category's readings all at once (PerformanceCounterCategory.ReadCategory): each counter's value for each
// instance, keyed by name in lower case, as .NET's collections are.

using System.Collections;

namespace System.Diagnostics
{
    public partial class InstanceData
    {
        public InstanceData(string instanceName, CounterSample sample)
        {
            InstanceName = instanceName;
            Sample = sample;
        }

        public string InstanceName { get; }

        public CounterSample Sample { get; }

        public long RawValue => Sample.RawValue;
    }

    public partial class InstanceDataCollection : DictionaryBase
    {
        // .NET's name for the one instance of a single-instance category
        internal const string SingleInstanceName = "systemdiagnosticsperfcounterlibsingleinstance";

        [Obsolete("This constructor has been deprecated. Use System.Diagnostics.InstanceDataCollectionCollection.get_Item to get an instance of this collection instead.")]
        public InstanceDataCollection(string counterName)
        {
            ArgumentNullException.ThrowIfNull(counterName);
            CounterName = counterName;
        }

        public string CounterName { get; }

        public ICollection Keys => Dictionary.Keys;

        public ICollection Values => Dictionary.Values;

        public InstanceData this[string instanceName]
        {
            get
            {
                ArgumentNullException.ThrowIfNull(instanceName);
                if (instanceName.Length == 0)
                    instanceName = SingleInstanceName;
                return (InstanceData)Dictionary[instanceName.ToLowerInvariant()];
            }
        }

        internal void Add(string instanceName, InstanceData value) => Dictionary.Add(instanceName.ToLowerInvariant(), value);

        public bool Contains(string instanceName)
        {
            ArgumentNullException.ThrowIfNull(instanceName);
            return Dictionary.Contains(instanceName.ToLowerInvariant());
        }

        public void CopyTo(InstanceData[] instances, int index) => Dictionary.Values.CopyTo(instances, index);
    }

    public partial class InstanceDataCollectionCollection : DictionaryBase
    {
        [Obsolete("This constructor has been deprecated. Use System.Diagnostics.PerformanceCounterCategory.ReadCategory() to get an instance of this collection instead.")]
        public InstanceDataCollectionCollection() : base()
        {
        }

        public InstanceDataCollection this[string counterName]
        {
            get
            {
                ArgumentNullException.ThrowIfNull(counterName);
                return (InstanceDataCollection)Dictionary[counterName.ToLowerInvariant()];
            }
        }

        public ICollection Keys => Dictionary.Keys;

        public ICollection Values => Dictionary.Values;

        internal void Add(string counterName, InstanceDataCollection value) => Dictionary.Add(counterName.ToLowerInvariant(), value);

        public bool Contains(string counterName)
        {
            ArgumentNullException.ThrowIfNull(counterName);
            return Dictionary.Contains(counterName.ToLowerInvariant());
        }

        public void CopyTo(InstanceDataCollection[] counters, int index) => Dictionary.Values.CopyTo(counters, index);
    }

    /// <summary>The old ICollectData entry point: .NET's answers nothing (a null result), as here.</summary>
    public sealed partial class PerformanceCounterManager : ICollectData
    {
        [Obsolete("PerformanceCounterManager has been deprecated. Use the PerformanceCounters through the System.Diagnostics.PerformanceCounter class instead.")]
        public PerformanceCounterManager()
        {
        }

        [Obsolete("PerformanceCounterManager has been deprecated. Use the PerformanceCounters through the System.Diagnostics.PerformanceCounter class instead.")]
        void ICollectData.CollectData(int callIdx, IntPtr valueNamePtr, IntPtr dataPtr, int totalBytes, out IntPtr res)
        {
            res = (IntPtr)(-1);
        }

        [Obsolete("PerformanceCounterManager has been deprecated. Use the PerformanceCounters through the System.Diagnostics.PerformanceCounter class instead.")]
        void ICollectData.CloseData()
        {
        }
    }
}
