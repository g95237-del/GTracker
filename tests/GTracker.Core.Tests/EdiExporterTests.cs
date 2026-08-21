using System.Text.Json;
using GTracker.Core.Edi;
using GTracker.Core.Projects;

namespace GTracker.Core.Tests;

public sealed class EdiExporterTests
{
    [Fact]
    public async Task Export_WritesCanonicalCsvAndSeparateAxisFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var project = new StudioProject
            {
                Name = "Export Test",
                Actions =
                [
                    new AuthoredAction
                    {
                        Name = "attack,heavy",
                        FileName = "attack",
                        Description = "Quoted \"description\"",
                        Variant = "intense",
                        DurationMilliseconds = 1200,
                        Loop = true,
                        Tracks =
                        [
                            new ActionTrack { Points = [new(0, 40), new(600, 90), new(1200, 40)] },
                            new ActionTrack { Axis = EdiAxis.Twist, Points = [new(0, 50), new(1200, 50)] }
                        ]
                    }
                ],
                Bundles = [new BundleDefinition { Name = "Combat", Actions = ["attack,heavy"] }]
            };

            var result = await new EdiExporter().ExportAsync(project, directory);

            Assert.Equal(2, result.DefinitionCount);
            Assert.Equal(3, result.ScriptCount);
            Assert.True(File.Exists(Path.Combine(directory, "Definitions.csv")));
            Assert.True(File.Exists(Path.Combine(directory, "intense", "attack.funscript")));
            Assert.True(File.Exists(Path.Combine(directory, "intense", "attack.twist.funscript")));
            Assert.True(File.Exists(Path.Combine(directory, "intense", "filler.funscript")));
            var csv = await File.ReadAllTextAsync(Path.Combine(directory, "Definitions.csv"));
            Assert.Contains("\"attack,heavy\",attack,0,1200,gallery,true,\"Quoted \"\"description\"\"\"", csv);
            Assert.Contains("filler,filler,0,1200,filler,true,", csv);
            Assert.Equal("-Combat\nattack,heavy\n\n", NormalizeNewlines(await File.ReadAllTextAsync(Path.Combine(directory, "BundleDefinition.txt"))));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Export_WritesBuiltInFillerToEveryVariantAndTracksVariantChanges()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var detailed = new AuthoredAction
            {
                Name = "detailed-scene",
                FileName = "detailed-scene",
                Variant = "Detailed",
                DurationMilliseconds = 1000,
                Tracks = [new ActionTrack { Points = [new(0, 20), new(1000, 20)] }]
            };
            var project = new StudioProject
            {
                Name = "Filler Test",
                Actions =
                [
                    new AuthoredAction
                    {
                        Name = "default-scene",
                        FileName = "default-scene",
                        DurationMilliseconds = 1000,
                        Tracks = [new ActionTrack { Points = [new(0, 80), new(1000, 80)] }]
                    },
                    detailed
                ]
            };

            var exporter = new EdiExporter();
            var result = await exporter.ExportAsync(project, directory);

            Assert.Equal(3, result.DefinitionCount);
            Assert.Equal(4, result.ScriptCount);
            Assert.True(File.Exists(Path.Combine(directory, "filler.funscript")));
            var detailedFiller = Path.Combine(directory, "Detailed", "filler.funscript");
            Assert.True(File.Exists(detailedFiller));
            await using (var stream = File.OpenRead(detailedFiller))
            {
                using var json = await JsonDocument.ParseAsync(stream);
                var actions = json.RootElement.GetProperty("actions").EnumerateArray().ToArray();
                Assert.Equal(new[] { 0, 600, 1200 },
                    actions.Select(action => action.GetProperty("at").GetInt32()).ToArray());
                Assert.Equal(new[] { 0, 40, 0 },
                    actions.Select(action => action.GetProperty("pos").GetInt32()).ToArray());
            }
            var manifest = await File.ReadAllTextAsync(Path.Combine(directory, ".edi-integration-studio-export.json"));
            Assert.Contains("Detailed/filler.funscript", manifest);
            Assert.Contains("filler.funscript", manifest);

            detailed.Variant = "intense";
            await exporter.ExportAsync(project, directory);

            Assert.False(File.Exists(detailedFiller));
            Assert.True(File.Exists(Path.Combine(directory, "intense", "filler.funscript")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Export_WritesOneDefinitionAndOneScriptPerVariantForSharedRenditions()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var definitionId = Guid.NewGuid();
            var detailed = new AuthoredAction
            {
                DefinitionId = definitionId,
                Name = "scene",
                FileName = "scene",
                Variant = "detailed",
                DurationMilliseconds = 1000,
                Tracks = [new ActionTrack { Points = [new(0, 20), new(1000, 20)] }]
            };
            var combined = new AuthoredAction
            {
                DefinitionId = definitionId,
                Name = "scene",
                FileName = "scene",
                Variant = "G-MarieMoo",
                DurationMilliseconds = 1000,
                Tracks = [new ActionTrack { Points = [new(0, 80), new(1000, 80)] }]
            };
            var project = new StudioProject
            {
                Variants = ["detailed", "G-MarieMoo"],
                Actions = [detailed, combined]
            };

            var result = await new EdiExporter().ExportAsync(project, directory);

            Assert.Equal(2, result.DefinitionCount);
            Assert.Equal(4, result.ScriptCount);
            Assert.True(File.Exists(Path.Combine(directory, "detailed", "scene.funscript")));
            Assert.True(File.Exists(Path.Combine(directory, "G-MarieMoo", "scene.funscript")));
            var lines = (await File.ReadAllLinesAsync(Path.Combine(directory, "Definitions.csv")))
                .Where(line => line.StartsWith("scene,", StringComparison.Ordinal))
                .ToArray();
            Assert.Single(lines);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Export_UsesLegacyIntegerActionsWithoutMetadata()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var action = new AuthoredAction
            {
                Name = "scene",
                FileName = "scene",
                DurationMilliseconds = 1000,
                Loop = true,
                Tracks = [new ActionTrack { Points = [new(100, 20), new(900, 80)] }]
            };
            await new EdiExporter().ExportAsync(new StudioProject { Actions = [action] }, directory);

            await using var stream = File.OpenRead(Path.Combine(directory, "scene.funscript"));
            using var json = await JsonDocument.ParseAsync(stream);
            var root = json.RootElement;
            Assert.Equal("1.0", root.GetProperty("version").GetString());
            Assert.False(root.TryGetProperty("metadata", out _));
            var actions = root.GetProperty("actions").EnumerateArray().ToArray();
            Assert.Equal(0, actions[0].GetProperty("at").GetInt32());
            Assert.Equal(1000, actions[^1].GetProperty("at").GetInt32());
            Assert.Equal(actions[0].GetProperty("pos").GetInt32(), actions[^1].GetProperty("pos").GetInt32());
            Assert.All(actions, item =>
            {
                Assert.Equal(JsonValueKind.Number, item.GetProperty("at").ValueKind);
                Assert.True(item.GetProperty("at").TryGetInt32(out _));
                Assert.True(item.GetProperty("pos").TryGetInt32(out _));
            });
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void PreparePoints_CreatesStableLoopBoundaries()
    {
        var points = EdiExporter.PreparePoints([new(100, 25), new(700, 80)], 1000, true);

        Assert.Equal(new FunscriptPoint(0, 25), points[0]);
        Assert.Equal(new FunscriptPoint(1000, 25), points[^1]);
    }

    [Fact]
    public async Task PreviewScript_IsTheExactJsonWrittenByExport()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            FunscriptPoint[] points = [new(0, 61), new(500, 100), new(900, 0)];
            var preview = EdiExporter.CreateFunscriptPreview(points, 1000, loop: true);
            var scene = new AuthoredAction
            {
                Name = "scene",
                FileName = "scene",
                DurationMilliseconds = 1000,
                Loop = true,
                Tracks = [new ActionTrack { Points = points.ToList() }]
            };

            await new EdiExporter().ExportAsync(new StudioProject { Actions = [scene] }, directory);

            Assert.Equal(preview.Json, await File.ReadAllTextAsync(Path.Combine(directory, "scene.funscript")));
            Assert.Equal(4, preview.ExportedPointCount);
            Assert.True(preview.BoundaryInserted);
            Assert.True(preview.LoopClosureApplied);
            Assert.Equal(100, preview.LoopClosureIntervalMilliseconds);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Export_GeneratesLinearlyScaledProjectSpeedVariants()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var action = new AuthoredAction
            {
                Name = "Boss1 - p1fin",
                FileName = "boss1-p1fin",
                DurationMilliseconds = 2250,
                Tracks = [new ActionTrack { Points = [new(0, 0), new(1000, 75), new(2250, 0)] }]
            };
            var project = new StudioProject
            {
                SpeedMultipliers = [0.75, 1, 2],
                Actions = [action]
            };

            var result = await new EdiExporter().ExportAsync(project, directory);

            Assert.Equal(4, result.DefinitionCount);
            Assert.Equal(4, result.ScriptCount);
            var csv = await File.ReadAllTextAsync(Path.Combine(directory, "Definitions.csv"));
            Assert.Contains("Boss1 - p1fin [0.75x],boss1-p1fin--speed-0_75x,0,3000", csv);
            Assert.Contains("Boss1 - p1fin,boss1-p1fin,0,2250", csv);
            Assert.Contains("Boss1 - p1fin [2x],boss1-p1fin--speed-2x,0,1125", csv);

            await using var stream = File.OpenRead(Path.Combine(directory, "boss1-p1fin--speed-0_75x.funscript"));
            using var json = await JsonDocument.ParseAsync(stream);
            var points = json.RootElement.GetProperty("actions").EnumerateArray().ToArray();
            Assert.Equal([0, 1333, 3000], points.Select(point => point.GetProperty("at").GetInt32()).ToArray());
            Assert.Equal([0, 75, 0], points.Select(point => point.GetProperty("pos").GetInt32()).ToArray());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ScalePoints_PreservesShapeAcrossLinearDurationChange()
    {
        var points = EdiSpeedVariants.ScalePoints([new(0, 10), new(750, 80), new(2250, 10)], 2250, 3000);

        Assert.Equal([new FunscriptPoint(0, 10), new FunscriptPoint(1000, 80), new FunscriptPoint(3000, 10)], points);
    }

    [Fact]
    public async Task Export_ReexportRemovesPreviouslyManagedAxesAndBundle()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var action = new AuthoredAction
            {
                Name = "scene",
                FileName = "scene",
                DurationMilliseconds = 1000,
                Tracks =
                [
                    new ActionTrack { Points = [new(0, 50), new(1000, 50)] },
                    new ActionTrack { Axis = EdiAxis.Twist, Points = [new(0, 50), new(1000, 50)] }
                ]
            };
            var project = new StudioProject
            {
                Actions = [action],
                Bundles = [new BundleDefinition { Name = "Scenes", Actions = ["scene"] }]
            };
            var exporter = new EdiExporter();
            await exporter.ExportAsync(project, directory);
            Assert.True(File.Exists(Path.Combine(directory, "scene.twist.funscript")));
            Assert.True(File.Exists(Path.Combine(directory, "BundleDefinition.txt")));

            action.Tracks.RemoveAll(track => track.Axis == EdiAxis.Twist);
            project.Bundles.Clear();
            await exporter.ExportAsync(project, directory);

            Assert.False(File.Exists(Path.Combine(directory, "scene.twist.funscript")));
            Assert.False(File.Exists(Path.Combine(directory, "BundleDefinition.txt")));
            Assert.True(File.Exists(Path.Combine(directory, ".edi-integration-studio-export.json")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Export_AdoptsUnmanagedGalleryWithoutOverwritingUnrelatedFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "unrelated.txt"), "keep me");
            var action = new AuthoredAction
            {
                Name = "scene",
                FileName = "scene",
                Tracks = [new ActionTrack { Points = [new(0, 50), new(1000, 50)] }]
            };

            await new EdiExporter().ExportAsync(new StudioProject { Actions = [action] }, directory);

            Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(directory, "unrelated.txt")));
            Assert.True(File.Exists(Path.Combine(directory, "scene.funscript")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Export_RefusesUnmanagedFileCollision()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "scene.funscript"), "keep me");
            var scene = new AuthoredAction
            {
                Name = "scene",
                FileName = "scene",
                Tracks = [new ActionTrack { Points = [new(0, 50), new(1000, 50)] }]
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new EdiExporter().ExportAsync(new StudioProject { Actions = [scene] }, directory));
            Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(directory, "scene.funscript")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Export_RefusesNewCollisionMissingFromExistingManifest()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var scene = new AuthoredAction
            {
                Name = "scene",
                FileName = "scene",
                Variant = "detailed",
                Tracks = [new ActionTrack { Points = [new(0, 50), new(1000, 50)] }]
            };
            var project = new StudioProject { Actions = [scene] };
            var exporter = new EdiExporter();
            await exporter.ExportAsync(project, directory);
            var manuallyOwned = Path.Combine(directory, "intense", "scene.funscript");
            Directory.CreateDirectory(Path.GetDirectoryName(manuallyOwned)!);
            await File.WriteAllTextAsync(manuallyOwned, "keep me");
            scene.Variant = "intense";

            await Assert.ThrowsAsync<InvalidOperationException>(() => exporter.ExportAsync(project, directory));

            Assert.Equal("keep me", await File.ReadAllTextAsync(manuallyOwned));
            Assert.True(File.Exists(Path.Combine(directory, "detailed", "scene.funscript")));
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

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n");
}
