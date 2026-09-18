// Warm capture pool tests. These exercise the real WASAPI stack, so every test
// skips when the machine has no input device (same convention as the Live
// category skipping without the ASR/LLM servers): `dotnet test` stays green on
// device-less CI while still covering the real path on developer machines.

using Athena.App.Audio;
using NAudio.CoreAudioApi;
using Xunit;

namespace Athena.App.Tests;

public class WarmRecorderPoolTests
{
    private static bool HasCaptureDevice()
    {
        try
        {
            _ = WasapiCapture.GetDefaultCaptureDevice();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(50);
        }
        Assert.True(condition(), $"condition not met within {timeout.TotalSeconds}s");
    }

    [SkippableFact]
    public void Prewarm_publishes_a_prepared_spare()
    {
        Skip.IfNot(HasCaptureDevice(), "no audio capture device");

        using var pool = new WarmRecorderPool();
        WaitFor(() => pool.HasSpare, TimeSpan.FromSeconds(5));

        var recorder = pool.Take();
        Assert.True(recorder.IsWarmed, "Take() must return a graph that only needs Start()");
        recorder.Dispose();
    }

    [SkippableFact]
    public void Take_rearms_the_next_spare_for_back_to_back_dictations()
    {
        Skip.IfNot(HasCaptureDevice(), "no audio capture device");

        using var pool = new WarmRecorderPool();
        WaitFor(() => pool.HasSpare, TimeSpan.FromSeconds(5));

        var first = pool.Take();
        Assert.False(pool.HasSpare, "spare must be consumed by Take");

        WaitFor(() => pool.HasSpare, TimeSpan.FromSeconds(5));
        var second = pool.Take();
        Assert.True(second.IsWarmed);

        first.Dispose();
        second.Dispose();
    }

    [SkippableFact]
    public void Refresh_replaces_the_spare_with_a_fresh_graph()
    {
        Skip.IfNot(HasCaptureDevice(), "no audio capture device");

        using var pool = new WarmRecorderPool();
        WaitFor(() => pool.HasSpare, TimeSpan.FromSeconds(5));

        pool.Refresh();
        WaitFor(() => pool.HasSpare, TimeSpan.FromSeconds(5));

        var recorder = pool.Take();
        Assert.True(recorder.IsWarmed);
        recorder.Dispose();
    }

    [SkippableFact]
    public void Take_after_dispose_throws()
    {
        Skip.IfNot(HasCaptureDevice(), "no audio capture device");

        var pool = new WarmRecorderPool();
        WaitFor(() => pool.HasSpare, TimeSpan.FromSeconds(5));
        pool.Dispose();

        Assert.Throws<ObjectDisposedException>(pool.Take);
    }

    [Fact]
    public void Deviceless_machine_degrades_to_cold_takes_without_throwing()
    {
        if (HasCaptureDevice())
            return; // cooldown branch only reachable without a device

        using var pool = new WarmRecorderPool();
        Thread.Sleep(300); // give the prewarm task time to fail
        Assert.False(pool.HasSpare, "a failed prewarm must not publish a spare");

        // Take still works — the session just builds cold. This must never throw.
        using var recorder = pool.Take();
        Assert.False(recorder.IsWarmed);
    }
}
