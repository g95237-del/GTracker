using System.Diagnostics;
using GTracker.App.Capture;

namespace GTracker.App.Tests;

public sealed class CaptureCadenceTests
{
    [Fact]
    public void ShouldSample_MaintainsTargetAverageForThirtyHertzSource()
    {
        var cadence = new CaptureCadence(20);
        var samples = 0;
        for (var frame = 0; frame <= 90; frame++)
        {
            var timestamp = (long)Math.Round(frame * Stopwatch.Frequency / 30d);
            if (cadence.ShouldSample(timestamp)) samples++;
        }

        Assert.InRange(samples, 60, 61);
    }

    [Fact]
    public void ShouldSample_SelectsThirtyFramesPerSecondFromSixtyHertzSource()
    {
        var cadence = new CaptureCadence(30);
        var samples = 0;
        for (var frame = 0; frame <= 60; frame++)
        {
            var timestamp = (long)Math.Round(frame * Stopwatch.Frequency / 60d);
            if (cadence.ShouldSample(timestamp)) samples++;
        }

        Assert.InRange(samples, 30, 31);
    }

    [Fact]
    public void ShouldSample_DoesNotBurstAfterLongCaptureGap()
    {
        var cadence = new CaptureCadence(30);
        Assert.True(cadence.ShouldSample(0));
        Assert.True(cadence.ShouldSample(10 * Stopwatch.Frequency));
        Assert.False(cadence.ShouldSample(10 * Stopwatch.Frequency + Stopwatch.Frequency / 1000));
    }

    [Fact]
    public void Constructor_ClampsRequestedRateToSupportedMaximum()
    {
        Assert.Equal(30, new CaptureCadence(60).FramesPerSecond);
    }
}
