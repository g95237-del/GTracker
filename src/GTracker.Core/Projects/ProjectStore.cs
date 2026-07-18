using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;

namespace GTracker.Core.Projects;

public sealed class ProjectStore
{
    public const string ProjectFileName = "project.edi.json";
    public const string HistoryDirectoryName = ".history";
    private const int MaximumHistoryFiles = 25;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SaveLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task SaveAsync(string projectDirectory, StudioProject project, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentNullException.ThrowIfNull(project);
        Directory.CreateDirectory(projectDirectory);
        project.UpdatedAt = DateTimeOffset.UtcNow;

        var path = Path.Combine(projectDirectory, ProjectFileName);
        var saveLock = SaveLocks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await saveLock.WaitAsync(cancellationToken);
        var temporaryPath = Path.Combine(projectDirectory, $".{ProjectFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, project, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            BackupCurrentProject(projectDirectory, path);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            saveLock.Release();
        }
    }

    public async Task<StudioProject> LoadAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var path = Path.Combine(projectDirectory, ProjectFileName);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var project = await JsonSerializer.DeserializeAsync<StudioProject>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The project file is empty.");

        if (project.SchemaVersion != StudioProject.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Project schema {project.SchemaVersion} is not supported by this build (expected {StudioProject.CurrentSchemaVersion}).");
        }

        project.Game ??= new GameTarget();
        project.Game.Simulator ??= new LinearSimulatorLayout();
        project.Game.TriggerMappings ??= [];
        project.Actions ??= [];
        project.Bundles ??= [];
        foreach (var action in project.Actions)
        {
            action.UnitySceneName ??= string.Empty;
            action.UnityAnimationName ??= string.Empty;
            action.Tracks ??= [];
            foreach (var track in action.Tracks)
            {
                track.Points ??= [];
            }
        }

        return project;
    }

    private static void BackupCurrentProject(string projectDirectory, string path)
    {
        if (!File.Exists(path)) return;
        var historyDirectory = Path.Combine(projectDirectory, HistoryDirectoryName);
        Directory.CreateDirectory(historyDirectory);
        var backupPath = Path.Combine(historyDirectory,
            $"project.edi.{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.json");
        File.Copy(path, backupPath, overwrite: false);
        foreach (var stale in Directory.EnumerateFiles(historyDirectory, "project.edi.*.json")
                     .Select(file => new FileInfo(file))
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                     .Skip(MaximumHistoryFiles))
        {
            try { stale.Delete(); } catch (IOException) { }
        }
    }
}
