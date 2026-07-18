using GTracker.Core.Projects;
using GTracker.Core.Unity;
using System.Text.Json;

namespace GTracker.Core.Tests;

public sealed class UnityToolingTests
{
    [Fact]
    public void Inspect_DetectsMonoMarkers()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "SampleGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            var managed = Path.Combine(directory, "SampleGame_Data", "Managed");
            Directory.CreateDirectory(managed);
            File.WriteAllBytes(Path.Combine(managed, "Assembly-CSharp.dll"), []);

            var result = new UnityGameInspector().Inspect(executable);

            Assert.Equal(UnityRuntimeKind.Mono, result.Runtime);
            Assert.True(result.IsUnity);
            Assert.Equal("netstandard2.0", result.RecommendedTargetFramework);
            Assert.Contains(result.Findings, finding => finding.Contains("Managed game assembly"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_DoesNotUseAnotherExecutablesDataDirectory()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "Launcher.exe");
            File.Copy(Environment.ProcessPath!, executable);
            var unrelatedManaged = Path.Combine(directory, "ActualGame_Data", "Managed");
            Directory.CreateDirectory(unrelatedManaged);
            File.WriteAllBytes(Path.Combine(unrelatedManaged, "Assembly-CSharp.dll"), []);

            var result = new UnityGameInspector().Inspect(executable);

            Assert.Equal(UnityRuntimeKind.Unknown, result.Runtime);
            Assert.EndsWith("Launcher_Data", result.DataDirectory);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_DetectsBepInExAndModularUnityReadiness()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "SampleGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            var managed = Path.Combine(directory, "SampleGame_Data", "Managed");
            Directory.CreateDirectory(managed);
            foreach (var name in new[] { "Assembly-CSharp.dll", "UnityEngine.CoreModule.dll", "UnityEngine.AnimationModule.dll", "UnityEngine.SceneManagementModule.dll" })
                File.WriteAllBytes(Path.Combine(managed, name), []);
            var core = Path.Combine(directory, "BepInEx", "core");
            Directory.CreateDirectory(core);
            File.WriteAllBytes(Path.Combine(core, "BepInEx.dll"), []);

            var result = new UnityGameInspector().Inspect(executable);

            Assert.Equal(BepInExFlavor.Mono5, result.BepInEx);
            Assert.True(result.IsModularUnity);
            Assert.True(result.IsBuildReady);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_AcceptsHybridUnityFacadeWhenSceneModuleIsAbsent()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "HybridGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            var managed = Path.Combine(directory, "HybridGame_Data", "Managed");
            Directory.CreateDirectory(managed);
            foreach (var name in new[] { "Assembly-CSharp.dll", "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.AnimationModule.dll" })
                File.WriteAllBytes(Path.Combine(managed, name), []);
            var core = Path.Combine(directory, "BepInEx", "core");
            Directory.CreateDirectory(core);
            File.WriteAllBytes(Path.Combine(core, "BepInEx.Unity.Mono.dll"), []);

            var result = new UnityGameInspector().Inspect(executable);

            Assert.Equal(BepInExFlavor.Mono6, result.BepInEx);
            Assert.True(result.IsBuildReady);
            Assert.Contains(result.Findings, finding => finding.Contains("Hybrid Unity reference layout"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_PrefersCompleteIl2CppMarkers()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "SampleGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            File.WriteAllBytes(Path.Combine(directory, "GameAssembly.dll"), []);
            var metadata = Path.Combine(directory, "SampleGame_Data", "il2cpp_data", "Metadata");
            Directory.CreateDirectory(metadata);
            File.WriteAllBytes(Path.Combine(metadata, "global-metadata.dat"), []);

            var result = new UnityGameInspector().Inspect(executable);

            Assert.Equal(UnityRuntimeKind.Il2Cpp, result.Runtime);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Scaffold_WritesRuntimeSpecificProjectAndActionConstants()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gameRoot = Path.Combine(directory, "game");
            var output = Path.Combine(directory, "mod");
            Directory.CreateDirectory(gameRoot);
            var executable = Path.Combine(gameRoot, "Game.exe");
            File.WriteAllBytes(executable, []);
            var inspection = new UnityInspectionResult(executable, UnityRuntimeKind.Il2Cpp, "Amd64",
                Path.Combine(gameRoot, "Game_Data"), [])
            {
                RecommendedTargetFramework = "net6.0",
                BepInEx = BepInExFlavor.Il2Cpp6
            };
            var project = new StudioProject
            {
                Name = "Sample Game",
                Actions = [new AuthoredAction { Name = "enemy-hit", FileName = "enemy-hit" }]
            };

            await new UnityModScaffolder().GenerateAsync(project, inspection, output);

            var projectFile = await File.ReadAllTextAsync(Path.Combine(output, "IntegrationMod.csproj"));
            var plugin = await File.ReadAllTextAsync(Path.Combine(output, "Plugin.cs"));
            var observer = await File.ReadAllTextAsync(Path.Combine(output, "RuntimeObserver.cs"));
            var ediClient = await File.ReadAllTextAsync(Path.Combine(output, "EdiClient.cs"));
            var actions = await File.ReadAllTextAsync(Path.Combine(output, "ActionNames.cs"));
            Assert.Contains("net6.0", projectFile);
            Assert.Contains("BepInEx.Unity.IL2CPP", projectFile);
            Assert.Contains("<ExternallyResolved>true</ExternallyResolved>", projectFile);
            Assert.Contains("<Private>false</Private>", projectFile);
            Assert.DoesNotContain("Assembly-CSharp", projectFile);
            Assert.DoesNotContain("mscorlib", projectFile, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("netstandard.dll", projectFile, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UnityEngine.InputLegacyModule", projectFile);
            Assert.Contains("BasePlugin", plugin);
            Assert.Contains("GetAsyncKeyState", observer);
            Assert.Contains("IsHotkeyDown(0x31, 0x61)", observer);
            Assert.Contains("IsHotkeyDown(0x34, 0x64)", observer);
            Assert.DoesNotContain("Input.GetKey", observer);
            Assert.Contains("_edi?.Pause()", observer);
            Assert.Contains("_edi?.Resume()", observer);
            Assert.Contains("_edi?.SetIntensity(40)", observer);
            Assert.Contains("_edi?.SetIntensity(100)", observer);
            Assert.Contains("PostAsync($\"/Intensity/{clamped}\")", ediClient);
            Assert.Contains("public const string EnemyHit = \"enemy-hit\";", actions);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Scaffold_MonoUsesOnlyExplicitCompileTimeEngineReferences()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gameRoot = Path.Combine(directory, "game");
            var output = Path.Combine(directory, "mod");
            Directory.CreateDirectory(gameRoot);
            var executable = Path.Combine(gameRoot, "Game.exe");
            File.WriteAllBytes(executable, []);
            var managed = Path.Combine(gameRoot, "Game_Data", "Managed");
            Directory.CreateDirectory(managed);
            foreach (var name in new[] { "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.AnimationModule.dll" })
                File.WriteAllBytes(Path.Combine(managed, name), []);
            var inspection = new UnityInspectionResult(executable, UnityRuntimeKind.Mono, "Amd64",
                Path.Combine(gameRoot, "Game_Data"), [])
            {
                RecommendedTargetFramework = "netstandard2.0",
                BepInEx = BepInExFlavor.Mono5,
                IsModularUnity = true
            };

            var project = new StudioProject
            {
                Actions = [new AuthoredAction { Name = "attack", FileName = "attack" }],
                Game = new GameTarget
                {
                    TriggerMappings =
                    [
                        new UnityTriggerMapping
                        {
                            Kind = UnityTriggerKind.Scene,
                            Candidate = "Boss Arena",
                            ActionName = "attack"
                        }
                    ]
                }
            };
            await new UnityModScaffolder().GenerateAsync(project, inspection, output, UnityModPresetKind.AnimationNames);

            var projectFile = await File.ReadAllTextAsync(Path.Combine(output, "IntegrationMod.csproj"));
            var preset = await File.ReadAllTextAsync(Path.Combine(output, "GamePreset.cs"));
            Assert.Contains("UnityEngine.AnimationModule", projectFile);
            Assert.DoesNotContain("UnityEngine.InputLegacyModule", projectFile);
            Assert.Contains("<Reference Include=\"UnityEngine\"", projectFile);
            Assert.DoesNotContain("UnityEngine.SceneManagementModule", projectFile);
            Assert.DoesNotContain("Assembly-CSharp", projectFile);
            Assert.Contains("MatchAnimations = true", preset);
            Assert.Contains("MatchScenes = false", preset);
            Assert.Contains("[\"bossarena\"] = ActionNames.Attack", preset);

            var pluginPath = Path.Combine(output, "Plugin.cs");
            var observerPath = Path.Combine(output, "RuntimeObserver.cs");
            var ediClientPath = Path.Combine(output, "EdiClient.cs");
            await File.WriteAllTextAsync(pluginPath, "// hand-written patch");
            await File.WriteAllTextAsync(observerPath, "// stale generated observer");
            await File.WriteAllTextAsync(ediClientPath, "// stale generated client");
            project.Game.TriggerMappings[0].Candidate = "Final Arena";
            await new UnityModScaffolder().UpdatePresetAsync(project, output, UnityModPresetKind.SceneNames);

            Assert.Equal("// hand-written patch", await File.ReadAllTextAsync(pluginPath));
            var updatedPreset = await File.ReadAllTextAsync(Path.Combine(output, "GamePreset.cs"));
            Assert.Contains("MatchScenes = true", updatedPreset);
            Assert.Contains("[\"finalarena\"] = ActionNames.Attack", updatedPreset);

            await new UnityModScaffolder().RepairProjectFileAsync(inspection, output);
            Assert.Equal("// hand-written patch", await File.ReadAllTextAsync(pluginPath));
            var repairedObserver = await File.ReadAllTextAsync(observerPath);
            Assert.Contains("ANIMATOR_LOOP", repairedObserver);
            Assert.Contains("ANIMATOR_STALLED", repairedObserver);
            Assert.Contains("animator.isActiveAndEnabled", repairedObserver);
            Assert.Contains("StopAllMappedPlayback", repairedObserver);
            Assert.Contains("cycleDurationSeconds", repairedObserver);
            Assert.Contains("Play(action, seekMilliseconds)", repairedObserver);
            Assert.Contains("GetAsyncKeyState", repairedObserver);
            Assert.DoesNotContain("Input.GetKey", repairedObserver);
            Assert.DoesNotContain("Math.Clamp", repairedObserver);
            var repairedClient = await File.ReadAllTextAsync(ediClientPath);
            Assert.Contains("/Intensity/{clamped}", repairedClient);
            var repairedProject = await File.ReadAllTextAsync(Path.Combine(output, "IntegrationMod.csproj"));
            Assert.Contains("<Reference Include=\"UnityEngine\"", repairedProject);
            Assert.DoesNotContain("UnityEngine.SceneManagementModule", repairedProject);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task GeneratedMonoSources_CompileAndInstallThroughDeploymentPipeline()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gameRoot = Path.Combine(directory, "game");
            var output = Path.Combine(directory, "mod");
            Directory.CreateDirectory(gameRoot);
            Directory.CreateDirectory(Path.Combine(gameRoot, "BepInEx", "core"));
            var executable = Path.Combine(gameRoot, "FixtureGame.exe");
            File.WriteAllBytes(executable, []);
            var inspection = new UnityInspectionResult(executable, UnityRuntimeKind.Mono, "Amd64",
                Path.Combine(gameRoot, "FixtureGame_Data"), [])
            {
                RecommendedTargetFramework = "net8.0",
                BepInEx = BepInExFlavor.Mono5,
                IsModularUnity = true
            };
            var project = new StudioProject
            {
                Name = "Compile Fixture",
                Actions = [new AuthoredAction { Name = "attack", FileName = "attack" }]
            };
            await new UnityModScaffolder().GenerateAsync(project, inspection, output);
            await File.WriteAllTextAsync(Path.Combine(output, "IntegrationMod.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>IntegrationMod</AssemblyName>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(output, "UnityStubs.cs"), MonoUnityStubs);

            var build = await new UnityModDeployer().BuildAsync(output);

            Assert.True(build.Success, build.Output);
            var install = new UnityModDeployer().Install(build);
            Assert.True(File.Exists(install.PluginPath));
            Assert.True(File.Exists(install.OwnershipManifestPath));
            Assert.StartsWith(Path.Combine(gameRoot, "BepInEx", "plugins"), install.PluginPath);
            File.Delete(install.OwnershipManifestPath);
            Assert.Throws<IOException>(() => new UnityModDeployer().Install(build));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task GeneratedIl2CppSources_CompileThroughDeploymentPipeline()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var gameRoot = Path.Combine(directory, "game");
            var output = Path.Combine(directory, "mod");
            Directory.CreateDirectory(gameRoot);
            var executable = Path.Combine(gameRoot, "FixtureGame.exe");
            File.WriteAllBytes(executable, []);
            var inspection = new UnityInspectionResult(executable, UnityRuntimeKind.Il2Cpp, "Amd64",
                Path.Combine(gameRoot, "FixtureGame_Data"), [])
            {
                RecommendedTargetFramework = "net8.0",
                BepInEx = BepInExFlavor.Il2Cpp6
            };
            await new UnityModScaffolder().GenerateAsync(new StudioProject { Name = "IL2CPP Fixture" }, inspection, output);
            await File.WriteAllTextAsync(Path.Combine(output, "IntegrationMod.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>IntegrationMod</AssemblyName>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(output, "UnityStubs.cs"), MonoUnityStubs);

            var build = await new UnityModDeployer().BuildAsync(output);

            Assert.True(build.Success, build.Output);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Scaffold_RefusesToOverwriteGeneratedOrHandEditedFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var output = Path.Combine(directory, "mod");
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "Plugin.cs"), "hand written patch");
            var inspection = new UnityInspectionResult(Path.Combine(directory, "Game.exe"), UnityRuntimeKind.Mono,
                "Amd64", Path.Combine(directory, "Game_Data"), []);

            await Assert.ThrowsAsync<IOException>(() =>
                new UnityModScaffolder().GenerateAsync(new StudioProject(), inspection, output));
            Assert.Equal("hand written patch", await File.ReadAllTextAsync(Path.Combine(output, "Plugin.cs")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Scaffold_RejectsActionIdentifierCollisions()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var inspection = new UnityInspectionResult(Path.Combine(directory, "Game.exe"), UnityRuntimeKind.Mono,
                "Amd64", Path.Combine(directory, "Game_Data"), []);
            var project = new StudioProject
            {
                Actions =
                [
                    new AuthoredAction { Name = "enemy-hit", FileName = "one" },
                    new AuthoredAction { Name = "enemy hit", FileName = "two" }
                ]
            };

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                new UnityModScaffolder().GenerateAsync(project, inspection, Path.Combine(directory, "mod")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private const string MonoUnityStubs = """
        namespace BepInEx
        {
            [AttributeUsage(AttributeTargets.Class)]
            public sealed class BepInPluginAttribute : Attribute
            {
                public BepInPluginAttribute(string guid, string name, string version) { }
            }
            [AttributeUsage(AttributeTargets.Class)]
            public sealed class BepInProcessAttribute : Attribute
            {
                public BepInProcessAttribute(string process) { }
            }
            public static class Paths
            {
                public static string ConfigPath => Path.GetTempPath();
            }
            public sealed class LogStub
            {
                public void LogInfo(object value) { }
                public void LogWarning(object value) { }
            }
            public class BaseUnityPlugin : UnityEngine.MonoBehaviour
            {
                public LogStub Logger { get; } = new();
            }
        }

        namespace BepInEx.Unity.IL2CPP
        {
            public class BasePlugin
            {
                public BepInEx.LogStub Log { get; } = new();
                public virtual void Load() { }
                public virtual bool Unload() => true;
                protected T AddComponent<T>() => default!;
            }
        }

        namespace HarmonyLib
        {
            public sealed class Harmony
            {
                public Harmony(string id) { }
                public void PatchAll() { }
                public void UnpatchSelf() { }
            }
        }

        namespace UnityEngine
        {
            public class Object
            {
                public string name { get; set; } = string.Empty;
                public static void Destroy(Object value) { }
            }
            public class GameObject : Object
            {
                public SceneManagement.Scene scene { get; set; }
                public bool activeInHierarchy { get; set; } = true;
                public T AddComponent<T>() => default!;
            }
            public class Component : Object
            {
                private static int _nextId;
                public GameObject gameObject { get; } = new();
                public Transform transform { get; } = new();
                public int GetInstanceID() => ++_nextId;
            }
            public class MonoBehaviour : Component
            {
                public MonoBehaviour() { }
                public MonoBehaviour(IntPtr pointer) { }
                protected static void Destroy(Object value) => Object.Destroy(value);
            }
            public class Transform : Component
            {
                public Transform? parent { get; set; }
            }
            public class Animator : Component
            {
                public int layerCount => 1;
                public float speed { get; set; } = 1;
                public bool isActiveAndEnabled { get; set; } = true;
                public AnimatorStateInfo GetCurrentAnimatorStateInfo(int layer) => default;
                public AnimatorClipInfo[] GetCurrentAnimatorClipInfo(int layer) => [];
            }
            public struct AnimatorStateInfo
            {
                public int fullPathHash { get; set; }
                public float normalizedTime { get; set; }
                public float length { get; set; }
                public bool loop { get; set; }
                public float speed { get; set; }
                public float speedMultiplier { get; set; }
            }
            public struct AnimatorClipInfo
            {
                public AnimationClip clip { get; set; }
                public float weight { get; set; }
            }
            public class AnimationClip : Object
            {
                public float length { get; set; }
                public bool isLooping { get; set; }
            }
            public static class Resources
            {
                public static T[] FindObjectsOfTypeAll<T>() => [];
            }
            public static class Time
            {
                public static float unscaledTime => 0;
            }
            public static class Application
            {
                public static bool isFocused => true;
            }
        }

        namespace UnityEngine.SceneManagement
        {
            public enum LoadSceneMode { Single, Additive }
            public struct Scene
            {
                public string name { get; set; }
                public bool IsValid() => true;
            }
            public static class SceneManager
            {
                public static event Action<Scene, LoadSceneMode>? sceneLoaded;
                public static event Action<Scene, Scene>? activeSceneChanged;
                public static Scene GetActiveScene() => default;
            }
        }
        """;
}
