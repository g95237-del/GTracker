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
    public void Correlate_UsesPlayMakerStateInsideClip()
    {
        var start = DateTimeOffset.UtcNow;
        UnityTelemetryEvent[] events =
        [
            new(start, "SCENE", "Stage_Main_00", "", "Stage_Main_00", ""),
            new(start.AddSeconds(1), "FSM_STATE", "Stage_Main_00", "Game/FSM", "Battle / PlayerClimax", "")
        ];

        var result = UnityTelemetryLog.Correlate(events, start, start.AddSeconds(3));

        Assert.Equal("Battle / PlayerClimax", result.AnimationName);
        Assert.True(UnityTelemetryLog.IsAnimatorEvent("FSM_STATE"));
    }

    [Fact]
    public void Correlate_CarriesActivePlayMakerStateIntoClip()
    {
        var start = DateTimeOffset.UtcNow;
        UnityTelemetryEvent[] events =
        [
            new(start.AddSeconds(-10), "SCENE", "Stage_Main_00", "", "Stage_Main_00", ""),
            new(start.AddSeconds(-2), "FSM_STATE", "DontDestroyOnLoad", "Game/FSM", "Battle / PlayerClimax",
                "stream=42;fsm=Battle;state=PlayerClimax"),
            new(start.AddSeconds(2), "FSM_EXIT", "DontDestroyOnLoad", "Game/FSM", "Battle / PlayerClimax",
                "stream=42;reason=state-empty")
        ];

        var result = UnityTelemetryLog.Correlate(events, start, start.AddSeconds(3));

        Assert.Equal("Battle / PlayerClimax", result.AnimationName);
    }

    [Fact]
    public void Correlate_DoesNotCarryExitedPlayMakerStateIntoClip()
    {
        var start = DateTimeOffset.UtcNow;
        UnityTelemetryEvent[] events =
        [
            new(start.AddSeconds(-10), "SCENE", "Stage_Main_00", "", "Stage_Main_00", ""),
            new(start.AddSeconds(-3), "FSM_STATE", "Stage_Main_00", "Game/FSM", "Battle / PlayerClimax",
                "stream=42;fsm=Battle;state=PlayerClimax"),
            new(start.AddSeconds(-1), "FSM_EXIT", "Stage_Main_00", "Game/FSM", "Battle / PlayerClimax",
                "stream=42;reason=state-empty")
        ];

        var result = UnityTelemetryLog.Correlate(events, start, start.AddSeconds(3));

        Assert.Empty(result.AnimationName);
        Assert.Empty(result.AnimationCandidates);
    }

    [Fact]
    public void Correlate_DoesNotTreatAdditiveLoadAsActiveScene()
    {
        var start = DateTimeOffset.UtcNow;
        UnityTelemetryEvent[] events =
        [
            new(start, "SCENE", "Stage_Main_00", "", "Stage_Main_00", "active"),
            new(start.AddSeconds(1), "LOADED_SCENE", "Stage_Main_00", "", "EffectsOverlay", "Additive")
        ];

        var result = UnityTelemetryLog.Correlate(events, start, start.AddSeconds(2));

        Assert.Equal("Stage_Main_00", result.SceneName);
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
    public void Read_IgnoresAnUnterminatedTelemetryTail()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "telemetry.tsv");
            File.WriteAllText(path,
                "2026-07-15T20:00:00.0000000+00:00\tSCENE\tRoom\t\tRoom\tactive\n" +
                "2026-07-15T20:00:01.0000000+00:00\tANIMATOR\tRoom\tPlayer\tAttack");

            var item = Assert.Single(UnityTelemetryLog.Read(path));

            Assert.Equal("SCENE", item.Kind);
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
