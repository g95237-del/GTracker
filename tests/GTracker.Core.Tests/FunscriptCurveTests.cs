using GTracker.Core.Projects;

namespace GTracker.Core.Tests;

public sealed class FunscriptCurveTests
{
    [Fact]
    public void Evaluate_UsesNeutralValueForEmptyCurve()
    {
        Assert.Equal(50, FunscriptCurve.Evaluate([], 500));
    }

    [Fact]
    public void Evaluate_ClampsOutsideAuthoredRange()
    {
        FunscriptPoint[] points = [new(100, 20), new(900, 80)];

        Assert.Equal(20, FunscriptCurve.Evaluate(points, 0));
        Assert.Equal(80, FunscriptCurve.Evaluate(points, 1000));
    }

    [Fact]
    public void Evaluate_InterpolatesSmoothPositionAtPlayhead()
    {
        FunscriptPoint[] points = [new(0, 0), new(1000, 100)];

        Assert.Equal(33.3, FunscriptCurve.Evaluate(points, 333), 1);
    }
}
