using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.ServiceProcess;
using Xunit;

namespace WpfMac.Tests;

/// <summary>The macOS replacement assemblies, through their public API, as an app uses them.</summary>
public class ShimTests
{
    [Fact]
    public void Wmi_answers_a_query_with_this_machine()
    {
        var results = new ManagementObjectSearcher("SELECT Name, NumberOfLogicalProcessors FROM Win32_ComputerSystem").Get();
        ManagementBaseObject o = Assert.Single(results.Cast<ManagementBaseObject>());
        Assert.Equal(Environment.MachineName.ToUpperInvariant(), o["Name"]);
        Assert.IsType<uint>(o["NumberOfLogicalProcessors"]);              // WMI's uint32, not int
        Assert.Throws<ManagementException>(() => o["Model"]);              // not selected
    }

    [Fact]
    public void Wmi_filters_with_WHERE()
    {
        int self = Environment.ProcessId;
        var all = new ManagementObjectSearcher($"SELECT ProcessId FROM Win32_Process WHERE ProcessId = {self} OR Name LIKE '%no such process%'").Get();
        Assert.Equal((uint)self, Assert.Single(all.Cast<ManagementBaseObject>())["ProcessId"]);
        Assert.Empty(new ManagementObjectSearcher("SELECT * FROM Win32_Service WHERE Name = 'MSSQLSERVER'").Get().Cast<object>());
    }

    [Fact]
    public void Wmi_reports_errors_as_Windows_does()
    {
        var bad = new ManagementObjectSearcher("SELEC * FROM Win32_BIOS").Get();
        Assert.Equal(ManagementStatus.InvalidQuery, Assert.Throws<ManagementException>(() => bad.Count).ErrorCode);
        var sql = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\Microsoft\SqlServer\ComputerManagement14"), new ObjectQuery("SELECT * FROM SqlService"));
        Assert.Equal(ManagementStatus.InvalidNamespace, Assert.Throws<ManagementException>(() => sql.Get()).ErrorCode);
    }

    [Theory]
    [InlineData(@"\\SERVER\root\cimv2:Win32_LogicalDisk.DeviceID=""C:""", "SERVER", @"root\cimv2", "Win32_LogicalDisk", true)]
    [InlineData(@"Win32_LogicalDisk.DeviceID=""/Volumes/Data""", "", "", "Win32_LogicalDisk", true)]
    [InlineData("Win32_Processor", "", "", "Win32_Processor", false)]
    [InlineData(@"root\cimv2", "", @"root\cimv2", "", false)]
    public void ManagementPath_parses_paths(string text, string server, string ns, string cls, bool instance)
    {
        var p = new ManagementPath(text);
        Assert.Equal((server, ns, cls, instance), (p.Server, p.NamespacePath, p.ClassName, p.IsInstance));
    }

    [Fact]
    public void Dmtf_dates_round_trip()
    {
        var d = new DateTime(2026, 10, 12, 4, 30, 15, 250, DateTimeKind.Local);
        Assert.Equal(d, ManagementDateTimeConverter.ToDateTime(ManagementDateTimeConverter.ToDmtfDateTime(d)));
        Assert.Equal(TimeSpan.FromHours(30.5), ManagementDateTimeConverter.ToTimeSpan(ManagementDateTimeConverter.ToDmtfTimeInterval(TimeSpan.FromHours(30.5))));
    }

    [Fact]
    public void ProtectedData_round_trips_and_checks_entropy()
    {
        byte[] secret = { 1, 2, 3, 4 }, entropy = { 9 };
        byte[] blob = ProtectedData.Protect(secret, entropy, DataProtectionScope.CurrentUser);
        Assert.Equal(secret, ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser));
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(blob, entropy, DataProtectionScope.LocalMachine));
        blob[^1] ^= 0xFF;
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(blob, entropy, DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void ServiceController_finds_no_services()
    {
        Assert.Empty(ServiceController.GetServices());
        var e = Assert.Throws<InvalidOperationException>(() => new ServiceController("Spooler").Status);
        Assert.Equal(1060, Assert.IsType<System.ComponentModel.Win32Exception>(e.InnerException).NativeErrorCode);
    }

    [Fact]
    public void PerformanceCounters_an_app_creates_count()
    {
        string category = "WpfMacTests" + Guid.NewGuid().ToString("N")[..8];
        Assert.Throws<InvalidOperationException>(() => new PerformanceCounter(category, "Requests", false));
        PerformanceCounterCategory.Create(category, "", PerformanceCounterCategoryType.SingleInstance,
            new CounterCreationDataCollection { new CounterCreationData("Requests", "", PerformanceCounterType.NumberOfItems64) });
        var counter = new PerformanceCounter(category, "Requests", false);
        counter.IncrementBy(5);
        counter.Decrement();
        Assert.Equal(4, new PerformanceCounter(category, "Requests").RawValue);
        Assert.Throws<InvalidOperationException>(() => new PerformanceCounter(category, "Requests").Increment());
    }

    [Fact]
    public void PerformanceCounters_report_the_machine()
    {
        Assert.Contains("_Total", new PerformanceCounterCategory("Processor").GetInstanceNames());
        Assert.True(new PerformanceCounter("Memory", "Available MBytes").NextValue() > 0);
    }

    [Fact]
    public void EventLog_registers_sources_and_keeps_entries()
    {
        string source = "WpfMacTests" + Guid.NewGuid().ToString("N")[..8];
        Assert.False(EventLog.SourceExists(source));
        EventLog.WriteEntry(source, "hello", EventLogEntryType.Warning, 7);
        Assert.Equal("Application", EventLog.LogNameFromSourceName(source, "."));
        EventLogEntry last = new EventLog("Application").Entries.Cast<EventLogEntry>().Last(e => e.Source == source);
        Assert.Equal(("hello", EventLogEntryType.Warning, 7L), (last.Message, last.EntryType, last.InstanceId));
        Assert.Throws<ArgumentException>(() => new EventLog("Application").WriteEntry("no source"));
    }
}
