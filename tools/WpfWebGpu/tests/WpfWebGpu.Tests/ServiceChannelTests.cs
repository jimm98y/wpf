using System.Collections.Concurrent;
using System.ServiceProcess;
using WpfWebGpu.Services;
using Xunit;

namespace WpfWebGpu.Tests;

/// <summary>The controls systemd and launchd have no word for -- pause, continue, a program's own -- reaching a running
/// service over its channel, refused as the SCM refuses them, and the state it reports read back.</summary>
[Collection(ServiceDatabaseEnvironment.Name)]
public class ServiceChannelTests
{
    private sealed class Probe : ServiceBase
    {
        public readonly ManualResetEventSlim Started = new();
        public readonly ConcurrentQueue<string> Calls = new();
        public Probe(string name, bool pausable) { ServiceName = name; CanPauseAndContinue = pausable; }
        protected override void OnStart(string[] args) => Started.Set();
        protected override void OnPause() => Calls.Enqueue("pause");
        protected override void OnContinue() => Calls.Enqueue("continue");
        protected override void OnCustomCommand(int command) => Calls.Enqueue("command " + command);
        protected override void OnStop() => Calls.Enqueue("stop");
    }

    private static bool Until(Func<bool> condition)
    {
        for (int i = 0; i < 200; i++) { if (condition()) return true; Thread.Sleep(25); }
        return condition();
    }

    [Fact]
    public void A_running_service_is_paused_continued_and_given_its_own_commands()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows' service control manager delivers them itself");
        // a socket's path is short: a folder of its own under /tmp
        string dir = Path.Combine("/tmp", "wsc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, ServiceDatabase.FileName);
        string? previous = Environment.GetEnvironmentVariable("WPFWEBGPU_SERVICES");
        Environment.SetEnvironmentVariable("WPFWEBGPU_SERVICES", path);
        try
        {
            var pausable = new Probe("Pausable", pausable: true);
            var plain = new Probe("Plain", pausable: false);
            var run = Task.Run(() => ServiceBase.Run(new ServiceBase[] { pausable, plain }));
            Assert.True(pausable.Started.Wait(5000) && plain.Started.Wait(5000));
            Assert.True(Until(() => ServiceChannel.Query(path, "Pausable", 0)?.State == ServiceState.Running));
            Assert.Equal(ServiceChannel.AcceptStop | ServiceChannel.AcceptPauseContinue, ServiceChannel.Query(path, "Pausable", 0)!.Value.Accepted);
            Assert.Equal(ServiceChannel.AcceptStop, ServiceChannel.Query(path, "plain", 0)!.Value.Accepted);   // the name as the SCM takes it: any case

            Assert.Equal(0, ServiceChannel.Send(path, "Pausable", 0, ServiceChannel.Pause).Error);
            Assert.True(Until(() => ServiceChannel.Query(path, "Pausable", 0)?.State == ServiceState.Paused));
            // paused, it takes a continue
            Assert.Equal(0, ServiceChannel.Send(path, "Pausable", 0, ServiceChannel.Continue).Error);
            Assert.True(Until(() => ServiceChannel.Query(path, "Pausable", 0)?.State == ServiceState.Running));
            Assert.Equal(0, ServiceChannel.Send(path, "Pausable", 0, 200).Error);
            Assert.True(Until(() => pausable.Calls.Contains("command 200")));
            Assert.Equal(new[] { "pause", "continue", "command 200" }, pausable.Calls);

            // what it does not accept, refused as the SCM refuses it; another process's id is not the service's
            Assert.Equal(ServiceManager.ERROR_INVALID_SERVICE_CONTROL, ServiceChannel.Send(path, "Plain", 0, ServiceChannel.Pause).Error);
            Assert.Equal(ServiceManager.ERROR_INVALID_SERVICE_CONTROL, ServiceChannel.Send(path, "Plain", 0, ServiceChannel.Shutdown).Error);
            Assert.Equal(ServiceManager.ERROR_INVALID_SERVICE_CONTROL, ServiceChannel.Send(path, "Plain", 0, 6).Error);
            Assert.Null(ServiceChannel.Query(path, "Plain", Environment.ProcessId + 100_000));
            Assert.Equal(ServiceManager.ERROR_SERVICE_NOT_ACTIVE, ServiceChannel.Send(path, "Missing", 0, 200).Error);
            Assert.Empty(plain.Calls);

            // a stop over the channel stops that one; the system's stops the rest
            Assert.Equal(0, ServiceChannel.Send(path, "Plain", 0, ServiceChannel.Stop).Error);
            Assert.True(Until(() => plain.Calls.Contains("stop")));
            Assert.False(run.IsCompleted);
            Assert.Equal(0, ServiceChannel.Send(path, "Pausable", 0, ServiceChannel.Stop).Error);
            Assert.True(run.Wait(5000));
            Assert.Null(ServiceChannel.Query(path, "Pausable", 0));      // gone with the service
        }
        finally
        {
            Environment.SetEnvironmentVariable("WPFWEBGPU_SERVICES", previous);
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(ServiceState.Stopped, 0u, 200u, ServiceManager.ERROR_SERVICE_NOT_ACTIVE)]
    [InlineData(ServiceState.StartPending, 0u, 2u, ServiceChannel.ERROR_SERVICE_CANNOT_ACCEPT_CTRL)]
    [InlineData(ServiceState.StartPending, 0u, 4u, 0)]
    [InlineData(ServiceState.Running, 1u, 2u, ServiceManager.ERROR_INVALID_SERVICE_CONTROL)]
    [InlineData(ServiceState.Running, 3u, 2u, 0)]
    [InlineData(ServiceState.Paused, 3u, 3u, 0)]
    [InlineData(ServiceState.Running, 0u, 128u, 0)]
    [InlineData(ServiceState.Running, 0u, 1u, ServiceManager.ERROR_INVALID_SERVICE_CONTROL)]
    public void A_control_is_admitted_as_the_SCM_admits_it(ServiceState state, uint accepted, uint control, int error) =>
        Assert.Equal(error, ServiceChannel.Admit(control, new ServiceChannel.Reported(state, accepted)));
}
