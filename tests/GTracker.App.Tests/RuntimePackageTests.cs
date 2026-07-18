using System.IO.Compression;
using System.Security.Cryptography;

namespace GTracker.App.Tests;

public sealed class RuntimePackageTests
{
    [Theory]
    [InlineData("BepInEx-Unity.Mono-win-x64-6.0.0-be.785+6abdba4.zip",
        "DB430F14D6661EB38BA96FCC13C07A163E87E553710821D87E5129F915A1B26B",
        "BepInEx/core/BepInEx.Unity.Mono.dll")]
    [InlineData("BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785+6abdba4.zip",
        "2A7CBF74D26ABE4765C3E662DB1721B923BAC39849EBFEF2CA5DC7DE7E2D9B7F",
        "BepInEx/core/BepInEx.Unity.IL2CPP.dll")]
    public void PackagedBepInExArchive_IsPresentAndMatchesRecordedHash(
        string fileName,
        string expectedHash,
        string runtimeMarker)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "RuntimePackages", fileName);
        Assert.True(File.Exists(path), $"Packaged runtime archive was not copied to test output: {path}");
        using (var stream = File.OpenRead(path))
        {
            Assert.Equal(expectedHash, Convert.ToHexString(SHA256.HashData(stream)));
        }

        using var archive = ZipFile.OpenRead(path);
        Assert.Contains(archive.Entries, entry => entry.FullName.Equals(runtimeMarker, StringComparison.OrdinalIgnoreCase));
    }
}
