// A category an app creates is published through .NET's metrics (System.Diagnostics.Metrics), on every OS --
// Windows too: where Windows' performance counters are watched with perfmon, the same counters are watched with
// dotnet-counters, OpenTelemetry's exporters or any MeterListener, on any OS. The category is a Meter of its name,
// each counter an instrument of the counter's name and help, an instance its "instance" tag:
//
//   a count (NumberOfItems*)                        a gauge of its value
//   a rate or a delta (RateOfCountsPerSecond*,      a counter of its total (what the counter counts up), the tools
//     CountPerTimeInterval*, CounterDelta*, ...)      making the rate of it as perfmon does
//   a fraction (RawFraction, SampleFraction)        a gauge of its percentage of its base
//   an average (AverageCount64, AverageTimer32)     a gauge of its total over its base (seconds, for a timer)
//   a base (AverageBase, RawBase, ...)              no instrument: it is the counter before it's denominator
//
// Each is observed when a listener collects: the value the counter has then.

using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;

namespace System.Diagnostics
{
    internal static class CounterMetrics
    {
        /// <summary>The category's Meter, its instruments made; disposed when the category is deleted.</summary>
        public static Meter Publish(CustomCategory category)
        {
            var meter = new Meter(category.Name);
            var definitions = category.Definitions;
            for (int i = 0; i < definitions.Count; i++)
            {
                var (name, help, type) = definitions[i];
                if (IsBase(type)) continue;
                string baseName = i + 1 < definitions.Count && IsBase(definitions[i + 1].Type) ? definitions[i + 1].Name : null;
                string counter = name;
                switch (type)
                {
                    case PerformanceCounterType.NumberOfItems32:
                    case PerformanceCounterType.NumberOfItems64:
                    case PerformanceCounterType.NumberOfItemsHEX32:
                    case PerformanceCounterType.NumberOfItemsHEX64:
                        meter.CreateObservableGauge(name, () => Measure(category, counter, raw => raw), unit: null, description: help);
                        break;
                    case PerformanceCounterType.RawFraction:
                    case PerformanceCounterType.SampleFraction:
                        meter.CreateObservableGauge(name, () => Measure(category, counter, baseName, (raw, b) => b == 0 ? 0.0 : 100.0 * raw / b), unit: "%", description: help);
                        break;
                    case PerformanceCounterType.AverageCount64:
                        meter.CreateObservableGauge(name, () => Measure(category, counter, baseName, (raw, b) => b == 0 ? 0.0 : (double)raw / b), unit: null, description: help);
                        break;
                    case PerformanceCounterType.AverageTimer32:
                        meter.CreateObservableGauge(name, () => Measure(category, counter, baseName, (raw, b) => b == 0 ? 0.0 : (double)raw / Stopwatch.Frequency / b), unit: "s", description: help);
                        break;
                    default:
                        // a rate, a delta, a timer: the total it counts up
                        meter.CreateObservableCounter(name, () => Measure(category, counter, raw => raw), unit: null, description: help);
                        break;
                }
            }
            return meter;
        }

        private static bool IsBase(PerformanceCounterType type) => type is PerformanceCounterType.AverageBase or PerformanceCounterType.CounterMultiBase
            or PerformanceCounterType.RawBase or PerformanceCounterType.SampleBase;

        // the counter's value in each instance that has one (a single-instance category's: untagged)
        private static IEnumerable<Measurement<long>> Measure(CustomCategory category, string counter, Func<long, long> value)
        {
            foreach (string instance in Instances(category))
                yield return Tagged(value(category.Get(counter, instance)), instance);
        }

        private static IEnumerable<Measurement<double>> Measure(CustomCategory category, string counter, string baseCounter, Func<long, long, double> value)
        {
            foreach (string instance in Instances(category))
                yield return Tagged(value(category.Get(counter, instance), baseCounter != null ? category.Get(baseCounter, instance) : 0), instance);
        }

        private static string[] Instances(CustomCategory category)
        {
            string[] names = category.InstanceNames();
            return category.Type == PerformanceCounterCategoryType.MultiInstance ? names : names.Length > 0 ? names : new[] { "" };
        }

        private static Measurement<T> Tagged<T>(T value, string instance) where T : struct =>
            instance.Length == 0 ? new Measurement<T>(value) : new Measurement<T>(value, new KeyValuePair<string, object>("instance", instance));
    }
}
