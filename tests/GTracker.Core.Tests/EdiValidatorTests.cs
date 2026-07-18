using GTracker.Core.Edi;
using GTracker.Core.Projects;

namespace GTracker.Core.Tests;

public sealed class EdiValidatorTests
{
    [Fact]
    public void Validate_RejectsDuplicateNamesAndUnsafePaths()
    {
        var project = new StudioProject
        {
            Actions =
            [
                CreateAction("Scene", "scene"),
                CreateAction("scene", "../escape")
            ]
        };

        var issues = new EdiValidator().Validate(project);

        Assert.Contains(issues, issue => issue.Severity == ValidationSeverity.Error && issue.Message.Contains("unique"));
        Assert.Contains(issues, issue => issue.Severity == ValidationSeverity.Error && issue.Message.Contains("FileName"));
    }

    [Fact]
    public void Validate_WarnsWhenExporterMustRepairBoundaries()
    {
        var action = CreateAction("loop", "loop");
        action.Tracks[0].Points = [new(100, 20), new(900, 80)];
        var project = new StudioProject { Actions = [action] };

        var issues = new EdiValidator().Validate(project);

        Assert.DoesNotContain(issues, issue => issue.Severity == ValidationSeverity.Error);
        Assert.Equal(3, issues.Count(issue => issue.Severity == ValidationSeverity.Warning));
    }

    [Fact]
    public void Validate_RequiresDefaultAxis()
    {
        var action = CreateAction("twist-only", "twist-only");
        action.Tracks[0].Axis = EdiAxis.Twist;

        var issues = new EdiValidator().Validate(new StudioProject { Actions = [action] });

        Assert.Contains(issues, issue => issue.Severity == ValidationSeverity.Error && issue.Message.Contains("Default axis"));
    }

    [Fact]
    public void Validate_WarnsAboutAbruptCleanLoopClosure()
    {
        var scene = CreateAction("short-loop", "short-loop");
        scene.Tracks[0].Points = [new(0, 60), new(975, 0)];

        var issues = new EdiValidator().Validate(new StudioProject { Actions = [scene] });

        Assert.Contains(issues, issue => issue.Severity == ValidationSeverity.Warning &&
                                         issue.Message.Contains("only 25 ms"));
    }

    private static AuthoredAction CreateAction(string name, string fileName) => new()
    {
        Name = name,
        FileName = fileName,
        DurationMilliseconds = 1000,
        Tracks = [new ActionTrack { Points = [new(0, 50), new(1000, 50)] }]
    };
}
