using System.IO.Compression;
using System.Security.Cryptography;
using GTracker.Core.Projects;
using GTracker.Core.Unity;

namespace GTracker.Core.Tests;

public sealed class UnityRuntimeProvisionerTests
{
    [Fact]
    public void InstallBepInEx_ExtractsMatchingPackageAndPreservesUnrelatedPlugins()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gameRoot = Path.Combine(directory, "game");
            Directory.CreateDirectory(gameRoot);
            var executable = Path.Combine(gameRoot, "FixtureGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            var existingPlugin = Path.Combine(gameRoot, "BepInEx", "plugins", "existing.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(existingPlugin)!);
            File.WriteAllText(existingPlugin, "keep");
            File.WriteAllText(Path.Combine(gameRoot, "winhttp.dll"), "old");
            var archivePath = Path.Combine(directory, "mono.zip");
            CreateBepInExArchive(archivePath, UnityRuntimeKind.Mono);
            var inspection = CreateInspection(executable, UnityRuntimeKind.Mono);

            var result = new UnityRuntimeProvisioner().InstallBepInEx(
                inspection, archivePath, ComputeSha256(archivePath));

            Assert.True(File.Exists(Path.Combine(gameRoot, "BepInEx", "core", "BepInEx.Unity.Mono.dll")));
            Assert.Equal("new-loader", File.ReadAllText(Path.Combine(gameRoot, "winhttp.dll")));
            Assert.Equal("keep", File.ReadAllText(existingPlugin));
            Assert.Equal(1, result.ReplacedFileCount);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void InstallBepInEx_RejectsWrongRuntimeAndArchiveTraversal()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gameRoot = Path.Combine(directory, "game");
            Directory.CreateDirectory(gameRoot);
            var executable = Path.Combine(gameRoot, "FixtureGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            var monoArchive = Path.Combine(directory, "mono.zip");
            CreateBepInExArchive(monoArchive, UnityRuntimeKind.Mono);
            var monoInspection = CreateInspection(executable, UnityRuntimeKind.Mono);
            Assert.Throws<InvalidDataException>(() => new UnityRuntimeProvisioner().InstallBepInEx(
                monoInspection, monoArchive, new string('0', 64)));

            var il2CppInspection = CreateInspection(executable, UnityRuntimeKind.Il2Cpp);
            Assert.Throws<InvalidDataException>(() => new UnityRuntimeProvisioner().InstallBepInEx(
                il2CppInspection, monoArchive, ComputeSha256(monoArchive)));

            var incompatibleInspection = CreateInspection(executable, UnityRuntimeKind.Mono) with
            {
                BepInEx = BepInExFlavor.Il2Cpp6
            };
            Assert.Throws<InvalidOperationException>(() => new UnityRuntimeProvisioner().InstallBepInEx(
                incompatibleInspection, monoArchive, ComputeSha256(monoArchive)));

            var traversalArchive = Path.Combine(directory, "traversal.zip");
            CreateBepInExArchive(traversalArchive, UnityRuntimeKind.Mono, includeTraversal: true);
            Assert.Throws<InvalidDataException>(() => new UnityRuntimeProvisioner().InstallBepInEx(
                monoInspection, traversalArchive, ComputeSha256(traversalArchive)));
            Assert.False(File.Exists(Path.Combine(directory, "escape.txt")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void InstallEdi_MergesFilesButPreservesGalleryContents()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(directory, "fresh-edi");
            Directory.CreateDirectory(Path.Combine(source, "assets"));
            Directory.CreateDirectory(Path.Combine(source, "Gallery"));
            File.WriteAllText(Path.Combine(source, "Edi.exe"), "edi");
            File.WriteAllText(Path.Combine(source, "EdiConfig.json"), "new-config");
            File.WriteAllText(Path.Combine(source, "assets", "runtime.dat"), "asset");
            File.WriteAllText(Path.Combine(source, "Gallery", "source.funscript"), "do-not-copy");

            var gameRoot = Path.Combine(directory, "game");
            Directory.CreateDirectory(Path.Combine(gameRoot, "Gallery"));
            var executable = Path.Combine(gameRoot, "FixtureGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            File.WriteAllText(Path.Combine(gameRoot, "EdiConfig.json"), "old-config");
            File.WriteAllText(Path.Combine(gameRoot, "Gallery", "existing.funscript"), "keep");

            var result = new UnityRuntimeProvisioner().InstallEdi(
                CreateInspection(executable, UnityRuntimeKind.Mono), source);

            Assert.Equal("new-config", File.ReadAllText(Path.Combine(gameRoot, "EdiConfig.json")));
            Assert.Equal("asset", File.ReadAllText(Path.Combine(gameRoot, "assets", "runtime.dat")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(gameRoot, "Gallery", "existing.funscript")));
            Assert.False(File.Exists(Path.Combine(gameRoot, "Gallery", "source.funscript")));
            Assert.Equal(3, result.InstalledFileCount);
            Assert.Equal(1, result.ReplacedFileCount);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void InstallEdi_RollsBackEarlierFilesWhenALaterCollisionFails()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(directory, "fresh-edi");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "a.txt"), "new-a");
            File.WriteAllText(Path.Combine(source, "Edi.exe"), "edi");
            File.WriteAllText(Path.Combine(source, "z.txt"), "cannot-replace-directory");

            var gameRoot = Path.Combine(directory, "game");
            Directory.CreateDirectory(gameRoot);
            var executable = Path.Combine(gameRoot, "FixtureGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            File.WriteAllText(Path.Combine(gameRoot, "a.txt"), "old-a");
            Directory.CreateDirectory(Path.Combine(gameRoot, "z.txt"));

            Assert.Throws<IOException>(() => new UnityRuntimeProvisioner().InstallEdi(
                CreateInspection(executable, UnityRuntimeKind.Mono), source));

            Assert.Equal("old-a", File.ReadAllText(Path.Combine(gameRoot, "a.txt")));
            Assert.False(File.Exists(Path.Combine(gameRoot, "Edi.exe")));
            Assert.True(Directory.Exists(Path.Combine(gameRoot, "z.txt")));
            Assert.False(Directory.Exists(Path.Combine(gameRoot, "Gallery")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task InitializeGame_PreCanceledTokenStopsBeforeProcessLaunch()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "NotARealGame.exe");
            File.WriteAllBytes(executable, []);
            var inspection = CreateInspection(executable, UnityRuntimeKind.Mono) with
            {
                BepInEx = BepInExFlavor.Mono6
            };
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new UnityRuntimeProvisioner().InitializeGameAsync(
                    inspection, cancellationToken: cancellation.Token));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task InitializeGame_RejectsDisabledDiskLoggingBeforeLaunch()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "NotARealGame.exe");
            File.WriteAllBytes(executable, []);
            var configDirectory = Path.Combine(directory, "BepInEx", "config");
            Directory.CreateDirectory(configDirectory);
            await File.WriteAllTextAsync(Path.Combine(configDirectory, "BepInEx.cfg"), """
                [Logging.Disk]
                Enabled = false
                LogLevels = Fatal, Error, Warning, Message, Info
                """);
            var inspection = CreateInspection(executable, UnityRuntimeKind.Mono) with
            {
                BepInEx = BepInExFlavor.Mono6
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new UnityRuntimeProvisioner().InitializeGameAsync(inspection));
            Assert.Contains("disk logging", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static UnityInspectionResult CreateInspection(string executable, UnityRuntimeKind runtime) =>
        new(executable, runtime, "Amd64", Path.Combine(Path.GetDirectoryName(executable)!, "FixtureGame_Data"), []);

    private static void CreateBepInExArchive(string path, UnityRuntimeKind runtime, bool includeTraversal = false)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        WriteEntry(archive, "winhttp.dll", "new-loader");
        WriteEntry(archive, "doorstop_config.ini", "config");
        WriteEntry(archive, "BepInEx/core/BepInEx.Core.dll", "core");
        WriteEntry(archive, runtime == UnityRuntimeKind.Il2Cpp
            ? "BepInEx/core/BepInEx.Unity.IL2CPP.dll"
            : "BepInEx/core/BepInEx.Unity.Mono.dll", "runtime");
        if (includeTraversal) WriteEntry(archive, "../escape.txt", "escape");
    }

    private static void WriteEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
