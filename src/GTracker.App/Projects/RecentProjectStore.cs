using System.IO;
using System.Text.Json;

namespace GTracker.App.Projects;

public sealed class RecentProjectStore
{
    private const int SchemaVersion = 1;
    private const int MaximumEntries = 10;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath;

    public RecentProjectStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EdiIntegrationStudio", "recent-projects.json");
    }

    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RememberAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(projectDirectory) ??
                         throw new ArgumentException("Project directory must be an absolute path.", nameof(projectDirectory));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var paths = (await LoadCoreAsync(cancellationToken))
                .Where(path => !path.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                .Prepend(normalized)
                .Take(MaximumEntries)
                .ToArray();
            await SaveCoreAsync(paths, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizePath(projectDirectory);
        if (normalized is null) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var paths = (await LoadCoreAsync(cancellationToken))
                .Where(path => !path.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            await SaveCoreAsync(paths, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IEnumerable<string> projectDirectories, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectDirectories);
        var normalized = NormalizePaths(projectDirectories);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SaveCoreAsync(normalized, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath)) return [];
        try
        {
            await using var stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var settings = await JsonSerializer.DeserializeAsync<RecentProjectSettings>(stream, JsonOptions, cancellationToken);
            return settings?.SchemaVersion == SchemaVersion
                ? NormalizePaths(settings.ProjectDirectories ?? [])
                : [];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private async Task SaveCoreAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!;
        Directory.CreateDirectory(parent);
        var temporary = _settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream,
                    new RecentProjectSettings { ProjectDirectories = paths.ToList() }, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string[] NormalizePaths(IEnumerable<string> paths) => paths
        .Select(NormalizePath)
        .Where(path => path is not null)
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaximumEntries)
        .ToArray();

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed class RecentProjectSettings
    {
        public int SchemaVersion { get; set; } = RecentProjectStore.SchemaVersion;
        public List<string>? ProjectDirectories { get; set; } = [];
    }
}
