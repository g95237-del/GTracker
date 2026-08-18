using System.Globalization;
using System.Text;

namespace GTracker.Core.Godot;

public sealed record GodotTelemetryEntry(
    DateTimeOffset Timestamp,
    string Kind,
    string Scene,
    string ObjectPath,
    string Candidate,
    string Details)
{
    public string DisplayText => $"{Timestamp:HH:mm:ss}  {Kind,-18}  {Candidate}  {ObjectPath}";
}

public sealed record GodotPlaybackTiming(TimeSpan CycleDuration, TimeSpan Phase, bool IsLooping)
{
    public DateTimeOffset GetCycleStart(DateTimeOffset timestamp) => timestamp - Phase;
}

public static class GodotTelemetryLog
{
    public static bool IsRuntimeCandidateEvent(string kind) =>
        kind is "ANIMATION_START" or "ANIMATION_LOOP" or "ANIMATION_UPDATE" or "ANIMATION_STOP";

    public static bool IsTimedPlaybackEvent(string kind) =>
        kind is "ANIMATION_START" or "ANIMATION_LOOP" or "ANIMATION_UPDATE";

    public static bool TryGetPlaybackTiming(GodotTelemetryEntry item, out GodotPlaybackTiming timing)
    {
        timing = default!;
        if (!IsTimedPlaybackEvent(item.Kind) || string.IsNullOrWhiteSpace(item.Details)) return false;
        var fields = item.Details.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(field => field.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.OrdinalIgnoreCase);
        if (!fields.TryGetValue("cycleDurationSeconds", out var durationText) ||
            !double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
            !double.IsFinite(duration) || duration <= 0) return false;
        var phase = fields.TryGetValue("phaseSeconds", out var phaseText) &&
                    double.TryParse(phaseText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedPhase) &&
                    double.IsFinite(parsedPhase) ? Math.Clamp(parsedPhase, 0, duration) : 0;
        var looping = fields.TryGetValue("loop", out var loopText) && bool.TryParse(loopText, out var parsedLoop) && parsedLoop;
        timing = new(TimeSpan.FromSeconds(duration), TimeSpan.FromSeconds(phase), looping);
        return true;
    }

    public static IReadOnlyList<GodotTelemetryEntry> Read(string path)
    {
        var cursor = new GodotTelemetryCursor();
        return cursor.ReadNew(path).Entries;
    }

    internal static GodotTelemetryEntry? Parse(string line)
    {
        var columns = line.TrimEnd('\r').Split('\t');
        if (columns.Length != 6 || !DateTimeOffset.TryParse(columns[0], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)) return null;
        return new(timestamp, columns[1], columns[2], columns[3], columns[4], columns[5]);
    }
}

public sealed record GodotTelemetryReadResult(IReadOnlyList<GodotTelemetryEntry> Entries, bool WasReset);

public sealed class GodotTelemetryCursor
{
    private long _offset;
    private string _partial = string.Empty;
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    public GodotTelemetryReadResult ReadNew(string path)
    {
        if (!File.Exists(path)) return new([], false);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var reset = stream.Length < _offset;
        if (reset)
        {
            _offset = 0;
            _partial = string.Empty;
            _decoder.Reset();
        }
        var snapshotLength = stream.Length;
        var remaining = snapshotLength - _offset;
        if (remaining <= 0) return new([], reset);
        if (remaining > int.MaxValue) throw new IOException("Godot telemetry increment is too large to read safely.");
        stream.Position = _offset;
        var bytes = new byte[(int)remaining];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = stream.Read(bytes, read, bytes.Length - read);
            if (count == 0) break;
            read += count;
        }
        _offset += read;
        var characters = new char[Encoding.UTF8.GetMaxCharCount(read)];
        var characterCount = _decoder.GetChars(bytes, 0, read, characters, 0, flush: false);
        var text = _partial + new string(characters, 0, characterCount);
        var lines = text.Split('\n');
        _partial = text.EndsWith('\n') ? string.Empty : lines[^1];
        var completeCount = lines.Length - (_partial.Length > 0 ? 1 : 0);
        var entries = new List<GodotTelemetryEntry>();
        for (var index = 0; index < completeCount; index++)
        {
            var entry = GodotTelemetryLog.Parse(lines[index]);
            if (entry is not null) entries.Add(entry);
        }
        return new(entries, reset);
    }

    public void Reset()
    {
        _offset = 0;
        _partial = string.Empty;
        _decoder.Reset();
    }
}
