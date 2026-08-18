using GTracker.App.Shell;

namespace GTracker.App.Tests;

public sealed class EngineSelectionTests
{
    [Fact]
    public void Catalog_DefinesCompleteDistinctProfiles()
    {
        Assert.Equal(Enum.GetValues<IntegrationEngine>().Length, EngineUiCatalog.Profiles.Count);
        Assert.Equal(EngineUiCatalog.Profiles.Count,
            EngineUiCatalog.Profiles.Select(profile => profile.Engine).Distinct().Count());
        foreach (var profile in EngineUiCatalog.Profiles)
        {
            Assert.Equal(profile.DisplayName, profile.ToString());
            Assert.Equal(EngineUiCatalog.PaletteKeys.Order(), profile.Palette.Keys.Order());
            Assert.All(profile.Palette.Values, color => Assert.Matches("^#[0-9A-F]{6,8}$", color));
            Assert.False(string.IsNullOrWhiteSpace(profile.WorkflowSummary));
            Assert.False(string.IsNullOrWhiteSpace(profile.WorkflowSteps));
        }
    }

    [Theory]
    [InlineData(IntegrationEngine.Unity)]
    [InlineData(IntegrationEngine.Godot)]
    [InlineData(IntegrationEngine.Unreal)]
    [InlineData(IntegrationEngine.RpgMaker)]
    public async Task PreferenceStore_RoundTripsEngine(IntegrationEngine engine)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new EnginePreferenceStore(Path.Combine(directory, "settings", "engine.json"));
            await store.SaveAsync(engine);

            Assert.Equal(engine, await store.LoadAsync());
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory, "settings"), "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task PreferenceStore_FallsBackToUnityForUnknownOrMalformedSettings()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "engine.json");
            await File.WriteAllTextAsync(path, "not-json");
            var store = new EnginePreferenceStore(path);
            Assert.Equal(IntegrationEngine.Unity, await store.LoadAsync());

            await File.WriteAllTextAsync(path, """
                { "schemaVersion": 1, "engine": "SourceEngine" }
                """);
            Assert.Equal(IntegrationEngine.Unity, await store.LoadAsync());
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
