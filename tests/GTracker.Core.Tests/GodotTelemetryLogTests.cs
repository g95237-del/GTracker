using GTracker.Core.Godot;

namespace GTracker.Core.Tests;

public sealed class GodotTelemetryLogTests
{
    [Theory]
    [InlineData("res://scenes/Stages/Stage1_1.tscn", "/root/Sperm_L/AnimationPlayer", "start", "Sperm L - start")]
    [InlineData("res://scenes/Stages/Stage1_1.tscn", "/root/stage1_1/TransCam/AnimationPlayer", "nervous", "Trans Cam - nervous")]
    [InlineData("res://scenes/Stages/Stage1_1.tscn", "/root/stage1_1/Interactions/painting_left/PressUp/AnimationPlayer", "idle", "painting left / Press Up - idle")]
    public void SuggestedName_UsesAnimationPlayerHierarchy(string scene, string path, string candidate, string expected)
    {
        var entry = new GodotTelemetryEntry(DateTimeOffset.UtcNow, "ANIMATION_START", scene, path, candidate, "");

        Assert.Equal(expected, entry.SuggestedName);
    }

    [Fact]
    public void SuggestedName_UsesSceneFileStem()
    {
        var entry = new GodotTelemetryEntry(DateTimeOffset.UtcNow, "SCENE", "res://scenes/Stages/Stage1_1.tscn", "",
            "res://scenes/Stages/Stage1_1.tscn", "");

        Assert.Equal("Stage1_1", entry.SuggestedName);
    }

    [Fact]
    public void SuggestedName_IncludesNonDefaultPlaybackSpeed()
    {
        var entry = new GodotTelemetryEntry(DateTimeOffset.UtcNow, "ANIMATION_UPDATE", "res://Gallery.tscn",
            "/root/Gallery/Units/Maid/AnimationPlayer", "p1",
            "phaseSeconds=0.1;cycleDurationSeconds=0.525;speed=2;loop=false");

        Assert.Equal("Maid - p1 (2x)", entry.SuggestedName);
    }

    [Fact]
    public void PlaybackTiming_ParsesAnimationUpdate()
    {
        var entry = new GodotTelemetryEntry(DateTimeOffset.UtcNow, "ANIMATION_UPDATE", "scene", "/root/player",
            "idle", "phaseSeconds=0.25;cycleDurationSeconds=1.5;speed=1;loop=true");

        Assert.True(GodotTelemetryLog.TryGetPlaybackTiming(entry, out var timing));
        Assert.Equal(TimeSpan.FromSeconds(1.5), timing.CycleDuration);
        Assert.Equal(TimeSpan.FromSeconds(0.25), timing.Phase);
        Assert.True(timing.IsLooping);
        Assert.Equal(1, timing.Speed);
    }

    [Fact]
    public void PlaybackTiming_AcceptsObservedRestart()
    {
        var entry = new GodotTelemetryEntry(DateTimeOffset.UtcNow, "ANIMATION_RESTART", "scene", "/root/player",
            "p1", "phaseSeconds=0.02;cycleDurationSeconds=1.05;speed=1;loop=true");

        Assert.True(GodotTelemetryLog.TryGetPlaybackTiming(entry, out var timing));
        Assert.True(timing.IsLooping);
    }

    [Fact]
    public void PlaybackStreamKey_MatchesStartWithObservedRestart()
    {
        var started = new GodotTelemetryEntry(DateTimeOffset.UtcNow, "ANIMATION_START", "res://Gallery.tscn",
            "/root/Gallery/Units/Boss1/AnimationPlayer", "p1", "loop=false");
        var restarted = started with { Kind = "ANIMATION_RESTART", Details = "loop=true" };

        Assert.True(GodotTelemetryLog.IsObservedLoopEvent(restarted.Kind));
        Assert.False(GodotTelemetryLog.IsObservedLoopEvent(started.Kind));
        Assert.Equal(GodotTelemetryLog.GetPlaybackStreamKey(started), GodotTelemetryLog.GetPlaybackStreamKey(restarted));
    }

    [Fact]
    public async Task Read_ParsesCompleteRecordsAndIgnoresMalformedTail()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "telemetry.tsv");
            await File.WriteAllTextAsync(path,
                "2026-08-18T16:08:42Z\tSCENE\tres://Intro.tscn\t\tres://Intro.tscn\tfrom=\n" +
                "malformed\n" +
                "2026-08-18T16:08:43Z\tANIMATION_START\tres://Intro.tscn\t/root/AnimationPlayer\tintro\tphaseSeconds=0.1");

            var entries = GodotTelemetryLog.Read(path);

            var entry = Assert.Single(entries);
            Assert.Equal("SCENE", entry.Kind);
            Assert.Equal("res://Intro.tscn", entry.Candidate);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Read_AllowsConcurrentWriter()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "telemetry.tsv");
            using var writer = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            using (var text = new StreamWriter(writer, leaveOpen: true))
            {
                text.WriteLine("2026-08-18T16:08:42Z\tSESSION\t\t\tobserver-started\tengine=godot3");
                text.Flush();
            }

            Assert.Single(GodotTelemetryLog.Read(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Cursor_ReadsOnlyNewCompleteRecordsAndHandlesTruncation()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "telemetry.tsv");
            var cursor = new GodotTelemetryCursor();
            await File.WriteAllTextAsync(path, "2026-08-18T16:08:42Z\tSESSION\t\t\tobserver-started\tengine=godot3\npartial");

            Assert.Single(cursor.ReadNew(path).Entries);
            Assert.Empty(cursor.ReadNew(path).Entries);
            await File.AppendAllTextAsync(path, " record\n2026-08-18T16:08:43Z\tSCENE\tintro\t\tintro\tfrom=\n");
            Assert.Single(cursor.ReadNew(path).Entries);

            await File.WriteAllTextAsync(path, "2026-08-18T16:08:44Z\tSESSION\t\t\trestarted\t\n");
            var reset = cursor.ReadNew(path);
            Assert.True(reset.WasReset);
            Assert.Single(reset.Entries);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Cursor_PreservesUtf8CharacterSplitAcrossReads()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "telemetry.tsv");
            var prefix = System.Text.Encoding.UTF8.GetBytes("2026-08-18T16:08:42Z\tSCENE\t場");
            File.WriteAllBytes(path, prefix[..^1]);
            var cursor = new GodotTelemetryCursor();
            Assert.Empty(cursor.ReadNew(path).Entries);
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
            {
                stream.Write(prefix[^1..]);
                stream.Write(System.Text.Encoding.UTF8.GetBytes("面\t\t場面\t\n"));
            }

            var entry = Assert.Single(cursor.ReadNew(path).Entries);
            Assert.Equal("場面", entry.Scene);
            Assert.Equal("場面", entry.Candidate);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
