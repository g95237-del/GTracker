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
            "ANIMATOR_STALLED" or "ANIMATOR_END";

    public static IReadOnlyList<UnityTelemetryEvent> Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return [];
        var events = new List<UnityTelemetryEvent>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
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
        var animations = ordered
            .Where(item => item.Timestamp >= clipStartUtc && item.Timestamp <= clipEndUtc && item.Kind == "ANIMATOR" &&
                           !string.IsNullOrWhiteSpace(item.Candidate))
            .Select(item => item.Candidate)
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
}
