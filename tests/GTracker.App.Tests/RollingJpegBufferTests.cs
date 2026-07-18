using System.Diagnostics;
using GTracker.App.Capture;

namespace GTracker.App.Tests;

public sealed class RollingJpegBufferTests
{
    [Fact]
    public void Add_EvictsFramesOutsideRetentionWindow()
    {
        var buffer = new RollingJpegBuffer(TimeSpan.FromSeconds(2), 64 * 1024 * 1024);
        buffer.Add(Frame(0, 1));
        buffer.Add(Frame(1, 2));
        buffer.Add(Frame(3, 3));

        var clip = buffer.Snapshot(0, 4 * Stopwatch.Frequency);

        Assert.Equal(2, clip.Frames.Count);
        Assert.Equal(2, clip.Frames[0].Data[0]);
        Assert.Equal(3, clip.Frames[1].Data[0]);
    }

    [Fact]
    public void Add_EnforcesEncodedByteLimit()
    {
        var buffer = new RollingJpegBuffer(TimeSpan.FromMinutes(1), 16 * 1024 * 1024);
        buffer.Add(new JpegFrame(0, new byte[9 * 1024 * 1024], 1, 1));
        buffer.Add(new JpegFrame(1, new byte[9 * 1024 * 1024], 1, 1));

        Assert.Equal(1, buffer.Count);
        Assert.True(buffer.Bytes <= 16 * 1024 * 1024);
    }

    [Fact]
    public void Add_RejectsSingleFrameLargerThanEncodedByteLimit()
    {
        var buffer = new RollingJpegBuffer(TimeSpan.FromMinutes(1), 16 * 1024 * 1024);

        buffer.Add(new JpegFrame(0, new byte[17 * 1024 * 1024], 1, 1));

        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.Bytes);
    }

    [Fact]
    public void BufferedDuration_ReportsRetainedMonotonicRange()
    {
        var buffer = new RollingJpegBuffer(TimeSpan.FromSeconds(10), 64 * 1024 * 1024);
        buffer.Add(Frame(4.25, 1));
        buffer.Add(Frame(6.75, 2));

        Assert.Equal(TimeSpan.FromSeconds(2.5), buffer.BufferedDuration);
    }

    [Fact]
    public void Snapshot_UsesMonotonicRelativeOffsets()
    {
        var buffer = new RollingJpegBuffer(TimeSpan.FromSeconds(10), 64 * 1024 * 1024);
        buffer.Add(Frame(5.0, 1));
        buffer.Add(Frame(5.5, 2));
        buffer.Add(Frame(6.25, 3));

        var clip = buffer.Snapshot((long)(4.9 * Stopwatch.Frequency), (long)(6.3 * Stopwatch.Frequency));

        Assert.Equal([0, 500, 1250], clip.Frames.Select(frame => frame.OffsetMilliseconds));
        Assert.Equal(1250, clip.DurationMilliseconds);
        Assert.Equal(2, clip.FindNearest(600)!.Data[0]);
    }

    [Fact]
    public void Snapshot_ByUtcSelectsEventWindowAndPreservesCaptureTimes()
    {
        var startedAt = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
        var buffer = new RollingJpegBuffer(TimeSpan.FromSeconds(10), 64 * 1024 * 1024);
        buffer.Add(Frame(5.0, 1, startedAt));
        buffer.Add(Frame(5.5, 2, startedAt.AddMilliseconds(500)));
        buffer.Add(Frame(6.0, 3, startedAt.AddSeconds(1)));
        buffer.Add(Frame(6.5, 4, startedAt.AddMilliseconds(1500)));

        var clip = buffer.Snapshot(startedAt.AddMilliseconds(400), startedAt.AddMilliseconds(1100));

        Assert.Equal([2, 3], clip.Frames.Select(frame => frame.Data[0]));
        Assert.Equal([100, 600], clip.Frames.Select(frame => frame.OffsetMilliseconds));
        Assert.Equal(700, clip.DurationMilliseconds);
        Assert.Equal(startedAt.AddMilliseconds(400), clip.StartedAtUtc);
        Assert.Equal(startedAt.AddMilliseconds(1100), clip.EndedAtUtc);
    }

    [Fact]
    public void Trim_RebasesFramesAndDuration()
    {
        var clip = new CapturedClip(
            [new(0, [1], 1, 1), new(500, [2], 1, 1), new(1000, [3], 1, 1)], 1000);

        var trimmed = clip.Trim(400, 900);

        Assert.Equal(500, trimmed.DurationMilliseconds);
        Assert.Single(trimmed.Frames);
        Assert.Equal(100, trimmed.Frames[0].OffsetMilliseconds);
    }

    private static JpegFrame Frame(double seconds, byte marker) =>
        new((long)(seconds * Stopwatch.Frequency), [marker], 1, 1);

    private static JpegFrame Frame(double seconds, byte marker, DateTimeOffset capturedAtUtc) =>
        new((long)(seconds * Stopwatch.Frequency), [marker], 1, 1, capturedAtUtc);
}
