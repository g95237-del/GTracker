using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using GTracker.Core.Projects;

namespace GTracker.Core.Unity;

public enum BepInExFlavor
{
    Missing,
    Mono5,
    Mono6,
    Il2Cpp6
}

public sealed record UnityInspectionResult(
    string ExecutablePath,
    UnityRuntimeKind Runtime,
    string Architecture,
    string DataDirectory,
    IReadOnlyList<string> Findings)
{
    public bool IsUnity => Runtime != UnityRuntimeKind.Unknown;
    public string UnityVersion { get; init; } = string.Empty;
    public string RecommendedTargetFramework { get; init; } = string.Empty;
    public BepInExFlavor BepInEx { get; init; }
    public bool IsModularUnity { get; init; }
    public bool InteropReady { get; init; }
    public bool IsBuildReady { get; init; }
}

public sealed class UnityGameInspector
{
    public UnityInspectionResult Inspect(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(executablePath)) throw new FileNotFoundException("Game executable was not found.", executablePath);

        var directory = Path.GetDirectoryName(executablePath)!;
        var stem = Path.GetFileNameWithoutExtension(executablePath);
        var dataDirectory = Path.Combine(directory, $"{stem}_Data");
        var managedDirectory = Path.Combine(dataDirectory, "Managed");
        var managedAssembly = Path.Combine(managedDirectory, "Assembly-CSharp.dll");
        var gameAssembly = Path.Combine(directory, "GameAssembly.dll");
        var metadata = Path.Combine(dataDirectory, "il2cpp_data", "Metadata", "global-metadata.dat");
        var findings = new List<string>();

        var mono = File.Exists(managedAssembly);
        var il2Cpp = File.Exists(gameAssembly) && File.Exists(metadata);
        if (mono) findings.Add($"Managed game assembly: {managedAssembly}");
        if (File.Exists(gameAssembly)) findings.Add($"IL2CPP native assembly: {gameAssembly}");
        if (File.Exists(metadata)) findings.Add($"IL2CPP metadata: {metadata}");
        if (!Directory.Exists(dataDirectory)) findings.Add($"Expected Unity data directory was not found: {dataDirectory}");

        var runtime = il2Cpp ? UnityRuntimeKind.Il2Cpp : mono ? UnityRuntimeKind.Mono : UnityRuntimeKind.Unknown;
        var architecture = ReadArchitecture(executablePath);
        var unityVersion = ReadUnityVersion(directory);
        var targetFramework = runtime == UnityRuntimeKind.Il2Cpp ? "net6.0" : DetectMonoTargetFramework(managedDirectory, findings);
        var modularUnity = File.Exists(Path.Combine(managedDirectory, "UnityEngine.CoreModule.dll"));
        var unityEngineFacade = File.Exists(Path.Combine(managedDirectory, "UnityEngine.dll"));
        var animationModule = File.Exists(Path.Combine(managedDirectory, "UnityEngine.AnimationModule.dll"));
        var sceneModule = File.Exists(Path.Combine(managedDirectory, "UnityEngine.SceneManagementModule.dll"));
        var bepinEx = DetectBepInEx(directory);
        var interopDirectory = Path.Combine(directory, "BepInEx", "interop");
        var interopReady = runtime != UnityRuntimeKind.Il2Cpp ||
                           File.Exists(Path.Combine(interopDirectory, "UnityEngine.CoreModule.dll")) &&
                           File.Exists(Path.Combine(interopDirectory, "UnityEngine.AnimationModule.dll")) &&
                           File.Exists(Path.Combine(interopDirectory, "UnityEngine.SceneManagementModule.dll"));
        var engineReady = runtime switch
        {
            UnityRuntimeKind.Mono => (unityEngineFacade || modularUnity) &&
                                     (unityEngineFacade || animationModule) &&
                                     (unityEngineFacade || sceneModule),
            UnityRuntimeKind.Il2Cpp => interopReady,
            _ => false
        };
        var loaderReady = runtime switch
        {
            UnityRuntimeKind.Mono => bepinEx is BepInExFlavor.Mono5 or BepInExFlavor.Mono6,
            UnityRuntimeKind.Il2Cpp => bepinEx == BepInExFlavor.Il2Cpp6,
            _ => false
        };

        findings.Add($"Executable architecture: {architecture}");
        if (!string.IsNullOrWhiteSpace(unityVersion)) findings.Add($"Unity player version: {unityVersion}");
        findings.Add(runtime == UnityRuntimeKind.Unknown
            ? "Unity Mono/IL2CPP markers were not detected. The selected executable may be a launcher."
            : $"Detected Unity {runtime} runtime; recommended target framework: {targetFramework}.");
        findings.Add(bepinEx == BepInExFlavor.Missing
            ? "BepInEx was not detected beside the game. Install the matching build and run the game once before building."
            : $"Detected BepInEx flavor: {bepinEx}.");
        if (runtime == UnityRuntimeKind.Il2Cpp && !interopReady)
            findings.Add("IL2CPP interop assemblies are missing. Run the game once with BepInEx before building the plugin.");
        if (runtime != UnityRuntimeKind.Unknown && !engineReady)
            findings.Add("Required Unity scene/animation reference assemblies were not found.");
        if (runtime == UnityRuntimeKind.Mono && unityEngineFacade && modularUnity)
            findings.Add("Hybrid Unity reference layout detected; the build must reference UnityEngine.dll plus only the modules present on disk.");

        return new(executablePath, runtime, architecture, dataDirectory, findings)
        {
            UnityVersion = unityVersion,
            RecommendedTargetFramework = targetFramework,
            BepInEx = bepinEx,
            IsModularUnity = modularUnity,
            InteropReady = interopReady,
            IsBuildReady = engineReady && loaderReady && targetFramework != "net35"
        };
    }

    private static string DetectMonoTargetFramework(string managedDirectory, List<string> findings)
    {
        var netstandard = Path.Combine(managedDirectory, "netstandard.dll");
        if (File.Exists(netstandard))
        {
            var version = ReadAssemblyVersion(netstandard);
            if (version is { Major: >= 2, Minor: >= 1 }) return "netstandard2.1";
            return "netstandard2.0";
        }

        var mscorlib = Path.Combine(managedDirectory, "mscorlib.dll");
        var mscorlibVersion = ReadAssemblyVersion(mscorlib);
        if (mscorlibVersion is { Major: >= 4 }) return "net46";
        if (File.Exists(mscorlib))
            findings.Add("Legacy Unity Mono profile detected. The generated HTTP client requires a .NET 4.x-compatible profile.");
        return File.Exists(mscorlib) ? "net35" : "netstandard2.0";
    }

    private static BepInExFlavor DetectBepInEx(string gameRoot)
    {
        var core = Path.Combine(gameRoot, "BepInEx", "core");
        if (File.Exists(Path.Combine(core, "BepInEx.Unity.IL2CPP.dll"))) return BepInExFlavor.Il2Cpp6;
        if (File.Exists(Path.Combine(core, "BepInEx.Unity.Mono.dll"))) return BepInExFlavor.Mono6;
        if (File.Exists(Path.Combine(core, "BepInEx.dll"))) return BepInExFlavor.Mono5;
        return BepInExFlavor.Missing;
    }

    private static Version? ReadAssemblyVersion(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            if (!reader.HasMetadata) return null;
            return reader.GetMetadataReader().GetAssemblyDefinition().Version;
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadUnityVersion(string gameRoot)
    {
        var unityPlayer = Path.Combine(gameRoot, "UnityPlayer.dll");
        if (!File.Exists(unityPlayer)) return string.Empty;
        try
        {
            return FileVersionInfo.GetVersionInfo(unityPlayer).ProductVersion ?? string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static string ReadArchitecture(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.PEHeaders.CoffHeader.Machine.ToString();
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            return "Unknown";
        }
    }
}
