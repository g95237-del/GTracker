using System.Diagnostics;

namespace GTracker.App.Capture;

public sealed class CaptureCadence
{
    private readonly long _intervalTicks;
    private long _nextSampleTimestamp;
    private bool _started;

    public CaptureCadence(int framesPerSecond)
    {
        FramesPerSecond = Math.Clamp(framesPerSecond, 5, 30);
        _intervalTicks = Math.Max(1, Stopwatch.Frequency / FramesPerSecond);
    }

    public int FramesPerSecond { get; }

    public bool ShouldSample(long timestamp)
    {
        if (!_started)
        {
            _started = true;
            _nextSampleTimestamp = timestamp + _intervalTicks;
            return true;
        }
        if (timestamp < _nextSampleTimestamp) return false;

        var missedIntervals = Math.Max(0, (timestamp - _nextSampleTimestamp) / _intervalTicks);
        _nextSampleTimestamp += (missedIntervals + 1) * _intervalTicks;
        return true;
    }
}
