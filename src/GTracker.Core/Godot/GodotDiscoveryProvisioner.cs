using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GTracker.Core.Godot;

public sealed record GodotDiscoveryInstallResult(
    string GameRoot,
    string OverrideConfigPath,
    string ScriptPath,
    string TelemetryPath,
    string ManifestPath,
    bool ReplacedExistingOverride);

public sealed class GodotDiscoveryProvisioner
{
    private const int ManifestSchemaVersion = 1;
    private const string RuntimeDirectoryName = "GTrackerRuntime";
    private const string ComponentDirectoryName = "Godot";
    private const string ManifestFileName = "install.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly GodotGameInspector _inspector = new();

    public GodotDiscoveryInstallResult Install(string executablePath, CancellationToken cancellationToken = default)
    {
        var inspection = _inspector.Inspect(executablePath);
        if (!inspection.IsSupported) throw new InvalidOperationException("A supported unencrypted Godot export is required.");
        if (inspection.EngineMajorVersion != 3)
            throw new NotSupportedException("Discovery installation currently supports Godot 3 exports only.");
        EnsureGameClosed(inspection.ExecutablePath);

        var gameRoot = Path.GetDirectoryName(inspection.ExecutablePath)!;
        var componentRoot = Path.Combine(gameRoot, RuntimeDirectoryName, ComponentDirectoryName);
        var scriptPath = Path.Combine(componentRoot, "gtracker_discovery.gd");
        var telemetryPath = Path.Combine(componentRoot, "telemetry.tsv");
        var manifestPath = Path.Combine(componentRoot, ManifestFileName);
        var overridePath = Path.Combine(gameRoot, "override.cfg");
        var backupPath = Path.Combine(componentRoot, "backup", "override.cfg");
        ValidateManagedPaths(gameRoot, componentRoot, scriptPath, telemetryPath, manifestPath, overridePath, backupPath);
        if (File.Exists(manifestPath)) throw new IOException("A Godot discovery installation already exists. Remove it before reinstalling.");
        if (File.Exists(scriptPath) || File.Exists(backupPath))
            throw new IOException("Refusing to replace unowned files in the Godot discovery directory.");

        var overrideExisted = File.Exists(overridePath);
        var telemetryExisted = File.Exists(telemetryPath);
        var originalOverride = overrideExisted ? File.ReadAllBytes(overridePath) : [];
        var script = GodotDiscoveryScript.Create(inspection.EngineMajorVersion);
        var installedOverride = GodotOverrideConfig.AddAutoload(originalOverride, scriptPath);
        var manifest = new GodotDiscoveryManifest
        {
            GameExecutable = inspection.ExecutablePath,
            GameRoot = gameRoot,
            GodotVersion = inspection.EngineVersion,
            OverrideExistedBeforeInstall = overrideExisted,
            OriginalOverrideSha256 = overrideExisted ? Hash(originalOverride) : string.Empty,
            InstalledOverrideSha256 = Hash(installedOverride),
            ScriptSha256 = Hash(Encoding.UTF8.GetBytes(script)),
            BackupRelativePath = overrideExisted ? Relative(gameRoot, backupPath) : string.Empty,
            InstalledAt = DateTimeOffset.UtcNow
        };

        var stage = CreateTemporaryDirectory("godot-install");
        try
        {
            Stage(stage, Relative(gameRoot, scriptPath), Encoding.UTF8.GetBytes(script));
            if (!telemetryExisted) Stage(stage, Relative(gameRoot, telemetryPath), []);
            Stage(stage, Relative(gameRoot, overridePath), installedOverride);
            if (overrideExisted) Stage(stage, Relative(gameRoot, backupPath), originalOverride);
            Stage(stage, Relative(gameRoot, manifestPath), JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
            cancellationToken.ThrowIfCancellationRequested();
            EnsureGameClosed(inspection.ExecutablePath);
            if (File.Exists(overridePath) != overrideExisted ||
                overrideExisted && Hash(File.ReadAllBytes(overridePath)) != Hash(originalOverride))
                throw new IOException("override.cfg changed while installation was being prepared. Analyze and retry without concurrent edits.");
            var files = new List<string>();
            if (overrideExisted) files.Add(Relative(gameRoot, backupPath));
            files.Add(Relative(gameRoot, scriptPath));
            if (!telemetryExisted) files.Add(Relative(gameRoot, telemetryPath));
            files.Add(Relative(gameRoot, overridePath));
            files.Add(Relative(gameRoot, manifestPath));
            InstallTransactional(stage, gameRoot, files, overridePath, originalOverride, overrideExisted, cancellationToken);
        }
        finally
        {
            DeleteDirectoryBestEffort(stage);
        }

        return new(gameRoot, overridePath, scriptPath, telemetryPath, manifestPath, overrideExisted);
    }

    public void Uninstall(string executablePath)
    {
        executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Game executable was not found.", executablePath);
        EnsureGameClosed(executablePath);
        var gameRoot = Path.GetDirectoryName(executablePath)!;
        var componentRoot = Path.Combine(gameRoot, RuntimeDirectoryName, ComponentDirectoryName);
        var manifestPath = Path.Combine(componentRoot, ManifestFileName);
        var overridePath = Path.Combine(gameRoot, "override.cfg");
        var scriptPath = Path.Combine(componentRoot, "gtracker_discovery.gd");
        var telemetryPath = Path.Combine(componentRoot, "telemetry.tsv");
        ValidateManagedPaths(gameRoot, componentRoot, manifestPath, overridePath, scriptPath, telemetryPath);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("The Godot discovery ownership manifest was not found.", manifestPath);
        var manifest = JsonSerializer.Deserialize<GodotDiscoveryManifest>(File.ReadAllBytes(manifestPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Godot discovery manifest is invalid.");
        ValidateManifest(manifest, executablePath, gameRoot);

        if (!File.Exists(overridePath) || Hash(File.ReadAllBytes(overridePath)) != manifest.InstalledOverrideSha256)
            throw new IOException("override.cfg changed after installation. Automatic removal was stopped to preserve those edits.");
        if (!File.Exists(scriptPath) || Hash(File.ReadAllBytes(scriptPath)) != manifest.ScriptSha256)
            throw new IOException("The installed Godot discovery script was modified. Automatic removal was stopped.");

        if (manifest.OverrideExistedBeforeInstall)
        {
            var backupPath = ResolveContained(gameRoot, manifest.BackupRelativePath);
            ValidateManagedPaths(gameRoot, backupPath);
            var backup = File.ReadAllBytes(backupPath);
            if (Hash(backup) != manifest.OriginalOverrideSha256) throw new InvalidDataException("The override.cfg backup is damaged.");
            EnsureGameClosed(executablePath);
            ValidateManagedPaths(gameRoot, overridePath, scriptPath, manifestPath, backupPath);
            if (Hash(File.ReadAllBytes(overridePath)) != manifest.InstalledOverrideSha256)
                throw new IOException("override.cfg changed while removal was being prepared.");
            RemoveTransactional(
            [
                new(overridePath, backup),
                new(scriptPath, null),
                new(manifestPath, null),
                new(backupPath, null)
            ]);
        }
        else
        {
            EnsureGameClosed(executablePath);
            ValidateManagedPaths(gameRoot, overridePath, scriptPath, manifestPath);
            if (Hash(File.ReadAllBytes(overridePath)) != manifest.InstalledOverrideSha256)
                throw new IOException("override.cfg changed while removal was being prepared.");
            RemoveTransactional(
            [
                new(overridePath, null),
                new(scriptPath, null),
                new(manifestPath, null)
            ]);
        }
        DeleteEmptyDirectories(Path.Combine(componentRoot, "backup"), componentRoot);
        // Telemetry is intentionally preserved for later mapping and troubleshooting.
        if (!File.Exists(telemetryPath)) DeleteEmptyDirectories(componentRoot, Path.Combine(gameRoot, RuntimeDirectoryName));
    }

    private static void ValidateManifest(GodotDiscoveryManifest manifest, string executablePath, string gameRoot)
    {
        if (manifest.SchemaVersion != ManifestSchemaVersion) throw new InvalidDataException("Unsupported Godot discovery manifest version.");
        if (!Path.GetFullPath(manifest.GameExecutable).Equals(executablePath, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(manifest.GameRoot).Equals(gameRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Godot discovery manifest belongs to another game target.");
        if (manifest.OverrideExistedBeforeInstall && string.IsNullOrWhiteSpace(manifest.BackupRelativePath))
            throw new InvalidDataException("The Godot discovery manifest does not identify its override backup.");
    }

    private static void ValidateManagedPaths(string gameRoot, params string[] paths)
    {
        RejectReparsePoint(gameRoot);
        foreach (var path in paths)
        {
            _ = ResolveContained(gameRoot, Relative(gameRoot, path));
            var current = gameRoot;
            foreach (var segment in Path.GetRelativePath(gameRoot, path).Split(Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (Directory.Exists(current) || File.Exists(current)) RejectReparsePoint(current);
            }
        }
    }

    private static void InstallTransactional(string stage, string gameRoot, IReadOnlyList<string> relativePaths,
        string overridePath, byte[] expectedOverride, bool overrideExisted, CancellationToken cancellationToken)
    {
        var rollback = CreateTemporaryDirectory("godot-rollback");
        var changes = new List<(string Destination, bool Existed, string Backup)>();
        var preserveRollback = false;
        try
        {
            foreach (var relativePath in relativePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = ResolveContained(stage, relativePath);
                var destination = ResolveContained(gameRoot, relativePath);
                ValidateManagedPaths(gameRoot, destination);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var existed = File.Exists(destination);
                if (existed && !destination.Equals(Path.Combine(gameRoot, "override.cfg"), StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Refusing to overwrite an existing file: {destination}");
                var backup = ResolveContained(rollback, relativePath);
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup);
                }
                changes.Add((destination, existed, backup));
                if (destination.Equals(overridePath, StringComparison.OrdinalIgnoreCase) &&
                    (File.Exists(destination) != overrideExisted ||
                     overrideExisted && Hash(File.ReadAllBytes(destination)) != Hash(expectedOverride)))
                    throw new IOException("override.cfg changed immediately before installation commit.");
                WriteAtomic(destination, File.ReadAllBytes(source));
            }
        }
        catch (Exception installException)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var change in changes.AsEnumerable().Reverse())
            {
                try
                {
                    ValidateManagedPaths(gameRoot, change.Destination);
                    if (change.Existed) WriteAtomic(change.Destination, File.ReadAllBytes(change.Backup));
                    else if (File.Exists(change.Destination)) File.Delete(change.Destination);
                }
                catch (Exception exception)
                {
                    rollbackErrors.Add(new IOException($"Could not restore {change.Destination}.", exception));
                }
            }
            if (rollbackErrors.Count > 0)
            {
                preserveRollback = true;
                throw new AggregateException(
                    $"Godot discovery installation failed and rollback was incomplete. Recovery files were retained at {rollback}.",
                    [installException, .. rollbackErrors]);
            }
            throw;
        }
        finally
        {
            if (!preserveRollback) DeleteDirectoryBestEffort(rollback);
        }
    }

    private static void RemoveTransactional(IReadOnlyList<RemovalChange> changes)
    {
        var originals = changes.ToDictionary(change => change.Path, change => File.Exists(change.Path) ? File.ReadAllBytes(change.Path) : null,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var change in changes)
            {
                var gameRoot = FindGameRoot(change.Path);
                ValidateManagedPaths(gameRoot, change.Path);
                if (change.Replacement is null)
                {
                    if (File.Exists(change.Path)) File.Delete(change.Path);
                }
                else
                {
                    WriteAtomic(change.Path, change.Replacement);
                }
            }
        }
        catch (Exception removalException)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var pair in originals.Reverse())
            {
                try
                {
                    if (pair.Value is null)
                    {
                        if (File.Exists(pair.Key)) File.Delete(pair.Key);
                    }
                    else
                    {
                        WriteAtomic(pair.Key, pair.Value);
                    }
                }
                catch (Exception exception)
                {
                    rollbackErrors.Add(new IOException($"Could not restore {pair.Key}.", exception));
                }
            }
            if (rollbackErrors.Count > 0)
                throw new AggregateException("Godot discovery removal failed and rollback was incomplete.",
                    [removalException, .. rollbackErrors]);
            throw;
        }
    }

    private static string FindGameRoot(string path)
    {
        var current = Path.GetDirectoryName(Path.GetFullPath(path))!;
        while (Path.GetFileName(current).Equals(ComponentDirectoryName, StringComparison.OrdinalIgnoreCase) ||
               Path.GetFileName(current).Equals(RuntimeDirectoryName, StringComparison.OrdinalIgnoreCase) ||
               Path.GetFileName(current).Equals("backup", StringComparison.OrdinalIgnoreCase))
            current = Path.GetDirectoryName(current)!;
        return current;
    }

    private static void EnsureGameClosed(string executablePath)
    {
        var target = Path.GetFullPath(executablePath);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
        {
            using (process)
            {
                try
                {
                    var runningPath = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(runningPath) &&
                        Path.GetFullPath(runningPath).Equals(target, StringComparison.OrdinalIgnoreCase))
                        throw new GameRunningException();
                }
                catch (GameRunningException)
                {
                    throw new InvalidOperationException("Close the selected Godot game before changing discovery files.");
                }
                catch (InvalidOperationException)
                {
                    // The process exited while its path was being inspected.
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    throw new InvalidOperationException("A matching game process is running and its path could not be verified.", exception);
                }
            }
        }
    }

    private static void Stage(string stage, string relativePath, byte[] content)
    {
        var path = ResolveContained(stage, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, content);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));

    private static string ResolveContained(string root, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath)) throw new InvalidDataException("Managed path must be relative.");
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Managed path escapes the game root.");
        return path;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reparse points are not supported for managed installation paths: {path}");
    }

    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content));

    private static string CreateTemporaryDirectory(string purpose)
    {
        var path = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio", purpose + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void DeleteEmptyDirectories(string start, string stop)
    {
        var current = start;
        while (Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
        {
            Directory.Delete(current);
            if (current.Equals(stop, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current)!;
        }
    }

    private sealed class GodotDiscoveryManifest
    {
        public int SchemaVersion { get; set; } = ManifestSchemaVersion;
        public string GameExecutable { get; set; } = string.Empty;
        public string GameRoot { get; set; } = string.Empty;
        public string GodotVersion { get; set; } = string.Empty;
        public bool OverrideExistedBeforeInstall { get; set; }
        public string OriginalOverrideSha256 { get; set; } = string.Empty;
        public string InstalledOverrideSha256 { get; set; } = string.Empty;
        public string ScriptSha256 { get; set; } = string.Empty;
        public string BackupRelativePath { get; set; } = string.Empty;
        public DateTimeOffset InstalledAt { get; set; }
    }

    private sealed record RemovalChange(string Path, byte[]? Replacement);
    private sealed class GameRunningException : Exception;
}
