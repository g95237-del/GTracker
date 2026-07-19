using GTracker.Core.Projects;

namespace GTracker.Core.Tests;

public sealed class ProjectStoreTests
{
    [Fact]
    public void SetTriggerMapping_ReassignsExistingCandidateWithoutDuplicatingIt()
    {
        var target = new GameTarget();
        target.SetTriggerMapping(UnityTriggerKind.AnimationClip, "GameOver_Male", "old-scene");

        target.SetTriggerMapping(UnityTriggerKind.AnimationClip, "gameover_male", "new-scene");

        var mapping = Assert.Single(target.TriggerMappings);
        Assert.Equal("new-scene", mapping.ActionName);
        Assert.Equal("gameover_male", mapping.Candidate);
    }

    [Fact]
    public void SetTriggerMapping_AllowsTimingVariantsForSameCandidate()
    {
        var target = new GameTarget();

        target.SetTriggerMapping(UnityTriggerKind.AnimationClip, "Loop", "normal", "Root/Animator", 817);
        target.SetTriggerMapping(UnityTriggerKind.AnimationClip, "Loop", "fast", "Root/Animator", 204);

        Assert.Equal(2, target.TriggerMappings.Count);
        Assert.Contains(target.TriggerMappings, mapping => mapping.ActionName == "normal" && mapping.CycleDurationMilliseconds == 817);
        Assert.Contains(target.TriggerMappings, mapping => mapping.ActionName == "fast" && mapping.CycleDurationMilliseconds == 204);
    }

    [Fact]
    public async Task SaveAndLoad_RoundTripsVersionedProject()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var project = new StudioProject
            {
                Name = "Round Trip",
                Game = new GameTarget
                {
                    Runtime = UnityRuntimeKind.Il2Cpp,
                    Architecture = "Amd64",
                    UnityVersion = "2022.3.10f1",
                    TargetFramework = "net6.0",
                    BepInExFlavor = "Il2Cpp6",
                    ModPreset = UnityModPresetKind.AnimationNames,
                    ModProjectPath = @"C:\mods\sample",
                    TelemetryPath = @"C:\game\BepInEx\config\sample.tsv",
                    TriggerMappings =
                    [
                        new UnityTriggerMapping
                        {
                            Kind = UnityTriggerKind.AnimationClip,
                            Candidate = "EnemyAttack",
                            ActionName = "intro",
                            ObjectPath = "Enemies/Goblin/Animator",
                            CycleDurationMilliseconds = 850
                        }
                    ],
                    Simulator = new LinearSimulatorLayout
                    {
                        IsVisible = false,
                        CenterX = 0.72,
                        CenterY = 0.28,
                        Width = 0.35,
                        Height = 0.12,
                        RotationDegrees = 37
                    }
                },
                Actions =
                [
                    new AuthoredAction
                    {
                        Name = "intro",
                        FileName = "intro",
                        IsLocked = true,
                        SourceStartedAtUtc = new DateTimeOffset(2026, 7, 15, 20, 0, 0, TimeSpan.Zero),
                        SourceEndedAtUtc = new DateTimeOffset(2026, 7, 15, 20, 0, 6, TimeSpan.Zero),
                        UnitySceneName = "GoblinHouse",
                        UnityAnimationName = "MailFail"
                    }
                ]
            };
            var store = new ProjectStore();

            await store.SaveAsync(directory, project);
            var loaded = await store.LoadAsync(directory);

            Assert.Equal(StudioProject.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal(project.Id, loaded.Id);
            Assert.Equal("Round Trip", loaded.Name);
            Assert.Equal(UnityRuntimeKind.Il2Cpp, loaded.Game.Runtime);
            Assert.Equal("net6.0", loaded.Game.TargetFramework);
            Assert.Equal(UnityModPresetKind.AnimationNames, loaded.Game.ModPreset);
            Assert.Equal(@"C:\mods\sample", loaded.Game.ModProjectPath);
            var mapping = Assert.Single(loaded.Game.TriggerMappings);
            Assert.Equal("EnemyAttack", mapping.Candidate);
            Assert.Equal("Enemies/Goblin/Animator", mapping.ObjectPath);
            Assert.Equal(850, mapping.CycleDurationMilliseconds);
            Assert.False(loaded.Game.Simulator.IsVisible);
            Assert.Equal(0.72, loaded.Game.Simulator.CenterX);
            Assert.Equal(37, loaded.Game.Simulator.RotationDegrees);
            Assert.Single(loaded.Actions);
            Assert.Equal("GoblinHouse", loaded.Actions[0].UnitySceneName);
            Assert.Equal("MailFail", loaded.Actions[0].UnityAnimationName);
            Assert.True(loaded.Actions[0].IsLocked);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));

            loaded.Name = "Round Trip Updated";
            await store.SaveAsync(directory, loaded);
            var historyFile = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(directory, ProjectStore.HistoryDirectoryName), "project.edi.*.json"));
            Assert.Contains("\"name\": \"Round Trip\"", await File.ReadAllTextAsync(historyFile));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Load_RejectsUnsupportedSchema()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, ProjectStore.ProjectFileName), "{\"schemaVersion\":999}");
            await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectStore().LoadAsync(directory));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Load_AddsDefaultSimulatorToExistingSchemaOneProject()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, ProjectStore.ProjectFileName),
                "{\"schemaVersion\":1,\"game\":{}}");

            var loaded = await new ProjectStore().LoadAsync(directory);

            Assert.True(loaded.Game.Simulator.IsVisible);
            Assert.Equal(0.5, loaded.Game.Simulator.CenterX);
            Assert.Equal(0.42, loaded.Game.Simulator.Width);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
