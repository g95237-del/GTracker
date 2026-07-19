using System.Globalization;

namespace GTracker.Core.Unity;

public sealed record UnityTelemetryEvent(
    DateTimeOffset Timestamp,
    string Kind,
    string Scene,
    string ObjectPath,
    string Candidate,
    string Details);

public sealed record UnityRuntimeCorrelation(
    string SceneName,
    string AnimationName,
    IReadOnlyList<string> AnimationCandidates)
{
    public string PreferredName => !string.IsNullOrWhiteSpace(AnimationName) ? AnimationName : SceneName;
    public bool HasAmbiguousAnimations => AnimationCandidates.Count > 1;
}

public sealed record UnityAnimatorTiming(
    TimeSpan CycleDuration,
    TimeSpan Phase,
    int LoopIndex,
    bool IsLooping,
    double NormalizedTime)
{
    public DateTimeOffset GetCycleStart(DateTimeOffset eventTimestamp) => eventTimestamp - Phase;
}

public static class UnityTelemetryLog
{
    public static bool IsAnimatorEvent(string kind) =>
        kind is "ANIMATOR" or "ANIMATOR_LOOP" or "ANIMATOR_RESTART" or "ANIMATOR_RESUME" or
            "ANIMATOR_STALLED" or "ANIMATOR_END" or "ANIMATOR_VARIANT" or "FSM_STATE";

    public static IReadOnlyList<UnityTelemetryEvent> Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return [];
        var events = new List<UnityTelemetryEvent>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var snapshotLength = stream.Length;
        var endsWithNewline = snapshotLength == 0;
        if (snapshotLength > 0)
        {
            stream.Position = snapshotLength - 1;
            endsWithNewline = stream.ReadByte() is '\r' or '\n';
            stream.Position = 0;
        }
        using var reader = new StreamReader(new SnapshotReadStream(stream, snapshotLength));
        while (reader.ReadLine() is { } line)
        {
            if (reader.EndOfStream && !endsWithNewline) break;
            var fields = line.Split('\t');
            if (fields.Length < 5 || !DateTimeOffset.TryParse(fields[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var timestamp)) continue;
            events.Add(new(timestamp, fields[1], fields[2], fields[3], fields[4], fields.Length > 5 ? fields[5] : string.Empty));
        }
        return events;
    }

    public static UnityRuntimeCorrelation Correlate(
        IEnumerable<UnityTelemetryEvent> events,
        DateTimeOffset clipStartUtc,
        DateTimeOffset clipEndUtc)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (clipEndUtc < clipStartUtc) (clipStartUtc, clipEndUtc) = (clipEndUtc, clipStartUtc);
        var ordered = events.OrderBy(item => item.Timestamp).ToArray();
        var sceneName = ordered
            .Where(item => item.Timestamp <= clipEndUtc && item.Kind is "SCENE" or "ACTIVE_SCENE" &&
                           !string.IsNullOrWhiteSpace(item.Candidate))
            .Select(item => item.Candidate)
            .LastOrDefault() ?? string.Empty;
        var sceneAtStart = ordered
            .Where(item => item.Timestamp <= clipStartUtc && item.Kind is "SCENE" or "ACTIVE_SCENE" &&
                           !string.IsNullOrWhiteSpace(item.Candidate))
            .LastOrDefault();
        var activeFsmStates = sceneAtStart is null
            ? []
            : ordered
                .Where(item => item.Timestamp >= sceneAtStart.Timestamp && item.Timestamp <= clipStartUtc &&
                               item.Kind is "FSM_STATE" or "FSM_EXIT")
                .GroupBy(FsmStreamKey, StringComparer.Ordinal)
                .Select(group => group.Last())
                .Where(item => item.Kind == "FSM_STATE" && !string.IsNullOrWhiteSpace(item.Candidate))
                .Select(item => item.Candidate)
                .ToArray();
        var animations = activeFsmStates.Concat(ordered
            .Where(item => item.Timestamp >= clipStartUtc && item.Timestamp <= clipEndUtc && IsAnimatorEvent(item.Kind) &&
                           !string.IsNullOrWhiteSpace(item.Candidate))
            .Select(item => item.Candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new(sceneName, animations.Length == 1 ? animations[0] : string.Empty, animations);
    }

    public static bool TryGetAnimatorTiming(UnityTelemetryEvent item, out UnityAnimatorTiming timing)
    {
        timing = default!;
        if (!IsAnimatorEvent(item.Kind) || string.IsNullOrWhiteSpace(item.Details)) return false;
        var fields = item.Details.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(field => field.Split('=', 2))
            .Where(field => field.Length == 2)
            .ToDictionary(field => field[0], field => field[1], StringComparer.OrdinalIgnoreCase);
        if (!TryNumber(fields, "cycleDurationSeconds", out var durationSeconds) ||
            durationSeconds <= 0 || !double.IsFinite(durationSeconds)) return false;
        TryNumber(fields, "phaseSeconds", out var phaseSeconds);
        TryNumber(fields, "normalizedTime", out var normalizedTime);
        _ = fields.TryGetValue("loopIndex", out var loopText);
        _ = int.TryParse(loopText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var loopIndex);
        _ = fields.TryGetValue("loop", out var loopingText);
        _ = bool.TryParse(loopingText, out var isLooping);
        phaseSeconds = double.IsFinite(phaseSeconds) ? Math.Clamp(phaseSeconds, 0, durationSeconds) : 0;
        timing = new(TimeSpan.FromSeconds(durationSeconds), TimeSpan.FromSeconds(phaseSeconds),
            loopIndex, isLooping, normalizedTime);
        return true;
    }

    private static bool TryNumber(IReadOnlyDictionary<string, string> fields, string key, out double value)
    {
        value = 0;
        return fields.TryGetValue(key, out var text) &&
               double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static string FsmStreamKey(UnityTelemetryEvent item)
    {
        var stream = Detail(item.Details, "stream");
        if (!string.IsNullOrWhiteSpace(stream)) return stream;
        if (string.IsNullOrWhiteSpace(stream)) stream = Detail(item.Details, "fsm");
        return string.Join("\0", item.Scene, item.ObjectPath, stream);
    }

    private static string Detail(string details, string key)
    {
        foreach (var field in details.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = field.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase)) return pair[1];
        }
        return string.Empty;
    }

    private sealed class SnapshotReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _remaining;

        public SnapshotReadStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var read = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            if (_remaining <= 0) return 0;
            var read = _inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
            _remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
