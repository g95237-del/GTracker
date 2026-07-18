using GTracker.App.Capture;
using GTracker.App.Projects;

namespace GTracker.App.Tests;

public sealed class ClipArchiveTests
{
    [Fact]
    public async Task SaveAndLoad_RoundTripsFrameDataAndTiming()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "clip.ediclip");
        try
        {
            var clip = new CapturedClip(
                [new(0, [1, 2, 3], 640, 360), new(50, [4, 5, 6], 640, 360)], 50);
            var store = new ClipArchive();

            await store.SaveAsync(path, clip);
            var loaded = await store.LoadAsync(path);

            Assert.Equal(50, loaded.DurationMilliseconds);
            Assert.Equal(2, loaded.Frames.Count);
            Assert.Equal([1, 2, 3], loaded.Frames[0].Data);
            Assert.Equal(640, loaded.Frames[1].Width);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Load_RejectsArchiveWithoutManifest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "invalid.ediclip");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(path, []);
            await Assert.ThrowsAnyAsync<InvalidDataException>(() => new ClipArchive().LoadAsync(path));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
