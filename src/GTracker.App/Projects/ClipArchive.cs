using System.IO.Compression;
using System.IO;
using System.Text.Json;
using GTracker.App.Capture;

namespace GTracker.App.Projects;

public sealed class ClipArchive
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task SaveAsync(string path, CapturedClip clip, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(clip);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, false))
            {
                var frames = new List<ClipFrameManifest>(clip.Frames.Count);
                for (var index = 0; index < clip.Frames.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var frame = clip.Frames[index];
                    var entryName = $"frames/{index:D6}.jpg";
                    var entry = archive.CreateEntry(entryName, CompressionLevel.NoCompression);
                    await using var entryStream = entry.Open();
                    await entryStream.WriteAsync(frame.Data, cancellationToken);
                    frames.Add(new(entryName, frame.OffsetMilliseconds, frame.Width, frame.Height));
                }

                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream,
                    new ClipManifest(1, clip.DurationMilliseconds, frames), JsonOptions, cancellationToken);
            }

            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public async Task<CapturedClip> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, false);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Clip manifest is missing.");
        ClipManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<ClipManifest>(manifestStream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("Clip manifest is empty.");
        }
        if (manifest.Version != 1) throw new InvalidDataException($"Unsupported clip version {manifest.Version}.");

        var frames = new List<ClipFrame>(manifest.Frames.Count);
        foreach (var item in manifest.Frames.OrderBy(frame => frame.OffsetMilliseconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(item.Entry) ?? throw new InvalidDataException($"Clip frame '{item.Entry}' is missing.");
            await using var entryStream = entry.Open();
            using var memory = new MemoryStream(checked((int)entry.Length));
            await entryStream.CopyToAsync(memory, cancellationToken);
            frames.Add(new(item.OffsetMilliseconds, memory.ToArray(), item.Width, item.Height));
        }

        return new(frames, manifest.DurationMilliseconds);
    }

    private sealed record ClipManifest(int Version, int DurationMilliseconds, List<ClipFrameManifest> Frames);
    private sealed record ClipFrameManifest(string Entry, int OffsetMilliseconds, int Width, int Height);
}
