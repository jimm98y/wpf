// System.Diagnostics.PerformanceData: a provider's counter sets, published through PerfLib version 2. The
// classes are .NET's, line for line in what they check and throw; what they call is PerfLib below -- advapi32's
// on Windows, so the counters show in Performance Monitor once the provider's manifest is installed (lodctr /m),
// and elsewhere PerfLib's own rules kept in this process: a counter set registered once per provider, an instance
// once per name, a single-instance set's one instance, and each counter's value read by reference from the
// memory the counter writes, exactly as PerfLib reads it.

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace System.Diagnostics.PerformanceData
{
    public partial class CounterSet : IDisposable
    {
        internal PerfProvider _provider;
        internal Guid _providerGuid;
        internal Guid _counterSet;
        internal CounterSetInstanceType _instType;
        private readonly object _lockObject;
        private bool _instanceCreated;
        internal Dictionary<string, int> _stringToId;
        internal Dictionary<int, CounterType> _idToCounter;

        public CounterSet(Guid providerGuid, Guid counterSetGuid, CounterSetInstanceType instanceType)
        {
            if (!Enum.IsDefined(instanceType))
                throw new ArgumentException(SR.Format(SR.InvalidCounterSetInstanceType, instanceType), nameof(instanceType));
            _providerGuid = providerGuid;
            _counterSet = counterSetGuid;
            _instType = instanceType;
            PerfProviderCollection.RegisterCounterSet(_counterSet);
            _provider = PerfProviderCollection.QueryProvider(_providerGuid);
            _lockObject = new object();
            _stringToId = new Dictionary<string, int>();
            _idToCounter = new Dictionary<int, CounterType>();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~CounterSet()
        {
            Dispose(false);
        }

        protected virtual void Dispose(bool disposing)
        {
            lock (this)
            {
                PerfProviderCollection.UnregisterCounterSet(_counterSet);
                if (_instanceCreated && _provider != null)
                {
                    lock (_lockObject)
                    {
                        if (_provider != null)
                        {
                            Interlocked.Decrement(ref _provider._counterSet);
                            if (_provider._counterSet <= 0)
                                PerfProviderCollection.RemoveProvider(_providerGuid);
                            _provider = null;
                        }
                    }
                }
            }
        }

        public void AddCounter(int counterId, CounterType counterType)
        {
            if (_provider == null)
                throw new InvalidOperationException(SR.Format(SR.NoActiveProvider, _providerGuid));
            if (!Enum.IsDefined(counterType))
                throw new ArgumentException(SR.Format(SR.InvalidCounterType, counterType), nameof(counterType));
            if (_instanceCreated)
                throw new InvalidOperationException(SR.Format(SR.AddCounterAfterInstance, _counterSet));
            lock (_lockObject)
            {
                if (_instanceCreated)
                    throw new InvalidOperationException(SR.Format(SR.AddCounterAfterInstance, _counterSet));
                if (_idToCounter.ContainsKey(counterId))
                    throw new ArgumentException(SR.Format(SR.CounterAlreadyExists, counterId, _counterSet), nameof(counterId));
                _idToCounter.Add(counterId, counterType);
            }
        }

        public void AddCounter(int counterId, CounterType counterType, string counterName)
        {
            ArgumentNullException.ThrowIfNull(counterName);
            if (counterName.Length == 0)
                throw new ArgumentException(SR.EmptyCounterName, nameof(counterName));
            if (!Enum.IsDefined(counterType))
                throw new ArgumentException(SR.Format(SR.InvalidCounterType, counterType), nameof(counterType));
            if (_provider == null)
                throw new InvalidOperationException(SR.Format(SR.NoActiveProvider, _providerGuid));
            if (_instanceCreated)
                throw new InvalidOperationException(SR.Format(SR.AddCounterAfterInstance, _counterSet));
            lock (_lockObject)
            {
                if (_instanceCreated)
                    throw new InvalidOperationException(SR.Format(SR.AddCounterAfterInstance, _counterSet));
                if (_stringToId.ContainsKey(counterName))
                    throw new ArgumentException(SR.Format(SR.CounterNameAlreadyExists, counterName, _counterSet), nameof(counterName));
                if (_idToCounter.ContainsKey(counterId))
                    throw new ArgumentException(SR.Format(SR.CounterAlreadyExists, counterId, _counterSet), nameof(counterId));
                _stringToId.Add(counterName, counterId);
                _idToCounter.Add(counterId, counterType);
            }
        }

        public CounterSetInstance CreateCounterSetInstance(string instanceName)
        {
            ArgumentNullException.ThrowIfNull(instanceName);
            if (instanceName.Length == 0)
                throw new ArgumentException(SR.EmptyInstanceName, nameof(instanceName));
            if (_provider == null)
                throw new InvalidOperationException(SR.Format(SR.NoActiveProvider, _providerGuid));
            if (!_instanceCreated)
            {
                lock (_lockObject)
                {
                    if (!_instanceCreated)
                    {
                        if (_provider == null || _provider._hProvider.IsInvalid)
                            throw new InvalidOperationException(SR.Format(SR.NoActiveProvider, _providerGuid));
                        if (_idToCounter.Count == 0)
                            throw new InvalidOperationException(SR.Format(SR.CounterSetContainsNoCounter, _counterSet));
                        // PERF_COUNTERSET_INFO, then a PERF_COUNTER_INFO per counter: each a pointer-sized value
                        // read by reference (PERF_ATTRIB_BY_REFERENCE), novice detail, default scale
                        var counters = new List<PerfLib.CounterInfo>();
                        uint offset = 0;
                        foreach (KeyValuePair<int, CounterType> def in _idToCounter)
                        {
                            counters.Add(new PerfLib.CounterInfo
                            {
                                CounterId = (uint)def.Key, CounterType = (uint)def.Value, Attrib = 1,
                                Size = (uint)IntPtr.Size, DetailLevel = 100, Scale = 0, Offset = offset,
                            });
                            offset += (uint)IntPtr.Size;
                        }
                        uint status = PerfLib.SetCounterSetInfo(_provider._hProvider, _counterSet, _providerGuid, (uint)_instType, counters);
                        if (status != PerfLib.ERROR_SUCCESS)
                        {
                            throw status == PerfLib.ERROR_ALREADY_EXISTS
                                ? new InvalidOperationException(SR.Format(SR.CounterSetAlreadyRegister, _counterSet))
                                : new Win32Exception((int)status);
                        }
                        Interlocked.Increment(ref _provider._counterSet);
                        _instanceCreated = true;
                    }
                }
            }
            return new CounterSetInstance(this, instanceName);
        }
    }

    public sealed partial class CounterSetInstance : IDisposable
    {
        internal CounterSet _counterSet;
        internal string _instName;
        private int _active;
        internal IntPtr _nativeInst;

        internal CounterSetInstance(CounterSet counterSetDefined, string instanceName)
        {
            ArgumentNullException.ThrowIfNull(counterSetDefined);
            ArgumentNullException.ThrowIfNull(instanceName);
            if (instanceName.Length == 0)
                throw new ArgumentException(SR.EmptyInstanceName, nameof(instanceName));
            _counterSet = counterSetDefined;
            _instName = instanceName;
            _nativeInst = PerfLib.CreateInstance(_counterSet._provider._hProvider, _counterSet._counterSet, _instName, 0, out int status);
            if (_nativeInst == IntPtr.Zero)
            {
                switch (status)
                {
                    case (int)PerfLib.ERROR_ALREADY_EXISTS:
                        throw new ArgumentException(SR.Format(SR.InstanceAlreadyExists, _instName, _counterSet._counterSet), nameof(instanceName));
                    case (int)PerfLib.ERROR_NOT_FOUND:
                        throw new InvalidOperationException(SR.Format(SR.CounterSetNotInstalled, _counterSet._counterSet));
                    case (int)PerfLib.ERROR_INVALID_PARAMETER:
                        if (_counterSet._instType == CounterSetInstanceType.Single)
                            throw new ArgumentException(SR.Format(SR.InvalidInstance, _counterSet._counterSet), nameof(instanceName));
                        throw new Win32Exception(status);
                    default:
                        throw new Win32Exception(status);
                }
            }
            Counters = new CounterSetInstanceCounterDataSet(this);
            _active = 1;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~CounterSetInstance()
        {
            Dispose(false);
        }

        private void Dispose(bool disposing)
        {
            if (disposing && Counters != null)
            {
                Counters.Dispose();
                Counters = null;
            }
            if (_nativeInst != IntPtr.Zero && Interlocked.Exchange(ref _active, 0) != 0)
            {
                lock (_counterSet)
                {
                    if (_counterSet._provider != null)
                        PerfLib.DeleteInstance(_counterSet._provider._hProvider, _nativeInst);
                    _nativeInst = IntPtr.Zero;
                }
            }
        }

        public CounterSetInstanceCounterDataSet Counters { get; private set; }
    }

    public sealed partial class CounterData
    {
        private readonly unsafe long* _offset;

        internal unsafe CounterData(long* pCounterData)
        {
            _offset = pCounterData;
            *_offset = 0;
        }

        public unsafe long Value
        {
            get => Interlocked.Read(ref *_offset);
            set => Interlocked.Exchange(ref *_offset, value);
        }

        public unsafe void Increment() => Interlocked.Increment(ref *_offset);

        public unsafe void Decrement() => Interlocked.Decrement(ref *_offset);

        public unsafe void IncrementBy(long value) => Interlocked.Add(ref *_offset, value);

        public unsafe long RawValue
        {
            get => *_offset;
            set => *_offset = value;
        }
    }

    public sealed partial class CounterSetInstanceCounterDataSet : IDisposable
    {
        internal CounterSetInstance _instance;
        private readonly Dictionary<int, CounterData> _counters;
        private int _disposed;
        internal unsafe byte* _dataBlock;

        internal unsafe CounterSetInstanceCounterDataSet(CounterSetInstance thisInst)
        {
            _instance = thisInst;
            _counters = new Dictionary<int, CounterData>();
            if (_instance._counterSet._provider == null)
                throw new ArgumentException(SR.Format(SR.ProviderNotFound, _instance._counterSet._providerGuid), "ProviderGuid");
            if (_instance._counterSet._provider._hProvider.IsInvalid)
                throw new InvalidOperationException(SR.Format(SR.NoActiveProvider, _instance._counterSet._providerGuid));
            _dataBlock = (byte*)Marshal.AllocHGlobal(_instance._counterSet._idToCounter.Count * sizeof(long));
            if (_dataBlock == null)
                throw new InsufficientMemoryException(SR.Format(SR.InstanceCounterBlock, _instance._counterSet._counterSet, _instance._instName));
            int counterOffset = 0;
            foreach (KeyValuePair<int, CounterType> def in _instance._counterSet._idToCounter)
            {
                long* address = (long*)(_dataBlock + counterOffset * sizeof(long));
                _counters.Add(def.Key, new CounterData(address));
                uint status = PerfLib.SetCounterRefValue(_instance._counterSet._provider._hProvider, _instance._nativeInst, (uint)def.Key, (IntPtr)address);
                if (status != PerfLib.ERROR_SUCCESS)
                {
                    DisposeCore();
                    throw status == PerfLib.ERROR_NOT_FOUND
                        ? new InvalidOperationException(SR.Format(SR.CounterRefValue, _instance._counterSet._counterSet, def.Key, _instance._instName))
                        : new Win32Exception((int)status);
                }
                counterOffset++;
            }
        }

        public void Dispose()
        {
            DisposeCore();
            GC.SuppressFinalize(this);
        }

        ~CounterSetInstanceCounterDataSet()
        {
            DisposeCore();
        }

        private unsafe void DisposeCore()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && _dataBlock != null)
            {
                Marshal.FreeHGlobal((IntPtr)_dataBlock);
                _dataBlock = null;
            }
        }

        public CounterData this[int counterId] => _disposed != 0 ? null : _counters.TryGetValue(counterId, out CounterData data) ? data : null;

        public CounterData this[string counterName]
        {
            get
            {
                ArgumentNullException.ThrowIfNull(counterName);
                if (counterName.Length == 0)
                    throw new ArgumentNullException(nameof(counterName));
                if (_disposed != 0)
                    return null;
                return _instance._counterSet._stringToId.TryGetValue(counterName, out int id) && _counters.TryGetValue(id, out CounterData data) ? data : null;
            }
        }
    }

    internal sealed class PerfProvider
    {
        internal Guid _providerGuid;
        internal int _counterSet;
        internal SafePerfProviderHandle _hProvider;

        internal PerfProvider(Guid providerGuid)
        {
            _providerGuid = providerGuid;
            uint status = PerfLib.StartProvider(_providerGuid, out _hProvider);
            if (status != PerfLib.ERROR_SUCCESS)
                throw new Win32Exception((int)status);
        }
    }

    internal static class PerfProviderCollection
    {
        private static readonly object s_lockObject = new object();
        private static readonly List<PerfProvider> s_providerList = new List<PerfProvider>();
        private static readonly HashSet<Guid> s_counterSetList = new HashSet<Guid>();

        internal static PerfProvider QueryProvider(Guid providerGuid)
        {
            lock (s_lockObject)
            {
                foreach (PerfProvider entry in s_providerList)
                    if (entry._providerGuid == providerGuid)
                        return entry;
                var provider = new PerfProvider(providerGuid);
                s_providerList.Add(provider);
                return provider;
            }
        }

        internal static void RemoveProvider(Guid providerGuid)
        {
            lock (s_lockObject)
            {
                PerfProvider matched = s_providerList.Find(p => p._providerGuid == providerGuid);
                if (matched != null)
                {
                    matched._hProvider.Dispose();
                    s_providerList.Remove(matched);
                }
            }
        }

        internal static void RegisterCounterSet(Guid counterSetGuid)
        {
            lock (s_lockObject)
            {
                if (!s_counterSetList.Add(counterSetGuid))
                    throw new ArgumentException(SR.Format(SR.CounterSetAlreadyRegister, counterSetGuid), nameof(counterSetGuid));
            }
        }

        internal static void UnregisterCounterSet(Guid counterSetGuid)
        {
            lock (s_lockObject)
                s_counterSetList.Remove(counterSetGuid);
        }
    }

    internal sealed class SafePerfProviderHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePerfProviderHandle() : base(true)
        {
        }

        internal SafePerfProviderHandle(IntPtr handle) : base(true) => SetHandle(handle);

        protected override bool ReleaseHandle()
        {
            PerfLib.StopProvider(handle);
            return true;
        }
    }

    /// <summary>PerfLib version 2: advapi32's on Windows, its rules kept in this process elsewhere.</summary>
    internal static class PerfLib
    {
        internal const uint ERROR_SUCCESS = 0, ERROR_INVALID_PARAMETER = 87, ERROR_ALREADY_EXISTS = 183, ERROR_NOT_FOUND = 1168;

        internal struct CounterInfo
        {
            public uint CounterId;
            public uint CounterType;
            public ulong Attrib;
            public uint Size;
            public uint DetailLevel;
            public int Scale;
            public uint Offset;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CounterSetInfo
        {
            public Guid CounterSetGuid;
            public Guid ProviderGuid;
            public uint NumCounters;
            public uint InstanceType;
        }

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint PerfStartProvider(ref Guid providerGuid, IntPtr controlCallback, out SafePerfProviderHandle provider);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint PerfStopProvider(IntPtr provider);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern unsafe uint PerfSetCounterSetInfo(SafePerfProviderHandle provider, byte* template, uint templateSize);

        [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr PerfCreateInstance(SafePerfProviderHandle provider, ref Guid counterSetGuid, string name, uint id);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint PerfDeleteInstance(SafePerfProviderHandle provider, IntPtr instance);

        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint PerfSetCounterRefValue(SafePerfProviderHandle provider, IntPtr instance, uint counterId, IntPtr address);

        // ---- the in-process PerfLib -----------------------------------------------------------------

        private sealed class Provider
        {
            public Guid Guid;
            public readonly Dictionary<Guid, (uint InstanceType, HashSet<uint> Counters)> Sets = new();
            public readonly Dictionary<IntPtr, Instance> Instances = new();
        }

        private sealed class Instance
        {
            public Guid CounterSet;
            public string Name;
            public uint Id;
            public readonly Dictionary<uint, IntPtr> Values = new();
        }

        private static readonly Dictionary<IntPtr, Provider> s_providers = new();
        private static long s_next = 0x50460000;   // "PF"

        private static IntPtr NextHandle() => (IntPtr)Interlocked.Increment(ref s_next);

        internal static uint StartProvider(Guid providerGuid, out SafePerfProviderHandle provider)
        {
            if (OperatingSystem.IsWindows())
                return PerfStartProvider(ref providerGuid, IntPtr.Zero, out provider);
            IntPtr handle = NextHandle();
            lock (s_providers)
                s_providers[handle] = new Provider { Guid = providerGuid };
            provider = new SafePerfProviderHandle(handle);
            return ERROR_SUCCESS;
        }

        internal static void StopProvider(IntPtr handle)
        {
            if (OperatingSystem.IsWindows())
            {
                PerfStopProvider(handle);
                return;
            }
            lock (s_providers)
                s_providers.Remove(handle);
        }

        internal static unsafe uint SetCounterSetInfo(SafePerfProviderHandle provider, Guid counterSet, Guid providerGuid, uint instanceType, List<CounterInfo> counters)
        {
            if (OperatingSystem.IsWindows())
            {
                int size = sizeof(CounterSetInfo) + counters.Count * 32;
                byte[] buffer = new byte[size];
                fixed (byte* p = buffer)
                {
                    *(CounterSetInfo*)p = new CounterSetInfo
                    {
                        CounterSetGuid = counterSet, ProviderGuid = providerGuid,
                        NumCounters = (uint)counters.Count, InstanceType = instanceType,
                    };
                    for (int i = 0; i < counters.Count; i++)
                    {
                        byte* c = p + sizeof(CounterSetInfo) + i * 32;
                        CounterInfo info = counters[i];
                        *(uint*)c = info.CounterId;
                        *(uint*)(c + 4) = info.CounterType;
                        *(ulong*)(c + 8) = info.Attrib;
                        *(uint*)(c + 16) = info.Size;
                        *(uint*)(c + 20) = info.DetailLevel;
                        *(int*)(c + 24) = info.Scale;
                        *(uint*)(c + 28) = info.Offset;
                    }
                    return PerfSetCounterSetInfo(provider, p, (uint)size);
                }
            }
            lock (s_providers)
            {
                if (!s_providers.TryGetValue(provider.DangerousGetHandle(), out Provider p))
                    return ERROR_INVALID_PARAMETER;
                if (p.Sets.ContainsKey(counterSet))
                    return ERROR_ALREADY_EXISTS;
                var ids = new HashSet<uint>();
                foreach (CounterInfo c in counters)
                    ids.Add(c.CounterId);
                p.Sets[counterSet] = (instanceType, ids);
                return ERROR_SUCCESS;
            }
        }

        internal static IntPtr CreateInstance(SafePerfProviderHandle provider, Guid counterSet, string name, uint id, out int status)
        {
            if (OperatingSystem.IsWindows())
            {
                IntPtr made = PerfCreateInstance(provider, ref counterSet, name, id);
                status = made != IntPtr.Zero ? 0 : Marshal.GetLastPInvokeError();
                return made;
            }
            lock (s_providers)
            {
                if (!s_providers.TryGetValue(provider.DangerousGetHandle(), out Provider p))
                {
                    status = (int)ERROR_INVALID_PARAMETER;
                    return IntPtr.Zero;
                }
                if (!p.Sets.TryGetValue(counterSet, out var set))
                {
                    status = (int)ERROR_NOT_FOUND;
                    return IntPtr.Zero;
                }
                bool single = set.InstanceType == (uint)CounterSetInstanceType.Single || set.InstanceType == (uint)CounterSetInstanceType.GlobalAggregate || set.InstanceType == (uint)CounterSetInstanceType.GlobalAggregateWithHistory;
                foreach (Instance existing in p.Instances.Values)
                {
                    if (existing.CounterSet != counterSet)
                        continue;
                    if (single)
                    {
                        status = (int)ERROR_INVALID_PARAMETER;
                        return IntPtr.Zero;
                    }
                    if (existing.Id == id && string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        status = (int)ERROR_ALREADY_EXISTS;
                        return IntPtr.Zero;
                    }
                }
                IntPtr handle = NextHandle();
                p.Instances[handle] = new Instance { CounterSet = counterSet, Name = name, Id = id };
                status = 0;
                return handle;
            }
        }

        internal static uint DeleteInstance(SafePerfProviderHandle provider, IntPtr instance)
        {
            if (OperatingSystem.IsWindows())
                return PerfDeleteInstance(provider, instance);
            lock (s_providers)
                return s_providers.TryGetValue(provider.DangerousGetHandle(), out Provider p) && p.Instances.Remove(instance) ? ERROR_SUCCESS : ERROR_NOT_FOUND;
        }

        internal static uint SetCounterRefValue(SafePerfProviderHandle provider, IntPtr instance, uint counterId, IntPtr address)
        {
            if (OperatingSystem.IsWindows())
                return PerfSetCounterRefValue(provider, instance, counterId, address);
            lock (s_providers)
            {
                if (!s_providers.TryGetValue(provider.DangerousGetHandle(), out Provider p) || !p.Instances.TryGetValue(instance, out Instance inst)
                    || !p.Sets.TryGetValue(inst.CounterSet, out var set) || !set.Counters.Contains(counterId))
                    return ERROR_NOT_FOUND;
                inst.Values[counterId] = address;
                return ERROR_SUCCESS;
            }
        }
    }

    /// <summary>.NET's messages.</summary>
    internal static class SR
    {
        internal const string CounterAlreadyExists = "CounterId '{0}' already added to CounterSet '{1}'.";
        internal const string CounterNameAlreadyExists = "CounterName '{0}' already added to CounterSet '{1}'.";
        internal const string CounterSetAlreadyRegister = "CounterSet '{0}' already registered.";
        internal const string EmptyCounterName = "Non-empty counterName required.";
        internal const string EmptyInstanceName = "Non-empty instanceName required.";
        internal const string InstanceAlreadyExists = "Instance '{0}' already exists in CounterSet '{1}'.";
        internal const string InvalidCounterSetInstanceType = "CounterSetInstanceType '{0}' is not a valid CounterSetInstanceType.";
        internal const string InvalidCounterType = "CounterType '{0}' is not a valid CounterType.";
        internal const string InvalidInstance = "Single instance type CounterSet '{0}' can only have 1 CounterSetInstance.";
        internal const string ProviderNotFound = "CounterSet provider '{0}' not found.";
        internal const string InstanceCounterBlock = "Cannot allocate raw counter data for CounterSet '{0}' Instance '{1}'.";
        internal const string AddCounterAfterInstance = "Cannot AddCounter to CounterSet '{0}' after CreateCounterSetInstance.";
        internal const string CounterRefValue = "Cannot locate raw counter data location for CounterSet '{0}', Counter '{1}, in Instance '{2}'.";
        internal const string CounterSetContainsNoCounter = "CounterSet '{0}' does not include any counters.";
        internal const string CounterSetNotInstalled = "CounterSet '{0}' not installed yet.";
        internal const string NoActiveProvider = "CounterSet provider '{0}' not active.";

        internal static string Format(string format, params object[] args) => string.Format(format, args);
    }
}
