using AirSend.Core.Capture;

namespace AirSend.Core.Tests;

/// <summary>
/// The capture follows whatever Windows plays to, so switching the output device
/// has to be noticed. A WASAPI loopback client keeps reading from the endpoint it
/// was opened on (which then only delivers silence), which is why AirSend rebuilds
/// the capture when the default endpoint changes.
/// </summary>
public class AudioEndpointWatcherTests
{
    [Fact]
    public void TheFirstReadOnlySeedsTheBaseline()
    {
        string? device = "device-a";
        using var watcher = new AudioEndpointWatcher(() => device, pollIntervalMs: 10_000);
        List<string?> changes = [];
        watcher.DefaultOutputChanged += changes.Add;

        Assert.False(watcher.Refresh());
        Assert.Empty(changes);
        Assert.Equal("device-a", watcher.ObservedDefaultDeviceId);
    }

    [Fact]
    public void SwitchingTheDefaultOutputRaisesTheEventOnce()
    {
        string? device = "device-a";
        using var watcher = new AudioEndpointWatcher(() => device, pollIntervalMs: 10_000);
        List<string?> changes = [];
        watcher.DefaultOutputChanged += changes.Add;

        watcher.Refresh();

        device = "device-b";
        Assert.True(watcher.Refresh());
        Assert.Equal(new string?[] { "device-b" }, changes);

        // Polling again with the same endpoint must not raise anything.
        Assert.False(watcher.Refresh());
        Assert.Single(changes);
    }

    [Fact]
    public void LosingEveryOutputIsAlsoAChange()
    {
        string? device = "device-a";
        using var watcher = new AudioEndpointWatcher(() => device, pollIntervalMs: 10_000);
        List<string?> changes = [];
        watcher.DefaultOutputChanged += changes.Add;

        watcher.Refresh();
        device = null;

        Assert.True(watcher.Refresh());
        Assert.Equal(new string?[] { null }, changes);
        Assert.Null(watcher.ObservedDefaultDeviceId);
    }

    [Fact]
    public void AFailingReadKeepsTheLastObservedDevice()
    {
        string? device = "device-a";
        bool fail = false;
        using var watcher = new AudioEndpointWatcher(
            () => fail ? throw new InvalidOperationException("no device") : device,
            pollIntervalMs: 10_000);
        List<string?> changes = [];
        watcher.DefaultOutputChanged += changes.Add;

        watcher.Refresh();
        fail = true;

        // A transient COM failure is not a switch: no event, and the baseline stays.
        Assert.False(watcher.Refresh());
        Assert.Empty(changes);
        Assert.Equal("device-a", watcher.ObservedDefaultDeviceId);
    }

    [Fact]
    public async Task ThePollLoopNoticesASwitchWithoutAnyoneAsking()
    {
        string? device = "device-a";
        using var watcher = new AudioEndpointWatcher(() => device, pollIntervalMs: 25);
        List<string?> changes = [];
        watcher.DefaultOutputChanged += changes.Add;

        watcher.Start();

        // Wait for the first poll to seed the baseline, otherwise the switch below
        // could happen before the loop ever read the old endpoint.
        Assert.True(await WaitForAsync(() => watcher.ObservedDefaultDeviceId == "device-a"));
        device = "device-b";

        Assert.True(await WaitForAsync(() => changes.Count == 1));
        Assert.Equal("device-b", changes[0]);
        Assert.Equal("device-b", watcher.ObservedDefaultDeviceId);
    }

    [Fact]
    public void StartingTwiceOrDisposingTwiceIsHarmless()
    {
        using var watcher = new AudioEndpointWatcher(() => "device-a", pollIntervalMs: 25);

        watcher.Start();
        watcher.Start();
        watcher.Dispose();
        watcher.Dispose();

        // Refresh after disposal must not throw either.
        watcher.Refresh();
    }

    /// <summary>
    /// Exercises the real COM path the watcher polls through. Machines without any
    /// playback endpoint (headless CI) legitimately answer null; the point is that
    /// reading it never throws and returns an endpoint id when one exists.
    /// </summary>
    [Fact]
    public void ReadingTheSystemDefaultUsesTheMmDeviceApi()
    {
        string? id = WasapiDevices.GetDefaultRenderDeviceId();

        Assert.True(
            id is null || id.StartsWith("{0.0.0.", StringComparison.Ordinal),
            $"unexpected endpoint id: {id}");
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(15);
        }

        return condition();
    }
}
