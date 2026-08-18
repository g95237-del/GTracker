using GTracker.Core.Godot;

namespace GTracker.Core.Tests;

public sealed class GodotToolingTests
{
    [Theory]
    [InlineData(1, 3, 5, 2)]
    [InlineData(2, 4, 2, 1)]
    [InlineData(3, 4, 6, 0)]
    [InlineData(4, 4, 7, 1)]
    public void Inspect_DetectsSupportedExternalPack(int format, int major, int minor, int patch)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "Game.exe");
            File.Copy(Environment.ProcessPath!, executable);
            WritePack(Path.Combine(directory, "Game.pck"), (uint)format, (uint)major, (uint)minor, (uint)patch,
                [new("project.binary"), new("scripts/player.gd")]);

            var result = new GodotGameInspector().Inspect(executable);

            Assert.True(result.IsGodot);
            Assert.True(result.IsSupported);
            Assert.Equal(GodotPackLocation.External, result.PackLocation);
            Assert.Equal((uint)format, result.PackFormatVersion);
            Assert.Equal($"{major}.{minor}.{patch}", result.EngineVersion);
            Assert.Equal(GodotScriptRuntime.GdScript, result.ScriptRuntime);
            Assert.Equal(2u, result.FileCount);
            Assert.Contains(result.Findings, finding => finding.Contains("No PCK directory or file encryption"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_DetectsEmbeddedTrailerPack()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "EmbeddedGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            var packPath = Path.Combine(directory, "source.pck");
            WritePack(packPath, 2, 4, 3, 0, [new("project.binary"), new("main.gd")]);
            var pack = File.ReadAllBytes(packPath);
            using (var stream = new FileStream(executable, FileMode.Append, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(pack);
                writer.Write((ulong)pack.Length);
                writer.Write(0x43504447u);
            }

            var result = new GodotGameInspector().Inspect(executable);

            Assert.True(result.IsGodot);
            Assert.True(result.IsSupported);
            Assert.Equal(GodotPackLocation.EmbeddedTrailer, result.PackLocation);
            Assert.True(result.PackOffset > 0);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_ReportsDirectoryEncryptionWithoutParsingCiphertext()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "EncryptedGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            WritePack(Path.Combine(directory, "EncryptedGame.pck"), 3, 4, 6, 0, [], packFlags: 0x01);

            var result = new GodotGameInspector().Inspect(executable);

            Assert.True(result.IsGodot);
            Assert.True(result.IsEncrypted);
            Assert.True(result.DirectoryEncrypted);
            Assert.False(result.IsSupported);
            Assert.Contains(result.Findings, finding => finding.Contains("directory is encrypted"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_ReportsEncryptedFilesAndDotNetMarkers()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "DotNetGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            WritePack(Path.Combine(directory, "DotNetGame.pck"), 4, 4, 7, 0,
            [
                new("project.binary"),
                new(".godot/mono/publish/x86_64/.dotnet-publish-manifest"),
                new("assets/encrypted.res", 0x01)
            ]);

            var result = new GodotGameInspector().Inspect(executable);

            Assert.True(result.IsGodot);
            Assert.Equal(GodotScriptRuntime.DotNet, result.ScriptRuntime);
            Assert.Equal(1, result.EncryptedFileCount);
            Assert.False(result.IsSupported);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_DoesNotUseAnotherExecutablesPack()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "Launcher.exe");
            File.Copy(Environment.ProcessPath!, executable);
            WritePack(Path.Combine(directory, "ActualGame.pck"), 2, 4, 2, 0, [new("project.binary")]);

            var result = new GodotGameInspector().Inspect(executable);

            Assert.False(result.IsGodot);
            Assert.Equal(GodotPackLocation.Missing, result.PackLocation);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_RejectsInvalidEmbeddedSizeWithoutThrowing()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "BrokenGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            using (var stream = new FileStream(executable, FileMode.Append, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ulong.MaxValue);
                writer.Write(0x43504447u);
            }

            var result = new GodotGameInspector().Inspect(executable);

            Assert.False(result.IsGodot);
            Assert.Contains(result.Findings, finding => finding.Contains("declared pack size is outside"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_ReportsTruncatedMatchingPackWithoutThrowing()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "BrokenGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            File.WriteAllBytes(Path.Combine(directory, "BrokenGame.pck"), "GDPC"u8.ToArray());

            var result = new GodotGameInspector().Inspect(executable);

            Assert.False(result.IsGodot);
            Assert.Contains(result.Findings, finding => finding.Contains("candidate extent is truncated"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_FallsBackFromMalformedSidecarToEmbeddedPack()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "FallbackGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            File.WriteAllBytes(Path.Combine(directory, "FallbackGame.pck"), "GDPC"u8.ToArray());
            var packPath = Path.Combine(directory, "source.pck");
            WritePack(packPath, 2, 4, 3, 0, [new("project.binary"), new("main.gd")]);
            var pack = File.ReadAllBytes(packPath);
            using (var stream = new FileStream(executable, FileMode.Append, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(pack);
                writer.Write((ulong)pack.Length);
                writer.Write(0x43504447u);
            }

            var result = new GodotGameInspector().Inspect(executable);

            Assert.True(result.IsSupported);
            Assert.Equal(GodotPackLocation.EmbeddedTrailer, result.PackLocation);
            Assert.Contains(result.Findings, finding => finding.Contains("PCK validation failed"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_RejectsOutOfBoundsPayload()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "UnsafeGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            var packPath = Path.Combine(directory, "UnsafeGame.pck");
            WritePack(packPath, 2, 4, 3, 0, [new("project.binary")]);
            using (var stream = new FileStream(packPath, FileMode.Open, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                stream.Position = 100 + sizeof(uint) + "project.binary"u8.Length;
                writer.Write(ulong.MaxValue);
            }

            var result = new GodotGameInspector().Inspect(executable);

            Assert.False(result.IsGodot);
            Assert.Contains(result.Findings, finding => finding.Contains("exceeds the supported range"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_DoesNotTreatRemovalEntryAsProjectSettings()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "RemovalGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            WritePack(Path.Combine(directory, "RemovalGame.pck"), 2, 4, 3, 0,
                [new("project.binary", 0x02), new("main.gd")]);

            var result = new GodotGameInspector().Inspect(executable);

            Assert.False(result.IsGodot);
            Assert.Contains(result.Findings, finding => finding.Contains("does not contain project.binary"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_RejectsPayloadOverlappingMetadata()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "OverlapGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            var packPath = Path.Combine(directory, "OverlapGame.pck");
            WritePack(packPath, 1, 3, 5, 0, [new("project.binary")]);
            using (var stream = new FileStream(packPath, FileMode.Open, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                stream.Position = 88 + sizeof(uint) + "project.binary"u8.Length;
                writer.Write(0ul);
            }

            var result = new GodotGameInspector().Inspect(executable);

            Assert.False(result.IsGodot);
            Assert.Contains(result.Findings, finding => finding.Contains("overlaps pack metadata"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_PrefersValidatedEmbeddedPackOverEncryptedSidecar()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "PreferredGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            WritePack(Path.Combine(directory, "PreferredGame.pck"), 3, 4, 6, 0, [], packFlags: 0x01);
            var packPath = Path.Combine(directory, "source.pck");
            WritePack(packPath, 2, 4, 3, 0, [new("project.binary"), new("main.gd")]);
            var pack = File.ReadAllBytes(packPath);
            using (var stream = new FileStream(executable, FileMode.Append, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(pack);
                writer.Write((ulong)pack.Length);
                writer.Write(0x43504447u);
            }

            var result = new GodotGameInspector().Inspect(executable);

            Assert.True(result.IsSupported);
            Assert.Equal(GodotPackLocation.EmbeddedTrailer, result.PackLocation);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void WritePack(
        string path,
        uint format,
        uint major,
        uint minor,
        uint patch,
        IReadOnlyList<PackEntry> entries,
        uint packFlags = 0)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        var effectivePackFlags = format == 2 ? packFlags | 0x02u : packFlags;
        var directorySize = sizeof(uint) + entries.Sum(entry =>
            sizeof(uint) + System.Text.Encoding.UTF8.GetByteCount(entry.Path) + sizeof(ulong) * 2 + 16 +
            (format >= 2 ? sizeof(uint) : 0));
        var headerSize = format >= 3 ? 104 : format == 2 ? 96 : 84;
        var payloadOffset = headerSize + directorySize;
        writer.Write(0x43504447u);
        writer.Write(format);
        writer.Write(major);
        writer.Write(minor);
        writer.Write(patch);
        if (format == 1)
        {
            writer.Write(new byte[64]);
        }
        else
        {
            writer.Write(effectivePackFlags);
            writer.Write((ulong)payloadOffset);
            if (format >= 3) writer.Write(104ul);
            writer.Write(new byte[64]);
        }
        writer.Write((uint)entries.Count);
        if ((effectivePackFlags & 0x01) != 0)
        {
            writer.Write(new byte[32]);
            return;
        }
        foreach (var entry in entries)
        {
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(entry.Path);
            writer.Write((uint)pathBytes.Length);
            writer.Write(pathBytes);
            writer.Write(format == 1 ? (ulong)payloadOffset : 0ul);
            writer.Write(1ul);
            writer.Write(new byte[16]);
            if (format >= 2) writer.Write(entry.Flags);
        }
        writer.Write((byte)0);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed record PackEntry(string Path, uint Flags = 0);
}
