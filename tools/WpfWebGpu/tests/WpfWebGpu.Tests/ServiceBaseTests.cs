using System.ServiceProcess;
using Xunit;

namespace WpfWebGpu.Tests;

public class ServiceBaseTests
{
    private sealed class Probe : ServiceBase
    {
        public readonly ManualResetEventSlim Started = new();
        public int Starts, Stops;
        public bool Fail;
        public Probe(string name) => ServiceName = name;
        protected override void OnStart(string[] args)
        {
            Starts++;
            if (Fail) throw new InvalidOperationException("no");
            Started.Set();
        }
        protected override void OnStop() => Stops++;
    }

    [Fact]
    public void Run_starts_the_services_and_returns_once_they_have_stopped()
    {
        var a = new Probe("A");
        var b = new Probe("B");
        var run = Task.Run(() => ServiceBase.Run(new ServiceBase[] { a, b }));
        Assert.True(a.Started.Wait(5000) && b.Started.Wait(5000));
        Assert.False(run.IsCompleted);
        a.Stop();
        Assert.False(run.Wait(200));                 // B still runs
        b.Stop();
        Assert.True(run.Wait(5000));
        Assert.Equal((1, 1), (a.Starts, a.Stops));
        Assert.Equal((1, 1), (b.Starts, b.Stops));
    }

    [Fact]
    public void A_service_that_fails_to_start_is_not_waited_for()
    {
        var bad = new Probe("Bad") { Fail = true };
        var run = Task.Run(() => ServiceBase.Run(bad));
        Assert.True(run.Wait(5000));
        Assert.Equal(1064, bad.ExitCode);
        Assert.Equal(0, bad.Stops);
        Environment.ExitCode = 0;
    }
}
