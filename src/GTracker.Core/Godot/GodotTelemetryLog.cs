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
    public string SuggestedName => GodotTelemetryLog.GetSuggestedName(this);
    public string DisplayText => $"{Timestamp:HH:mm:ss}  {Kind,-18}  {SuggestedName}  {ObjectPath}";
}

public sealed record GodotPlaybackTiming(TimeSpan CycleDuration, TimeSpan Phase, bool IsLooping, double Speed)
{
    public DateTimeOffset GetCycleStart(DateTimeOffset timestamp) => timestamp - Phase;
}

public static class GodotTelemetryLog
{
    private static readonly HashSet<string> ContextDependentOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "AnimationPlayer", "AnimatedSprite", "Control", "Node", "Node2D", "Player", "Position2D",
        "PressDown", "PressUp", "Spatial", "Sprite"
    };

    public static string GetSuggestedName(GodotTelemetryEntry entry)
    {
        var candidate = entry.Candidate.Trim();
        if (entry.Kind == "SCENE") return SceneStem(candidate);
        var segments = entry.ObjectPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count > 0 && segments[0].Equals("root", StringComparison.OrdinalIgnoreCase)) segments.RemoveAt(0);
        if (segments.Count > 0 && Normalize(segments[0]) == Normalize(SceneStem(entry.Scene))) segments.RemoveAt(0);
        if (segments.Count > 0 && segments[^1].Equals("AnimationPlayer", StringComparison.OrdinalIgnoreCase)) segments.RemoveAt(segments.Count - 1);
        if (segments.Count == 0) return DisplayWords(candidate);
        var owner = segments[^1];
        var ownerLabel = DisplayWords(owner);
        if (segments.Count > 1 && (ContextDependentOwners.Contains(owner) || owner.All(char.IsDigit)))
            ownerLabel = $"{DisplayWords(segments[^2])} / {ownerLabel}";
        var state = DisplayWords(candidate);
        if (TryDetailNumber(entry.Details, "speed", out var speed) && Math.Abs(speed - 1) > 0.01)
            state += $" ({speed.ToString("0.##", CultureInfo.InvariantCulture)}x)";
        return string.IsNullOrWhiteSpace(state) ? ownerLabel : $"{ownerLabel} - {state}";
    }

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
        var speed = TryDetailNumber(item.Details, "speed", out var parsedSpeed) ? parsedSpeed : 1;
        timing = new(TimeSpan.FromSeconds(duration), TimeSpan.FromSeconds(phase), looping, speed);
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

    private static string SceneStem(string value)
    {
        var separator = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\'));
        if (separator >= 0) value = value[(separator + 1)..];
        var extension = value.LastIndexOf('.');
        return extension > 0 ? value[..extension] : value;
    }

    private static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool TryDetailNumber(string details, string key, out double value)
    {
        value = 0;
        foreach (var field in details.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = field.Split('=', 2);
            if (pair.Length == 2 && pair[0].Equals(key, StringComparison.OrdinalIgnoreCase))
                return double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
        }
        return false;
    }

    private static string DisplayWords(string value)
    {
        var output = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '_' or '-')
            {
                if (output.Length > 0 && output[^1] != ' ') output.Append(' ');
                continue;
            }
            if (index > 0 && char.IsUpper(character) && char.IsLower(value[index - 1]) && output.Length > 0 && output[^1] != ' ')
                output.Append(' ');
            output.Append(character);
        }
        return output.ToString().Trim();
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
