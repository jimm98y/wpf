// A counter's raw reading and how Windows turns two readings into a value (the PERF_* formulas).

using System.Runtime.InteropServices;

namespace System.Diagnostics
{
    [StructLayout(LayoutKind.Sequential)]
    public readonly partial struct CounterSample : IEquatable<CounterSample>
    {
        public static CounterSample Empty = new CounterSample(0, 0, 0, 0, 0, 0, PerformanceCounterType.NumberOfItems32);

        private readonly long _rawValue;
        private readonly long _baseValue;
        private readonly long _timeStamp;
        private readonly long _counterFrequency;
        private readonly PerformanceCounterType _counterType;
        private readonly long _timeStamp100nSec;
        private readonly long _systemFrequency;
        private readonly long _counterTimeStamp;

        public CounterSample(long rawValue, long baseValue, long counterFrequency, long systemFrequency, long timeStamp, long timeStamp100nSec, PerformanceCounterType counterType)
            : this(rawValue, baseValue, counterFrequency, systemFrequency, timeStamp, timeStamp100nSec, counterType, 0) { }

        public CounterSample(long rawValue, long baseValue, long counterFrequency, long systemFrequency, long timeStamp, long timeStamp100nSec, PerformanceCounterType counterType, long counterTimeStamp)
        {
            _rawValue = rawValue;
            _baseValue = baseValue;
            _timeStamp = timeStamp;
            _counterFrequency = counterFrequency;
            _counterType = counterType;
            _timeStamp100nSec = timeStamp100nSec;
            _systemFrequency = systemFrequency;
            _counterTimeStamp = counterTimeStamp;
        }

        public long BaseValue => _baseValue;
        public long CounterFrequency => _counterFrequency;
        public long CounterTimeStamp => _counterTimeStamp;
        public PerformanceCounterType CounterType => _counterType;
        public long RawValue => _rawValue;
        public long SystemFrequency => _systemFrequency;
        public long TimeStamp => _timeStamp;
        public long TimeStamp100nSec => _timeStamp100nSec;

        public static float Calculate(CounterSample counterSample) => CounterSampleCalculator.ComputeCounterValue(counterSample);

        public static float Calculate(CounterSample counterSample, CounterSample nextCounterSample) => CounterSampleCalculator.ComputeCounterValue(counterSample, nextCounterSample);

        public bool Equals(CounterSample sample) =>
            _rawValue == sample._rawValue && _baseValue == sample._baseValue && _timeStamp == sample._timeStamp
            && _counterFrequency == sample._counterFrequency && _counterType == sample._counterType
            && _timeStamp100nSec == sample._timeStamp100nSec && _systemFrequency == sample._systemFrequency
            && _counterTimeStamp == sample._counterTimeStamp;

        public override bool Equals(object o) => o is CounterSample s && Equals(s);

        public override int GetHashCode() => _rawValue.GetHashCode();

        public static bool operator ==(CounterSample a, CounterSample b) => a.Equals(b);

        public static bool operator !=(CounterSample a, CounterSample b) => !a.Equals(b);
    }

    public static partial class CounterSampleCalculator
    {
        public static float ComputeCounterValue(CounterSample newSample) => ComputeCounterValue(CounterSample.Empty, newSample);

        public static float ComputeCounterValue(CounterSample oldSample, CounterSample newSample)
        {
            long n1 = newSample.RawValue, n0 = oldSample.RawValue;
            long b1 = newSample.BaseValue, b0 = oldSample.BaseValue;
            // Two-reading formulas have no value until there is a first reading to compare with.
            bool first = oldSample.TimeStamp == 0 && oldSample.TimeStamp100nSec == 0;
            double frequency = newSample.CounterFrequency > 0 ? newSample.CounterFrequency : newSample.SystemFrequency;

            switch (newSample.CounterType)
            {
                case PerformanceCounterType.NumberOfItems32:
                case PerformanceCounterType.NumberOfItems64:
                case PerformanceCounterType.NumberOfItemsHEX32:
                case PerformanceCounterType.NumberOfItemsHEX64:
                    return n1;

                case PerformanceCounterType.RawFraction:
                    return b1 == 0 ? 0 : (float)(100.0 * n1 / b1);

                case PerformanceCounterType.ElapsedTime:
                    return newSample.CounterFrequency == 0 ? 0 : (float)((newSample.CounterTimeStamp - n1) / (double)newSample.CounterFrequency);

                case PerformanceCounterType.AverageBase:
                case PerformanceCounterType.CounterMultiBase:
                case PerformanceCounterType.RawBase:
                case PerformanceCounterType.SampleBase:
                    return 0;
            }

            if (first) return 0;
            double dt = newSample.TimeStamp - oldSample.TimeStamp;
            double dt100 = newSample.TimeStamp100nSec - oldSample.TimeStamp100nSec;
            double dn = n1 - n0, db = b1 - b0;
            double value = newSample.CounterType switch
            {
                PerformanceCounterType.CounterDelta32 or PerformanceCounterType.CounterDelta64 => dn,
                PerformanceCounterType.RateOfCountsPerSecond32 or PerformanceCounterType.RateOfCountsPerSecond64
                    or PerformanceCounterType.SampleCounter => dt <= 0 || frequency <= 0 ? 0 : dn / (dt / frequency),
                PerformanceCounterType.CountPerTimeInterval32 or PerformanceCounterType.CountPerTimeInterval64 => dt <= 0 ? 0 : dn / dt,
                PerformanceCounterType.SampleFraction => db == 0 ? 0 : 100.0 * dn / db,
                PerformanceCounterType.AverageTimer32 => db == 0 || frequency <= 0 ? 0 : (dn / frequency) / db,
                PerformanceCounterType.AverageCount64 => db == 0 ? 0 : dn / db,
                PerformanceCounterType.CounterTimer => dt <= 0 ? 0 : 100.0 * dn / dt,
                PerformanceCounterType.CounterTimerInverse => dt <= 0 ? 0 : 100.0 * (1 - dn / dt),
                PerformanceCounterType.Timer100Ns => dt100 <= 0 ? 0 : 100.0 * dn / dt100,
                PerformanceCounterType.Timer100NsInverse => dt100 <= 0 ? 0 : 100.0 * (1 - dn / dt100),
                PerformanceCounterType.CounterMultiTimer => dt <= 0 || b1 == 0 ? 0 : 100.0 * dn / dt / b1,
                PerformanceCounterType.CounterMultiTimerInverse => dt <= 0 || b1 == 0 ? 0 : 100.0 * (b1 - dn / dt),
                PerformanceCounterType.CounterMultiTimer100Ns => dt100 <= 0 || b1 == 0 ? 0 : 100.0 * dn / dt100 / b1,
                PerformanceCounterType.CounterMultiTimer100NsInverse => dt100 <= 0 || b1 == 0 ? 0 : 100.0 * (b1 - dn / dt100),
                _ => 0,
            };
            return value < 0 ? 0 : (float)value;
        }
    }
}
