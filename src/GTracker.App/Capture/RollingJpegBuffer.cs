using System.Diagnostics;

namespace GTracker.App.Capture;

public sealed record JpegFrame(long Timestamp, byte[] Data, int Width, int Height, DateTimeOffset CapturedAtUtc = default);

public sealed record ClipFrame(int OffsetMilliseconds, byte[] Data, int Width, int Height);

public sealed class CapturedClip
{
    public CapturedClip(
        IReadOnlyList<ClipFrame> frames,
        int durationMilliseconds,
        DateTimeOffset? startedAtUtc = null,
        DateTimeOffset? endedAtUtc = null)
    {
        Frames = frames;
        DurationMilliseconds = Math.Max(0, durationMilliseconds);
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
    }

    public IReadOnlyList<ClipFrame> Frames { get; }
    public int DurationMilliseconds { get; }
    public DateTimeOffset? StartedAtUtc { get; }
    public DateTimeOffset? EndedAtUtc { get; }

    public ClipFrame? FindNearest(int offsetMilliseconds)
    {
        if (Frames.Count == 0)
        {
            return null;
        }

        offsetMilliseconds = Math.Clamp(offsetMilliseconds, 0, DurationMilliseconds);
        var low = 0;
        var high = Frames.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (Frames[middle].OffsetMilliseconds < offsetMilliseconds) low = middle + 1;
            else high = middle - 1;
        }

        if (low == 0) return Frames[0];
        if (low >= Frames.Count) return Frames[^1];
        var before = Frames[low - 1];
        var after = Frames[low];
        return offsetMilliseconds - before.OffsetMilliseconds <= after.OffsetMilliseconds - offsetMilliseconds ? before : after;
    }

    public CapturedClip Trim(int startMilliseconds, int endMilliseconds)
    {
        startMilliseconds = Math.Clamp(startMilliseconds, 0, DurationMilliseconds);
        endMilliseconds = Math.Clamp(endMilliseconds, startMilliseconds + 1, DurationMilliseconds);
        var selected = Frames
            .Where(frame => frame.OffsetMilliseconds >= startMilliseconds && frame.OffsetMilliseconds <= endMilliseconds)
            .Select(frame => frame with { OffsetMilliseconds = frame.OffsetMilliseconds - startMilliseconds })
            .ToList();
        if (selected.Count == 0 && FindNearest(startMilliseconds) is { } nearest)
        {
            selected.Add(nearest with { OffsetMilliseconds = 0 });
        }

        return new(selected, endMilliseconds - startMilliseconds,
            StartedAtUtc?.AddMilliseconds(startMilliseconds), StartedAtUtc?.AddMilliseconds(endMilliseconds));
    }
}

public sealed class RollingJpegBuffer
{
    private readonly object _gate = new();
    private readonly Queue<JpegFrame> _frames = new();
    private readonly long _retentionTicks;
    private readonly long _maximumBytes;
    private long _bytes;

    public RollingJpegBuffer(TimeSpan retention, long maximumBytes)
    {
        _retentionTicks = (long)(Math.Max(1, retention.TotalSeconds) * Stopwatch.Frequency);
        _maximumBytes = Math.Max(16 * 1024 * 1024, maximumBytes);
    }

    public int Count { get { lock (_gate) return _frames.Count; } }
    public long Bytes { get { lock (_gate) return _bytes; } }
    public long? LatestTimestamp { get { lock (_gate) return _frames.Count == 0 ? null : _frames.Last().Timestamp; } }
    public DateTimeOffset? EarliestCapturedAtUtc { get { lock (_gate) return GetCapturedAtUtc(_frames.FirstOrDefault()); } }
    public DateTimeOffset? LatestCapturedAtUtc { get { lock (_gate) return GetCapturedAtUtc(_frames.LastOrDefault()); } }

    public void Add(JpegFrame frame)
    {
        lock (_gate)
        {
            _frames.Enqueue(frame);
            _bytes += frame.Data.LongLength;
            while (_frames.Count > 1 &&
                   (frame.Timestamp - _frames.Peek().Timestamp > _retentionTicks || _bytes > _maximumBytes))
            {
                _bytes -= _frames.Dequeue().Data.LongLength;
            }
        }
    }

    public CapturedClip Snapshot(long startTimestamp, long endTimestamp)
    {
        lock (_gate)
        {
            var selected = _frames.Where(frame => frame.Timestamp >= startTimestamp && frame.Timestamp <= endTimestamp).ToArray();
            if (selected.Length == 0)
            {
                return new([], 0);
            }

            var firstTimestamp = selected[0].Timestamp;
            var frames = selected.Select(frame => new ClipFrame(
                TicksToMilliseconds(frame.Timestamp - firstTimestamp), frame.Data, frame.Width, frame.Height)).ToArray();
            DateTimeOffset? startedAtUtc = selected[0].CapturedAtUtc == default ? null : selected[0].CapturedAtUtc;
            DateTimeOffset? endedAtUtc = selected[^1].CapturedAtUtc == default ? null : selected[^1].CapturedAtUtc;
            return new(frames, Math.Max(1, TicksToMilliseconds(selected[^1].Timestamp - firstTimestamp)), startedAtUtc, endedAtUtc);
        }
    }

    public CapturedClip Snapshot(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc < startUtc) (startUtc, endUtc) = (endUtc, startUtc);
        lock (_gate)
        {
            var selected = _frames.Where(frame => frame.CapturedAtUtc != default &&
                                                    frame.CapturedAtUtc >= startUtc && frame.CapturedAtUtc <= endUtc).ToArray();
            if (selected.Length == 0) return new([], 0);
            var firstTimestamp = selected[0].Timestamp;
            var firstOffset = (int)Math.Round((selected[0].CapturedAtUtc - startUtc).TotalMilliseconds);
            var duration = Math.Max(1, (int)Math.Round((endUtc - startUtc).TotalMilliseconds));
            var frames = selected.Select(frame => new ClipFrame(
                Math.Clamp(firstOffset + TicksToMilliseconds(frame.Timestamp - firstTimestamp), 0, duration),
                frame.Data, frame.Width, frame.Height)).ToArray();
            return new(frames, duration, startUtc, endUtc);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _bytes = 0;
        }
    }

    public static long SecondsToTicks(double seconds) => (long)(seconds * Stopwatch.Frequency);
    private static int TicksToMilliseconds(long ticks) => (int)Math.Round(ticks * 1000d / Stopwatch.Frequency);
    private static DateTimeOffset? GetCapturedAtUtc(JpegFrame? frame) =>
        frame is null || frame.CapturedAtUtc == default ? null : frame.CapturedAtUtc;
}
