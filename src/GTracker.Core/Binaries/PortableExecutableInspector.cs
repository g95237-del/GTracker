using System.Reflection.PortableExecutable;

namespace GTracker.Core.Binaries;

public sealed record PortableExecutableSection(string Name, long FileOffset, long Size);

public sealed record PortableExecutableInfo(string Architecture, IReadOnlyList<PortableExecutableSection> Sections)
{
    public static PortableExecutableInfo Unknown { get; } = new("Unknown", []);
}

public static class PortableExecutableInspector
{
    public static PortableExecutableInfo Inspect(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            var sections = reader.PEHeaders.SectionHeaders
                .Select(section => new PortableExecutableSection(section.Name, section.PointerToRawData, section.SizeOfRawData))
                .ToArray();
            return new(reader.PEHeaders.CoffHeader.Machine.ToString(), sections);
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return PortableExecutableInfo.Unknown;
        }
    }
}
