namespace GTracker.Core.Projects;

public static class FunscriptCurve
{
    public static double Evaluate(IReadOnlyList<FunscriptPoint> points, int milliseconds, double emptyValue = 50)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0) return emptyValue;
        if (milliseconds <= points[0].At) return points[0].Pos;
        if (milliseconds >= points[^1].At) return points[^1].Pos;

        for (var index = 1; index < points.Count; index++)
        {
            if (points[index].At < milliseconds) continue;
            var left = points[index - 1];
            var right = points[index];
            var amount = (milliseconds - left.At) / (double)Math.Max(1, right.At - left.At);
            return left.Pos + (right.Pos - left.Pos) * amount;
        }

        return points[^1].Pos;
    }
}
