using System.Globalization;

namespace GTracker.Core.Projects;

public sealed record EdiSpeedVariant(
    double Multiplier,
    int DurationMilliseconds,
    string ActionName,
    string FileName,
    bool IsBase);

public static class EdiSpeedVariants
{
    public static IReadOnlyList<double> RecommendedMultipliers { get; } = [0.75, 1.0, 1.5, 2.0, 2.5, 3.0];

    public static IReadOnlyList<double> NormalizeMultipliers(IEnumerable<double>? values)
    {
        var normalized = (values ?? [])
            .Where(value => double.IsFinite(value) && value > 0)
            .Append(1.0)
            .DistinctBy(value => Math.Round(value, 6))
            .OrderBy(value => value)
            .ToArray();
        return normalized;
    }

    public static IReadOnlyList<EdiSpeedVariant> Create(AuthoredAction action, IEnumerable<double>? multipliers)
    {
        ArgumentNullException.ThrowIfNull(action);
        var speeds = action.Type == EdiGalleryType.Filler ? [1.0] : NormalizeMultipliers(multipliers);
        return speeds
            .Select(speed =>
            {
                var isBase = Math.Abs(speed - 1.0) < 0.000001;
                var label = FormatMultiplier(speed);
                return new EdiSpeedVariant(
                    speed,
                    ScaleDuration(action.DurationMilliseconds, speed),
                    isBase ? action.Name : $"{action.Name} [{label}x]",
                    isBase ? action.FileName : $"{action.FileName}--speed-{label.Replace('.', '_')}x",
                    isBase);
            })
            .GroupBy(variant => variant.DurationMilliseconds)
            .Select(group => group.OrderBy(variant => Math.Abs(variant.Multiplier - 1.0)).First())
            .OrderByDescending(variant => variant.DurationMilliseconds)
            .ToArray();
    }

    public static IReadOnlyList<FunscriptPoint> ScalePoints(
        IEnumerable<FunscriptPoint> points,
        int sourceDurationMilliseconds,
        int targetDurationMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (sourceDurationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(sourceDurationMilliseconds));
        if (targetDurationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(targetDurationMilliseconds));
        return points
            .Select(point => new FunscriptPoint(
                Math.Clamp((int)Math.Round((long)point.At * targetDurationMilliseconds / (double)sourceDurationMilliseconds,
                    MidpointRounding.AwayFromZero), 0, targetDurationMilliseconds),
                point.Pos))
            .GroupBy(point => point.At)
            .Select(group => group.Last())
            .OrderBy(point => point.At)
            .ToArray();
    }

    public static int ScaleDuration(int sourceDurationMilliseconds, double multiplier)
    {
        if (sourceDurationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(sourceDurationMilliseconds));
        if (!double.IsFinite(multiplier) || multiplier <= 0) throw new ArgumentOutOfRangeException(nameof(multiplier));
        var duration = sourceDurationMilliseconds / multiplier;
        if (!double.IsFinite(duration) || duration > int.MaxValue)
            throw new InvalidDataException($"Speed multiplier {FormatMultiplier(multiplier)}x produces a duration larger than EDI supports.");
        return Math.Max(1, (int)Math.Round(duration, MidpointRounding.AwayFromZero));
    }

    public static string FormatMultiplier(double multiplier) =>
        multiplier.ToString("0.######", CultureInfo.InvariantCulture);
}
