using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using GTracker.Core.Projects;

namespace GTracker.Core.Unity;

public sealed record BepInExInstallResult(
    string GameRoot,
    int InstalledFileCount,
    int ReplacedFileCount,
    string PackageSha256);

public sealed record GameInitializationResult(
    TimeSpan Elapsed,
    bool ForcedClose,
    string LogPath);

public sealed record EdiInstallResult(
    string GameRoot,
    int InstalledFileCount,
    int ReplacedFileCount,
    string GalleryPath);

public sealed class UnityRuntimeProvisioner
{
    private const long MaximumArchiveSize = 2L * 1024 * 1024 * 1024;
    private const int MaximumArchiveEntries = 20_000;
    private static readonly TimeSpan MonoInitializationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Il2CppInitializationTimeout = TimeSpan.FromMinutes(10);

    public BepInExInstallResult InstallBepInEx(
        UnityInspectionResult inspection,
        string archivePath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        ValidateSupportedGame(inspection);
        if (!inspection.Architecture.Equals("Amd64", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"The packaged BepInEx builds require an x64 game; detected {inspection.Architecture}.");
        if (!File.Exists(archivePath)) throw new FileNotFoundException("The packaged BepInEx archive was not found.", archivePath);
        ValidateExistingBepInExFlavor(inspection);
        EnsureGameIsClosed(inspection.ExecutablePath);

        var stageDirectory = CreateTemporaryDirectory("BepInEx-stage");
        try
        {
            using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var packageHash = ComputeSha256(stream);
            if (!packageHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The packaged BepInEx archive failed its SHA-256 check. Expected {expectedSha256}, found {packageHash}.");
            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            ValidateBepInExArchive(archive, inspection.Runtime);
            ExtractArchive(archive, stageDirectory, cancellationToken);

            var gameRoot = Path.GetDirectoryName(inspection.ExecutablePath)!;
            var files = Directory.EnumerateFiles(stageDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(stageDirectory, path))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var directories = Directory.EnumerateDirectories(stageDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(stageDirectory, path))
                .OrderBy(path => path.Length)
                .ToArray();
            EnsureGameIsClosed(inspection.ExecutablePath);
            var copy = CopyFilesTransactional(stageDirectory, gameRoot, files, directories, cancellationToken);
            return new(gameRoot, files.Length, copy.ReplacedFiles, packageHash);
        }
        finally
        {
            DeleteDirectoryBestEffort(stageDirectory);
        }
    }

    public EdiInstallResult InstallEdi(
        UnityInspectionResult inspection,
        string sourceDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ValidateSupportedGame(inspection);
        EnsureGameIsClosed(inspection.ExecutablePath);

        sourceDirectory = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"The EDI source folder was not found: {sourceDirectory}");
        RejectReparsePoint(sourceDirectory);
        var gameRoot = Path.GetDirectoryName(inspection.ExecutablePath)!;
        RejectNestedDirectories(sourceDirectory, gameRoot);

        var source = EnumerateEdiSource(sourceDirectory);
        if (!source.Files.Any(path => path.Equals("Edi.exe", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The selected folder does not contain Edi.exe at its root.");
        EnsureProcessIsClosed(Path.Combine(sourceDirectory, "Edi.exe"), "Close the selected EDI instance before copying it.");
        var destinationEdi = Path.Combine(gameRoot, "Edi.exe");
        if (File.Exists(destinationEdi))
            EnsureProcessIsClosed(destinationEdi, "Close the EDI instance in the game folder before replacing it.");

        var galleryPath = Path.Combine(gameRoot, "Gallery");
        if (File.Exists(galleryPath))
            throw new IOException($"Cannot preserve Gallery because a file exists at: {galleryPath}");
        var destinationDirectories = source.Directories.Concat(["Gallery"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        EnsureGameIsClosed(inspection.ExecutablePath);
        var copy = CopyFilesTransactional(sourceDirectory, gameRoot, source.Files, destinationDirectories, cancellationToken);
        return new(gameRoot, source.Files.Count, copy.ReplacedFiles, galleryPath);
    }

    public async Task<GameInitializationResult> InitializeGameAsync(
        UnityInspectionResult inspection,
        IProgress<string>? progress = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ValidateSupportedGame(inspection);
        if (inspection.Runtime == UnityRuntimeKind.Mono &&
            inspection.BepInEx is not (BepInExFlavor.Mono5 or BepInExFlavor.Mono6))
            throw new InvalidOperationException("A Mono BepInEx installation was not detected after extraction.");
        if (inspection.Runtime == UnityRuntimeKind.Il2Cpp && inspection.BepInEx != BepInExFlavor.Il2Cpp6)
            throw new InvalidOperationException("A BepInEx IL2CPP installation was not detected after extraction.");
        EnsureGameIsClosed(inspection.ExecutablePath);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Delay(1, cancellationToken).ConfigureAwait(false);

        var gameRoot = Path.GetDirectoryName(inspection.ExecutablePath)!;
        if (IsDiskLoggingDisabled(Path.Combine(gameRoot, "BepInEx", "config", "BepInEx.cfg")))
            throw new InvalidOperationException("BepInEx disk logging or the Message log level is disabled. Enable it in BepInEx\\config\\BepInEx.cfg so initialization can be verified.");
        var logMonitor = new InitializationLogMonitor(Path.Combine(gameRoot, "BepInEx"));
        var allowedTime = timeout ?? (inspection.Runtime == UnityRuntimeKind.Il2Cpp
            ? Il2CppInitializationTimeout
            : MonoInitializationTimeout);
        if (allowedTime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));

        var startInfo = new ProcessStartInfo(inspection.ExecutablePath)
        {
            WorkingDirectory = gameRoot,
            UseShellExecute = true
        };
        Process? process = null;
        var timer = Stopwatch.StartNew();
        var forcedClose = false;
        var initialized = false;
        try
        {
            progress?.Report($"Launching {Path.GetFileName(inspection.ExecutablePath)} for BepInEx initialization...");
            process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start the selected game executable.");
            var nextProgressAt = TimeSpan.Zero;
            while (timer.Elapsed < allowedTime)
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Refresh();
                initialized = IsInitializationComplete(inspection.Runtime, gameRoot, logMonitor);
                if (initialized) break;
                if (process.HasExited)
                {
                    var replacement = await FindRunningProcessAsync(
                        inspection.ExecutablePath, process.Id, cancellationToken).ConfigureAwait(false);
                    if (replacement is not null)
                    {
                        process.Dispose();
                        process = replacement;
                        progress?.Report("The launcher handed off to the game process; continuing initialization...");
                        continue;
                    }
                    var logTail = logMonitor.ReadLatestTail(4000);
                    throw new InvalidOperationException(
                        $"The game exited with code {process.ExitCode} before BepInEx completed initialization." +
                        (string.IsNullOrWhiteSpace(logTail) ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}{logTail}"));
                }

                if (timer.Elapsed >= nextProgressAt)
                {
                    var activity = inspection.Runtime == UnityRuntimeKind.Il2Cpp && !HasRequiredInteropAssemblies(gameRoot)
                        ? "Generating IL2CPP interop assemblies"
                        : "Waiting for the BepInEx chainloader";
                    progress?.Report($"{activity} ({timer.Elapsed:mm\\:ss} elapsed)...");
                    nextProgressAt = timer.Elapsed + TimeSpan.FromSeconds(5);
                }
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            if (!initialized)
                throw new TimeoutException($"BepInEx did not finish initialization within {allowedTime.TotalMinutes:0.#} minutes.");
            progress?.Report("BepInEx initialization completed. Closing the game...");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (process is not null)
            {
                forcedClose = await CloseGameAsync(process).ConfigureAwait(false);
                process.Dispose();
            }
        }

        return new(timer.Elapsed, forcedClose, logMonitor.ActiveLogPath);
    }

    private static void ValidateSupportedGame(UnityInspectionResult inspection)
    {
        if (!File.Exists(inspection.ExecutablePath))
            throw new FileNotFoundException("The selected game executable was not found.", inspection.ExecutablePath);
        if (inspection.Runtime == UnityRuntimeKind.Unknown)
            throw new InvalidOperationException("A supported Unity Mono or IL2CPP runtime was not detected.");
    }

    private static void ValidateExistingBepInExFlavor(UnityInspectionResult inspection)
    {
        if (inspection.Runtime == UnityRuntimeKind.Mono && inspection.BepInEx == BepInExFlavor.Il2Cpp6)
            throw new InvalidOperationException("An IL2CPP BepInEx installation already exists in this Mono game. Remove the incompatible loader before installing the Mono package.");
        if (inspection.Runtime == UnityRuntimeKind.Il2Cpp &&
            inspection.BepInEx is BepInExFlavor.Mono5 or BepInExFlavor.Mono6)
            throw new InvalidOperationException("A Mono BepInEx installation already exists in this IL2CPP game. Remove the incompatible loader before installing the IL2CPP package.");
    }

    private static void ValidateBepInExArchive(ZipArchive archive, UnityRuntimeKind runtime)
    {
        if (archive.Entries.Count > MaximumArchiveEntries)
            throw new InvalidDataException("The BepInEx archive contains too many entries.");
        var totalLength = archive.Entries.Sum(entry => entry.Length);
        if (totalLength > MaximumArchiveSize)
            throw new InvalidDataException("The BepInEx archive expands beyond the permitted size.");

        var names = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var common = new[] { "winhttp.dll", "doorstop_config.ini", "BepInEx/core/BepInEx.Core.dll" };
        var runtimeMarker = runtime == UnityRuntimeKind.Il2Cpp
            ? "BepInEx/core/BepInEx.Unity.IL2CPP.dll"
            : "BepInEx/core/BepInEx.Unity.Mono.dll";
        if (common.Any(name => !names.Contains(name)) || !names.Contains(runtimeMarker))
            throw new InvalidDataException($"The archive is not the expected BepInEx {runtime} package.");
    }

    private static void ExtractArchive(ZipArchive archive, string destinationRoot, CancellationToken cancellationToken)
    {
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeArchivePath(entry.FullName);
            if (relativePath.Length == 0) continue;
            var destination = ResolveContainedPath(destinationRoot, relativePath);
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static string NormalizeArchivePath(string entryName)
    {
        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            throw new InvalidDataException($"Archive entry has an unsafe path: {entryName}");
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException($"Archive entry escapes the destination: {entryName}");
        return segments.Length == 0 ? string.Empty : Path.Combine(segments);
    }

    private static EdiSourceFiles EnumerateEdiSource(string sourceRoot)
    {
        var files = new List<string>();
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(sourceRoot);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                RejectReparsePoint(file);
                files.Add(Path.GetRelativePath(sourceRoot, file));
            }
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                RejectReparsePoint(child);
                if (directory.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(child).Equals("Gallery", StringComparison.OrdinalIgnoreCase))
                    continue;
                directories.Add(Path.GetRelativePath(sourceRoot, child));
                pending.Push(child);
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        directories.Sort(StringComparer.OrdinalIgnoreCase);
        return new(files, directories);
    }

    private static CopyResult CopyFilesTransactional(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyCollection<string> relativeFiles,
        IReadOnlyCollection<string> relativeDirectories,
        CancellationToken cancellationToken)
    {
        sourceRoot = Path.GetFullPath(sourceRoot);
        destinationRoot = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        RejectReparsePoint(destinationRoot);

        var rollbackRoot = CreateTemporaryDirectory("install-rollback");
        var changes = new List<FileChange>();
        var createdDirectories = new List<string>();
        var replacedFiles = 0;
        var preserveRollback = false;
        try
        {
            foreach (var relativeDirectory in relativeDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationDirectory = ResolveContainedPath(destinationRoot, relativeDirectory);
                EnsureNoReparsePoints(destinationRoot, destinationDirectory);
                CreateDirectoryTracked(destinationRoot, destinationDirectory, createdDirectories);
            }
            foreach (var relativePath in relativeFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = ResolveContainedPath(sourceRoot, relativePath);
                var destination = ResolveContainedPath(destinationRoot, relativePath);
                RejectReparsePoint(source);
                EnsureNoReparsePoints(destinationRoot, destination);
                if (Directory.Exists(destination))
                    throw new IOException($"Cannot install file because a directory exists at: {destination}");
                CreateDirectoryTracked(destinationRoot, Path.GetDirectoryName(destination)!, createdDirectories);

                var existed = File.Exists(destination);
                if (existed) RejectReparsePoint(destination);
                var backup = ResolveContainedPath(rollbackRoot, relativePath);
                if (existed)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(destination, backup, overwrite: false);
                    replacedFiles++;
                }
                changes.Add(new(destination, existed, backup));
                CopyFileAtomic(source, destination);
            }
            return new(replacedFiles);
        }
        catch (Exception installException)
        {
            try
            {
                RollBack(changes);
                RemoveCreatedDirectories(createdDirectories);
            }
            catch (Exception rollbackException)
            {
                preserveRollback = true;
                throw new AggregateException(
                    $"Installation failed and its rollback was incomplete. Recovery files were retained at {rollbackRoot}.",
                    installException, rollbackException);
            }
            throw;
        }
        finally
        {
            if (!preserveRollback) DeleteDirectoryBestEffort(rollbackRoot);
        }
    }

    private static void CopyFileAtomic(string source, string destination)
    {
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void RollBack(IEnumerable<FileChange> changes)
    {
        var errors = new List<Exception>();
        foreach (var change in changes.Reverse())
        {
            try
            {
                if (change.Existed)
                {
                    if (!File.Exists(change.BackupPath))
                        throw new IOException($"Rollback data is missing for {change.DestinationPath}.");
                    CopyFileAtomic(change.BackupPath, change.DestinationPath);
                }
                else if (File.Exists(change.DestinationPath))
                {
                    File.Delete(change.DestinationPath);
                }
            }
            catch (Exception exception)
            {
                errors.Add(new IOException($"Could not restore {change.DestinationPath}.", exception));
            }
        }
        if (errors.Count > 0) throw new AggregateException("One or more installed files could not be restored.", errors);
    }

    private static void CreateDirectoryTracked(string destinationRoot, string directory, List<string> createdDirectories)
    {
        var missing = new Stack<string>();
        var current = directory;
        while (!Directory.Exists(current))
        {
            if (File.Exists(current)) throw new IOException($"Cannot create a directory because a file exists at: {current}");
            if (!current.StartsWith(NormalizeDirectoryRoot(destinationRoot), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Directory escapes its destination root: {directory}");
            missing.Push(current);
            current = Path.GetDirectoryName(current) ?? throw new InvalidDataException($"Directory has no parent: {directory}");
        }
        RejectReparsePoint(current);
        while (missing.Count > 0)
        {
            var created = missing.Pop();
            Directory.CreateDirectory(created);
            createdDirectories.Add(created);
        }
    }

    private static void RemoveCreatedDirectories(IEnumerable<string> createdDirectories)
    {
        foreach (var directory in createdDirectories.OrderByDescending(path => path.Length))
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
    }

    private static void EnsureNoReparsePoints(string destinationRoot, string destination)
    {
        var root = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        RejectReparsePoint(root);
        var relative = Path.GetRelativePath(root, destination);
        var current = root;
        foreach (var segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current) || File.Exists(current)) RejectReparsePoint(current);
        }
    }

    private static bool IsInitializationComplete(
        UnityRuntimeKind runtime,
        string gameRoot,
        InitializationLogMonitor logMonitor)
    {
        if (!logMonitor.ContainsStartupComplete()) return false;
        return runtime != UnityRuntimeKind.Il2Cpp || HasRequiredInteropAssemblies(gameRoot);
    }

    private static bool HasRequiredInteropAssemblies(string gameRoot)
    {
        var interop = Path.Combine(gameRoot, "BepInEx", "interop");
        return File.Exists(Path.Combine(interop, "UnityEngine.CoreModule.dll")) &&
               File.Exists(Path.Combine(interop, "UnityEngine.AnimationModule.dll")) &&
               File.Exists(Path.Combine(interop, "UnityEngine.SceneManagementModule.dll"));
    }

    private static LogSnapshot CaptureLogSnapshot(string logPath, bool includeHash = false)
    {
        try
        {
            var info = new FileInfo(logPath);
            if (!info.Exists) return new(false, 0, DateTime.MinValue, string.Empty);
            var hash = string.Empty;
            if (includeHash)
            {
                using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                hash = ComputeSha256(stream);
            }
            info.Refresh();
            return new(true, info.Length, info.LastWriteTimeUtc, hash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(false, 0, DateTime.MinValue, string.Empty);
        }
    }

    private static string ReadLogTail(string logPath, int maximumBytes)
        => ReadLogSegment(logPath, 0, maximumBytes);

    private static string ReadLogSegment(string logPath, long minimumOffset, int maximumBytes)
    {
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Max(minimumOffset, stream.Length - maximumBytes);
            if (offset > 0) stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string ComputeFilePrefixSha256(string path, long length)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            var remaining = length;
            while (remaining > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) return string.Empty;
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool IsDiskLoggingDisabled(string configPath)
    {
        if (!File.Exists(configPath)) return false;
        try
        {
            var inDiskSection = false;
            foreach (var rawLine in File.ReadLines(configPath))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inDiskSection = line.Equals("[Logging.Disk]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inDiskSection || line.Length == 0 || line.StartsWith('#')) continue;
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var key = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim();
                if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase) &&
                    value.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;
                if (key.Equals("LogLevels", StringComparison.OrdinalIgnoreCase))
                {
                    var levels = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (!levels.Any(level => level.Equals("Message", StringComparison.OrdinalIgnoreCase) ||
                                             level.Equals("All", StringComparison.OrdinalIgnoreCase))) return true;
                }
            }
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> CloseGameAsync(Process process)
    {
        process.Refresh();
        if (process.HasExited) return false;
        try
        {
            if (process.CloseMainWindow() && await WaitForExitAsync(process, TimeSpan.FromSeconds(8)).ConfigureAwait(false))
                return false;
        }
        catch (InvalidOperationException)
        {
        }

        process.Refresh();
        if (process.HasExited) return false;
        process.Kill(entireProcessTree: true);
        if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(10)).ConfigureAwait(false))
            throw new InvalidOperationException("The game process did not exit after a forced process-tree stop.");
        return true;
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var wait = process.WaitForExitAsync();
        var completed = await Task.WhenAny(wait, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != wait) return process.HasExited;
        await wait.ConfigureAwait(false);
        return true;
    }

    private static void EnsureGameIsClosed(string executablePath)
        => EnsureProcessIsClosed(executablePath, "Close the selected game before installing runtime files.");

    private static void EnsureProcessIsClosed(string executablePath, string message)
    {
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath));
        try
        {
            var expectedPath = Path.GetFullPath(executablePath);
            if (processes.Any(process => TryGetProcessPath(process) is not { } processPath ||
                                         processPath.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(message);
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static Process? FindRunningProcess(string executablePath, int excludedProcessId)
    {
        var expectedPath = Path.GetFullPath(executablePath);
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath));
        Process? match = null;
        foreach (var candidate in processes)
        {
            if (match is null && candidate.Id != excludedProcessId &&
                TryGetProcessPath(candidate)?.Equals(expectedPath, StringComparison.OrdinalIgnoreCase) == true)
            {
                match = candidate;
            }
            else
            {
                candidate.Dispose();
            }
        }
        return match;
    }

    private static async Task<Process?> FindRunningProcessAsync(
        string executablePath,
        int excludedProcessId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var process = FindRunningProcess(executablePath, excludedProcessId);
            if (process is not null) return process;
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName is { Length: > 0 } path ? Path.GetFullPath(path) : null;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    private static void RejectNestedDirectories(string first, string second)
    {
        var firstRoot = NormalizeDirectoryRoot(first);
        var secondRoot = NormalizeDirectoryRoot(second);
        if (firstRoot.Equals(secondRoot, StringComparison.OrdinalIgnoreCase) ||
            firstRoot.StartsWith(secondRoot, StringComparison.OrdinalIgnoreCase) ||
            secondRoot.StartsWith(firstRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The EDI source and game destination folders cannot contain one another.");
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var rootPrefix = NormalizeDirectoryRoot(root);
        var fullPath = Path.GetFullPath(Path.Combine(rootPrefix, relativePath));
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path escapes its destination root: {relativePath}");
        return fullPath;
    }

    private static string NormalizeDirectoryRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reparse points are not supported during installation: {path}");
    }

    private static string ComputeSha256(Stream stream)
    {
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string CreateTemporaryDirectory(string category)
    {
        var parent = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio", category);
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class InitializationLogMonitor
    {
        private readonly string _directory;
        private readonly string _defaultLogPath;
        private readonly Dictionary<string, LogSnapshot> _initialLogs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _currentLaunchOffsets = new(StringComparer.OrdinalIgnoreCase);

        public InitializationLogMonitor(string directory)
        {
            _directory = directory;
            _defaultLogPath = Path.Combine(directory, "LogOutput.log");
            foreach (var path in EnumerateLogPaths())
                _initialLogs[path] = CaptureLogSnapshot(path, includeHash: true);
            if (!_initialLogs.ContainsKey(_defaultLogPath))
                _initialLogs[_defaultLogPath] = new(false, 0, DateTime.MinValue, string.Empty);
            ActiveLogPath = _defaultLogPath;
        }

        public string ActiveLogPath { get; private set; }

        public bool ContainsStartupComplete()
        {
            foreach (var path in EnumerateLogPaths().Concat(_initialLogs.Keys)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var initial = _initialLogs.TryGetValue(path, out var snapshot)
                    ? snapshot
                    : new LogSnapshot(false, 0, DateTime.MinValue, string.Empty);
                var current = CaptureLogSnapshot(path);
                if (!current.Exists || current.Length == initial.Length &&
                    current.LastWriteTimeUtc == initial.LastWriteTimeUtc) continue;
                if (!_currentLaunchOffsets.TryGetValue(path, out var offset))
                {
                    offset = initial.Exists && current.Length > initial.Length && initial.Sha256.Length > 0 &&
                             ComputeFilePrefixSha256(path, initial.Length)
                                 .Equals(initial.Sha256, StringComparison.OrdinalIgnoreCase)
                        ? initial.Length
                        : 0;
                    _currentLaunchOffsets[path] = offset;
                }
                if (!ReadLogSegment(path, offset, 128 * 1024)
                        .Contains("Chainloader startup complete", StringComparison.OrdinalIgnoreCase)) continue;
                ActiveLogPath = path;
                return true;
            }
            return false;
        }

        public string ReadLatestTail(int maximumBytes)
        {
            var latest = EnumerateLogPaths()
                .Select(path => (Path: path, Snapshot: CaptureLogSnapshot(path)))
                .Where(item => item.Snapshot.Exists)
                .OrderByDescending(item => item.Snapshot.LastWriteTimeUtc)
                .FirstOrDefault();
            var path = latest.Path ?? ActiveLogPath;
            return ReadLogTail(path, maximumBytes);
        }

        private IReadOnlyList<string> EnumerateLogPaths()
        {
            try
            {
                return Directory.Exists(_directory)
                    ? Directory.EnumerateFiles(_directory, "LogOutput*.log", SearchOption.TopDirectoryOnly).ToArray()
                    : [];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    private sealed record EdiSourceFiles(List<string> Files, List<string> Directories);
    private sealed record CopyResult(int ReplacedFiles);
    private sealed record FileChange(string DestinationPath, bool Existed, string BackupPath);
    private sealed record LogSnapshot(bool Exists, long Length, DateTime LastWriteTimeUtc, string Sha256);
}
