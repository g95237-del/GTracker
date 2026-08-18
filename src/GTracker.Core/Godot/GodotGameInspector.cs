using System.Buffers.Binary;
using System.Text;
using GTracker.Core.Binaries;

namespace GTracker.Core.Godot;

public enum GodotPackLocation
{
    Missing,
    External,
    EmbeddedSection,
    EmbeddedTrailer
}

public enum GodotScriptRuntime
{
    Unknown,
    GdScript,
    DotNet,
    Mixed
}

public sealed record GodotInspectionResult(
    string ExecutablePath,
    string Architecture,
    GodotPackLocation PackLocation,
    string PackPath,
    long PackOffset,
    uint PackFormatVersion,
    uint EngineMajorVersion,
    uint EngineMinorVersion,
    uint EnginePatchVersion,
    uint FileCount,
    int EncryptedFileCount,
    bool DirectoryEncrypted,
    bool SparseBundle,
    bool HasProjectSettings,
    GodotScriptRuntime ScriptRuntime,
    IReadOnlyList<string> Findings)
{
    public bool IsEncrypted => DirectoryEncrypted || EncryptedFileCount > 0;
    public bool IsGodot => PackLocation != GodotPackLocation.Missing && (HasProjectSettings || DirectoryEncrypted);
    public bool IsSupported => IsGodot && !Architecture.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                               EngineMajorVersion is 3 or 4 && PackFormatVersion is >= 1 and <= 4 &&
                               !IsEncrypted && !SparseBundle;
    public string EngineVersion => IsGodot
        ? $"{EngineMajorVersion}.{EngineMinorVersion}.{EnginePatchVersion}"
        : string.Empty;
}

public sealed class GodotGameInspector
{
    private const uint PackMagic = 0x43504447;
    private const uint DirectoryEncryptedFlag = 0x01;
    private const uint SparseBundleFlag = 0x04;
    private const uint FileEncryptedFlag = 0x01;
    private const int MaximumPathBytes = 64 * 1024;
    private const long MaximumDirectoryPathBytes = 64L * 1024 * 1024;
    private const uint MaximumFileCount = 500_000;

    public GodotInspectionResult Inspect(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Game executable was not found.", executablePath);

        var findings = new List<string>();
        var pe = PortableExecutableInspector.Inspect(executablePath);
        findings.Add($"Executable architecture: {pe.Architecture}");
        PackCandidate? candidate = null;
        PackMetadata pack = default;
        PackCandidate? encryptedCandidate = null;
        PackMetadata encryptedPack = default;
        foreach (var possibleCandidate in FindPackCandidates(executablePath, pe, findings))
        {
            if (!TryReadPack(possibleCandidate, findings, out var possiblePack)) continue;
            if (possiblePack.DirectoryEncrypted)
            {
                encryptedCandidate ??= possibleCandidate;
                if (encryptedCandidate == possibleCandidate) encryptedPack = possiblePack;
                continue;
            }
            if (!possiblePack.HasProjectSettings)
            {
                findings.Add($"Pack candidate does not contain project.binary or project.godot: {possibleCandidate.DisplayPath}");
                continue;
            }
            candidate = possibleCandidate;
            pack = possiblePack;
            break;
        }
        if (candidate is null && encryptedCandidate is not null)
        {
            candidate = encryptedCandidate;
            pack = encryptedPack;
        }
        if (candidate is null)
        {
            findings.Add("A matching Godot PCK was not found. Select the exported game executable rather than a launcher or editor.");
            return Empty(executablePath, pe.Architecture, findings);
        }

        findings.Add($"Godot PCK: {candidate.DisplayPath} ({candidate.Location}, format {pack.FormatVersion}).");
        findings.Add($"Godot exporter version recorded by PCK: {pack.EngineMajor}.{pack.EngineMinor}.{pack.EnginePatch}.");
        findings.Add($"PCK directory entries: {pack.FileCount}.");
        if (pack.DirectoryEncrypted)
            findings.Add("The PCK directory is encrypted. GTracker will not request or attempt to recover an encryption key.");
        else if (pack.EncryptedFileCount > 0)
            findings.Add($"The PCK contains {pack.EncryptedFileCount} encrypted file(s). Encrypted exports are not eligible for automatic integration.");
        else
            findings.Add("No PCK directory or file encryption flags were detected.");
        if (pack.SparseBundle)
            findings.Add("Sparse-bundle PCK layout detected. External bundle references are not supported for automatic integration.");
        if (pe.Architecture.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            findings.Add("The selected target is not a recognized Windows PE executable.");
        if (!pack.HasProjectSettings && !pack.DirectoryEncrypted)
            findings.Add("The pack does not contain project.binary or project.godot, so it is not confirmed as the selected executable's main project pack.");
        findings.Add(pack.ScriptRuntime switch
        {
            GodotScriptRuntime.GdScript => "GDScript resources detected.",
            GodotScriptRuntime.DotNet => "Godot .NET/C# export markers detected.",
            GodotScriptRuntime.Mixed => "Both GDScript and Godot .NET/C# export markers detected.",
            _ => "No definitive GDScript or .NET runtime marker was found in the readable PCK directory."
        });

        var result = new GodotInspectionResult(executablePath, pe.Architecture, candidate.Location,
            candidate.Path, candidate.Offset, pack.FormatVersion, pack.EngineMajor, pack.EngineMinor, pack.EnginePatch,
            pack.FileCount, pack.EncryptedFileCount, pack.DirectoryEncrypted, pack.SparseBundle,
            pack.HasProjectSettings, pack.ScriptRuntime, findings);
        findings.Add(result.IsSupported
            ? "This unencrypted Godot export is ready for integration workflow development."
            : "This export is not currently eligible for automatic Godot integration.");
        return result;
    }

    private static GodotInspectionResult Empty(string executablePath, string architecture, IReadOnlyList<string> findings) =>
        new(executablePath, architecture, GodotPackLocation.Missing, string.Empty, 0, 0, 0, 0, 0,
            0, 0, false, false, false, GodotScriptRuntime.Unknown, findings);

    private static IReadOnlyList<PackCandidate> FindPackCandidates(
        string executablePath,
        PortableExecutableInfo pe,
        List<string> findings)
    {
        var candidates = new List<PackCandidate>();
        var directory = Path.GetDirectoryName(executablePath)!;
        var stemCandidate = Path.Combine(directory, Path.GetFileNameWithoutExtension(executablePath) + ".pck");
        var fullNameCandidate = executablePath + ".pck";
        foreach (var path in new[] { stemCandidate, fullNameCandidate }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            if (HasMagic(path, 0))
                candidates.Add(new(path, 0, new FileInfo(path).Length, GodotPackLocation.External, path));
            else
                findings.Add($"Matching pack is malformed or does not begin with GDPC: {path}");
        }

        var section = pe.Sections.FirstOrDefault(item => item.Name.Equals("pck", StringComparison.OrdinalIgnoreCase));
        if (section is not null && section.FileOffset >= 0 && section.Size > 0)
        {
            for (var alignment = 0; alignment <= 8; alignment++)
            {
                var offset = section.FileOffset + alignment;
                if (offset >= 0 && offset < section.FileOffset + section.Size && HasMagic(executablePath, offset))
                {
                    candidates.Add(new(executablePath, offset, section.FileOffset + section.Size - offset,
                        GodotPackLocation.EmbeddedSection, $"{executablePath} [pck section]"));
                    break;
                }
            }
        }

        using var stream = File.OpenRead(executablePath);
        if (stream.Length < 12) return candidates;
        stream.Position = stream.Length - 12;
        Span<byte> footer = stackalloc byte[12];
        stream.ReadExactly(footer);
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[8..]) != PackMagic) return candidates;
        var size = BinaryPrimitives.ReadUInt64LittleEndian(footer);
        if (size > (ulong)(stream.Length - 12))
        {
            findings.Add("An embedded PCK footer was found, but its declared pack size is outside the executable.");
            return candidates;
        }
        var start = stream.Length - 12 - (long)size;
        if (HasMagic(executablePath, start) && candidates.All(item => item.Offset != start))
            candidates.Add(new(executablePath, start, (long)size, GodotPackLocation.EmbeddedTrailer,
                $"{executablePath} [embedded trailer]"));
        return candidates;
    }

    private static bool TryReadPack(PackCandidate candidate, List<string> findings, out PackMetadata metadata)
    {
        metadata = default;
        try
        {
            using var stream = File.Open(candidate.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var packEnd = checked(candidate.Offset + candidate.Length);
            if (candidate.Offset < 0 || candidate.Length < 24 || packEnd > stream.Length)
            {
                findings.Add($"PCK validation failed: candidate extent is truncated or outside the containing file: {candidate.DisplayPath}");
                return false;
            }
            stream.Position = candidate.Offset;
            var magic = ReadUInt32(stream, packEnd);
            if (magic != PackMagic) return false;
            var format = ReadUInt32(stream, packEnd);
            var major = ReadUInt32(stream, packEnd);
            var minor = ReadUInt32(stream, packEnd);
            var patch = ReadUInt32(stream, packEnd);
            if (format is < 1 or > 4)
            {
                findings.Add($"PCK format {format} is not recognized by this GTracker build.");
                return false;
            }

            uint packFlags = 0;
            ulong storedFileBase = 0;
            long directoryOffset;
            long headerEnd;
            if (format == 1)
            {
                Skip(stream, 64, packEnd);
                directoryOffset = stream.Position;
                headerEnd = directoryOffset;
            }
            else
            {
                packFlags = ReadUInt32(stream, packEnd);
                storedFileBase = ReadUInt64(stream, packEnd);
                if (format >= 3)
                {
                    var relativeDirectory = ReadUInt64(stream, packEnd);
                    directoryOffset = CheckedPosition(candidate.Offset, relativeDirectory, packEnd);
                    Skip(stream, 64, packEnd);
                    headerEnd = stream.Position;
                }
                else
                {
                    Skip(stream, 64, packEnd);
                    directoryOffset = stream.Position;
                    headerEnd = directoryOffset;
                }
            }

            stream.Position = directoryOffset;
            var fileCount = ReadUInt32(stream, packEnd);
            if (fileCount > MaximumFileCount) throw new InvalidDataException("PCK file count exceeds the inspection limit.");
            var directoryEncrypted = (packFlags & DirectoryEncryptedFlag) != 0;
            if (directoryEncrypted)
            {
                metadata = new(format, major, minor, patch, fileCount, 0, true,
                    (packFlags & SparseBundleFlag) != 0, false, GodotScriptRuntime.Unknown);
                return true;
            }

            var encryptedFiles = 0;
            long totalPathBytes = 0;
            var hasProjectSettings = false;
            var hasGdScript = false;
            var hasDotNet = false;
            var payloads = new List<PayloadReference>();
            for (var index = 0u; index < fileCount; index++)
            {
                var pathLength = ReadUInt32(stream, packEnd);
                if (pathLength > MaximumPathBytes) throw new InvalidDataException("PCK path length exceeds the inspection limit.");
                totalPathBytes = checked(totalPathBytes + pathLength);
                if (totalPathBytes > MaximumDirectoryPathBytes)
                    throw new InvalidDataException("PCK directory path data exceeds the inspection limit.");
                var pathBytes = ReadBytes(stream, checked((int)pathLength), packEnd);
                var path = Encoding.UTF8.GetString(pathBytes).TrimEnd('\0').Replace('\\', '/');
                var storedOffset = ReadUInt64(stream, packEnd);
                var storedSize = ReadUInt64(stream, packEnd);
                Skip(stream, 16, packEnd);
                uint fileFlags = 0;
                if (format >= 2) fileFlags = ReadUInt32(stream, packEnd);
                var removal = (fileFlags & 0x02) != 0;
                if ((fileFlags & FileEncryptedFlag) != 0) encryptedFiles++;
                if (!removal && (packFlags & SparseBundleFlag) == 0 && (fileFlags & FileEncryptedFlag) == 0)
                {
                    var absoluteBase = format switch
                    {
                        1 => 0L,
                        2 when (packFlags & 0x02) != 0 => checked(candidate.Offset + ToInt64(storedFileBase)),
                        2 => ToInt64(storedFileBase),
                        _ => checked(candidate.Offset + ToInt64(storedFileBase))
                    };
                    var absoluteOffset = checked(absoluteBase + ToInt64(storedOffset));
                    var size = ToInt64(storedSize);
                    if (absoluteOffset < candidate.Offset || absoluteOffset > packEnd || size < 0 || size > packEnd - absoluteOffset)
                        throw new InvalidDataException($"PCK payload for '{path}' is outside the pack.");
                    payloads.Add(new(path, absoluteOffset, size));
                }
                var normalized = path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ? path[6..] : path;
                if (!removal)
                {
                    hasProjectSettings |= normalized.Equals("project.binary", StringComparison.OrdinalIgnoreCase) ||
                                          normalized.Equals("project.godot", StringComparison.OrdinalIgnoreCase);
                    hasGdScript |= normalized.EndsWith(".gd", StringComparison.OrdinalIgnoreCase) ||
                                   normalized.EndsWith(".gdc", StringComparison.OrdinalIgnoreCase);
                    hasDotNet |= normalized.Contains(".godot/mono/", StringComparison.OrdinalIgnoreCase) ||
                                 normalized.Contains(".mono/assemblies/", StringComparison.OrdinalIgnoreCase) ||
                                 normalized.EndsWith(".dotnet-publish-manifest", StringComparison.OrdinalIgnoreCase) ||
                                 normalized.EndsWith("GodotSharp.dll", StringComparison.OrdinalIgnoreCase);
                }
            }

            var directoryEnd = stream.Position;
            foreach (var payload in payloads)
            {
                if (payload.Size == 0) continue;
                if (RangesOverlap(payload.Offset, payload.Size, candidate.Offset, headerEnd - candidate.Offset) ||
                    RangesOverlap(payload.Offset, payload.Size, directoryOffset, directoryEnd - directoryOffset))
                    throw new InvalidDataException($"PCK payload for '{payload.Path}' overlaps pack metadata.");
            }

            var runtime = hasGdScript && hasDotNet ? GodotScriptRuntime.Mixed :
                hasDotNet ? GodotScriptRuntime.DotNet : hasGdScript ? GodotScriptRuntime.GdScript : GodotScriptRuntime.Unknown;
            metadata = new(format, major, minor, patch, fileCount, encryptedFiles, false,
                (packFlags & SparseBundleFlag) != 0, hasProjectSettings, runtime);
            return true;
        }
        catch (Exception exception) when (exception is EndOfStreamException or InvalidDataException or IOException or
                                          UnauthorizedAccessException or OverflowException or ArgumentOutOfRangeException)
        {
            findings.Add($"PCK validation failed: {exception.Message}");
            return false;
        }
    }

    private static bool HasMagic(string path, long offset)
    {
        if (offset < 0) return false;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (offset > stream.Length - 4) return false;
            stream.Position = offset;
            Span<byte> bytes = stackalloc byte[4];
            stream.ReadExactly(bytes);
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes) == PackMagic;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static uint ReadUInt32(Stream stream, long end)
    {
        Span<byte> bytes = stackalloc byte[4];
        ReadExactly(stream, bytes, end);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(Stream stream, long end)
    {
        Span<byte> bytes = stackalloc byte[8];
        ReadExactly(stream, bytes, end);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static byte[] ReadBytes(Stream stream, int count, long end)
    {
        if (count < 0 || stream.Position > end - count) throw new EndOfStreamException("PCK directory entry is truncated.");
        var bytes = new byte[count];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static void ReadExactly(Stream stream, Span<byte> bytes, long end)
    {
        if (stream.Position > end - bytes.Length) throw new EndOfStreamException("PCK structure is truncated.");
        stream.ReadExactly(bytes);
    }

    private static void Skip(Stream stream, int count, long end)
    {
        if (count < 0 || stream.Position > end - count) throw new EndOfStreamException("PCK structure is truncated.");
        stream.Position += count;
    }

    private static long CheckedPosition(long start, ulong relative, long end)
    {
        if (relative > long.MaxValue) throw new InvalidDataException("PCK offset exceeds the supported range.");
        var position = checked(start + (long)relative);
        if (position < start || position > end - 4) throw new InvalidDataException("PCK directory offset is outside the pack.");
        return position;
    }

    private static long ToInt64(ulong value) => value <= long.MaxValue
        ? (long)value
        : throw new InvalidDataException("PCK value exceeds the supported range.");

    private sealed record PackCandidate(string Path, long Offset, long Length, GodotPackLocation Location, string DisplayPath);

    private sealed record PayloadReference(string Path, long Offset, long Size);

    private static bool RangesOverlap(long firstOffset, long firstSize, long secondOffset, long secondSize) =>
        firstSize > 0 && secondSize > 0 && firstOffset < secondOffset + secondSize && secondOffset < firstOffset + firstSize;

    private readonly record struct PackMetadata(
        uint FormatVersion,
        uint EngineMajor,
        uint EngineMinor,
        uint EnginePatch,
        uint FileCount,
        int EncryptedFileCount,
        bool DirectoryEncrypted,
        bool SparseBundle,
        bool HasProjectSettings,
        GodotScriptRuntime ScriptRuntime);
}
