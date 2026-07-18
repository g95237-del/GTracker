using GTracker.App.Projects;

namespace GTracker.App.Tests;

public sealed class RecentProjectStoreTests
{
    [Fact]
    public async Task RememberAsync_KeepsNewestFirstDeduplicatedAndCapped()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new RecentProjectStore(Path.Combine(directory, "settings", "recent-projects.json"));
            var paths = Enumerable.Range(0, 12).Select(index => Path.Combine(directory, $"project-{index}")).ToArray();
            foreach (var path in paths) await store.RememberAsync(path);
            await store.RememberAsync(paths[^1].ToUpperInvariant());

            var loaded = await store.LoadAsync();

            Assert.Equal(10, loaded.Count);
            Assert.Equal(paths[^1], loaded[0], ignoreCase: true);
            Assert.Equal(paths[2], loaded[^1], ignoreCase: true);
            Assert.Equal(loaded.Count, loaded.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LoadAsync_ReturnsEmptyForMalformedOrUnsupportedSettings()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "recent-projects.json");
            await File.WriteAllTextAsync(path, "not-json");
            var store = new RecentProjectStore(path);
            Assert.Empty(await store.LoadAsync());

            await File.WriteAllTextAsync(path, """
                { "schemaVersion": 99, "projectDirectories": ["C:\\Project"] }
                """);
            Assert.Empty(await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SaveAsync_IgnoresInvalidPathsAndLeavesNoTemporaryFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var settingsDirectory = Path.Combine(directory, "settings");
            var store = new RecentProjectStore(Path.Combine(settingsDirectory, "recent-projects.json"));
            var valid = Path.Combine(directory, "valid-project");

            await store.SaveAsync([string.Empty, "relative-project", valid, valid]);

            Assert.Equal([valid], await store.LoadAsync());
            Assert.Empty(Directory.EnumerateFiles(settingsDirectory, "*.tmp-*"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RemoveAsync_RemovesOnlyMatchingPath()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new RecentProjectStore(Path.Combine(directory, "recent-projects.json"));
            var first = Path.Combine(directory, "first");
            var second = Path.Combine(directory, "second");
            await store.SaveAsync([first, second]);

            await store.RemoveAsync(first.ToUpperInvariant());

            Assert.Equal([second], await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task LoadAsync_PropagatesCancellation()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new RecentProjectStore(Path.Combine(directory, "recent-projects.json"));
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
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
