using GTracker.Core.Unity;

namespace GTracker.Core.Tests;

public sealed class UnityTelemetryLogTests
{
    [Fact]
    public void Correlate_UsesActiveSceneAndSingleAnimationInsideClip()
    {
        var start = new DateTimeOffset(2026, 7, 15, 20, 0, 0, TimeSpan.Zero);
        UnityTelemetryEvent[] events =
        [
            new(start.AddSeconds(-20), "SCENE", "Town", "", "Town", "startup"),
            new(start.AddSeconds(-1), "ACTIVE_SCENE", "GoblinHouse", "", "GoblinHouse", "from=Town"),
            new(start.AddSeconds(2), "ANIMATOR", "GoblinHouse", "Goblin/Mail", "MailFail", "layer=0")
        ];

        var result = UnityTelemetryLog.Correlate(events, start, start.AddSeconds(6));

        Assert.Equal("GoblinHouse", result.SceneName);
        Assert.Equal("MailFail", result.AnimationName);
        Assert.Equal("MailFail", result.PreferredName);
        Assert.False(result.HasAmbiguousAnimations);
    }

    [Fact]
    public void Correlate_DoesNotGuessBetweenMultipleAnimations()
    {
        var start = DateTimeOffset.UtcNow;
        UnityTelemetryEvent[] events =
        [
            new(start, "SCENE", "Room", "", "Room", ""),
            new(start.AddSeconds(1), "ANIMATOR", "Room", "Player", "Idle", ""),
            new(start.AddSeconds(2), "ANIMATOR", "Room", "Goblin", "Attack", "")
        ];

        var result = UnityTelemetryLog.Correlate(events, start, start.AddSeconds(3));

        Assert.Equal("Room", result.PreferredName);
        Assert.Empty(result.AnimationName);
        Assert.True(result.HasAmbiguousAnimations);
    }

    [Fact]
    public void Read_ParsesStructuredTelemetryAndSkipsMalformedLines()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "telemetry.tsv");
            File.WriteAllText(path,
                "malformed\n2026-07-15T20:00:00.0000000+00:00\tANIMATOR\tRoom\tPlayer\tAttack\tlayer=0\n");

            var item = Assert.Single(UnityTelemetryLog.Read(path));

            Assert.Equal("Attack", item.Candidate);
            Assert.Equal("Player", item.ObjectPath);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void TryGetAnimatorTiming_ParsesCycleAndDerivesCycleStart()
    {
        var timestamp = new DateTimeOffset(2026, 7, 15, 20, 0, 3, TimeSpan.Zero);
        var item = new UnityTelemetryEvent(timestamp, "ANIMATOR_LOOP", "Room", "Player", "Attack",
            "normalizedTime=2.25;loopIndex=2;loop=true;cycleDurationSeconds=4;phaseSeconds=1");

        var parsed = UnityTelemetryLog.TryGetAnimatorTiming(item, out var timing);

        Assert.True(parsed);
        Assert.Equal(TimeSpan.FromSeconds(4), timing.CycleDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), timing.Phase);
        Assert.Equal(2, timing.LoopIndex);
        Assert.True(timing.IsLooping);
        Assert.Equal(timestamp.AddSeconds(-1), timing.GetCycleStart(timestamp));
    }
}
