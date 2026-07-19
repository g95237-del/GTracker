using System.Security;
using System.Text;
using System.Text.Json;
using GTracker.Core.Projects;

namespace GTracker.Core.Unity;

public sealed record UnityScaffoldResult(
    string ProjectDirectory,
    string ProjectFile,
    string TargetFramework,
    string PluginGuid,
    string TelemetryFile);

public sealed class UnityScaffoldManifest
{
    public string Project { get; set; } = string.Empty;
    public string Runtime { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string BepInExFlavor { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
    public string AssemblyName { get; set; } = "IntegrationMod";
    public string PluginGuid { get; set; } = string.Empty;
    public string GameExecutable { get; set; } = string.Empty;
    public string GameRoot { get; set; } = string.Empty;
    public string EdiBaseUrl { get; set; } = string.Empty;
    public string Preset { get; set; } = string.Empty;
    public string TelemetryFile { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public string[] Actions { get; set; } = [];
}

public sealed class UnityModScaffolder
{
    public const string ManifestFileName = "scaffold.json";
    public const string AssemblyName = "IntegrationMod";

    public async Task<UnityScaffoldResult> GenerateAsync(
        StudioProject project,
        UnityInspectionResult inspection,
        string outputDirectory,
        UnityModPresetKind preset = UnityModPresetKind.Discovery,
        string ediBaseUrl = "http://127.0.0.1:5000/Edi",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (inspection.Runtime == UnityRuntimeKind.Unknown)
            throw new InvalidOperationException("A Mono or IL2CPP Unity runtime must be detected before generating a mod project.");
        if (inspection.Runtime == UnityRuntimeKind.Mono && inspection.RecommendedTargetFramework == "net35")
            throw new NotSupportedException("Legacy .NET 3.5 Unity games require a separate HTTP client template and are not supported yet.");
        if (!Uri.TryCreate(ediBaseUrl, UriKind.Absolute, out var parsedEdiUrl) ||
            parsedEdiUrl.Scheme is not ("http" or "https"))
            throw new ArgumentException("EDI base URL must be an absolute HTTP or HTTPS URL.", nameof(ediBaseUrl));

        var generatedFiles = new[]
        {
            "IntegrationMod.csproj", "Plugin.cs", "RuntimeObserver.cs", "GamePreset.cs", "EdiClient.cs",
            "ActionNames.cs", "README.md", ManifestFileName
        };
        var existingFiles = generatedFiles.Where(file => File.Exists(Path.Combine(outputDirectory, file))).ToArray();
        if (existingFiles.Length > 0)
        {
            throw new IOException(
                "Refusing to overwrite an existing scaffold because it may contain hand-written patches: " +
                string.Join(", ", existingFiles));
        }

        ValidateProjectActions(project);

        var gameRoot = Path.GetDirectoryName(inspection.ExecutablePath)!;
        var pluginName = ToIdentifier(project.Name) + "EdiIntegration";
        var pluginGuid = $"community.edi.{project.Id:N}";
        var targetFramework = string.IsNullOrWhiteSpace(inspection.RecommendedTargetFramework)
            ? inspection.Runtime == UnityRuntimeKind.Mono ? "netstandard2.0" : "net6.0"
            : inspection.RecommendedTargetFramework;
        var telemetryFile = Path.Combine(gameRoot, "BepInEx", "config", pluginGuid + ".telemetry.tsv");
        var hasPlayMaker = HasRuntimeAssembly(inspection, "PlayMaker.dll");
        ediBaseUrl = parsedEdiUrl.ToString().TrimEnd('/');

        var files = new Dictionary<string, string>
        {
            ["IntegrationMod.csproj"] = CreateProjectFile(inspection, targetFramework),
            ["Plugin.cs"] = inspection.Runtime == UnityRuntimeKind.Mono
                ? CreateMonoPlugin(pluginName, pluginGuid, project, inspection, ediBaseUrl)
                : CreateIl2CppPlugin(pluginName, pluginGuid, project, inspection, ediBaseUrl),
            ["RuntimeObserver.cs"] = CreateRuntimeObserver(pluginGuid, inspection.Runtime, hasPlayMaker),
            ["GamePreset.cs"] = CreateGamePreset(project, pluginGuid, preset),
            ["EdiClient.cs"] = CreateEdiClient(),
            ["ActionNames.cs"] = CreateActionNames(project),
            ["README.md"] = CreateReadme(project, inspection, gameRoot, targetFramework, preset, telemetryFile)
        };
        var manifest = new UnityScaffoldManifest
        {
            Project = project.Name,
            Runtime = inspection.Runtime.ToString(),
            Architecture = inspection.Architecture,
            BepInExFlavor = inspection.BepInEx.ToString(),
            TargetFramework = targetFramework,
            AssemblyName = AssemblyName,
            PluginGuid = pluginGuid,
            GameExecutable = inspection.ExecutablePath,
            GameRoot = gameRoot,
            EdiBaseUrl = ediBaseUrl,
            Preset = preset.ToString(),
            TelemetryFile = telemetryFile,
            GeneratedAt = DateTimeOffset.UtcNow,
            Actions = project.Actions.Select(action => action.Name).ToArray()
        };
        files[ManifestFileName] = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });

        Directory.CreateDirectory(outputDirectory);
        var createdFiles = new List<string>();
        try
        {
            foreach (var pair in files)
            {
                var path = Path.Combine(outputDirectory, pair.Key);
                await File.WriteAllTextAsync(path, pair.Value, Encoding.UTF8, cancellationToken);
                createdFiles.Add(path);
            }
        }
        catch
        {
            foreach (var path in createdFiles)
            {
                try { File.Delete(path); } catch (IOException) { }
            }
            throw;
        }

        return new(outputDirectory, Path.Combine(outputDirectory, "IntegrationMod.csproj"), targetFramework, pluginGuid, telemetryFile);
    }

    public async Task UpdatePresetAsync(
        StudioProject project,
        string projectDirectory,
        UnityModPresetKind preset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ValidateProjectActions(project);
        var manifest = await UnityModDeployer.LoadManifestAsync(projectDirectory, cancellationToken);
        var expectedPluginGuid = $"community.edi.{project.Id:N}";
        if (!manifest.PluginGuid.Equals(expectedPluginGuid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The generated mod belongs to a different studio project.");

        await ReplaceFileAsync(Path.Combine(projectDirectory, "GamePreset.cs"),
            CreateGamePreset(project, manifest.PluginGuid, preset), cancellationToken);
        await ReplaceFileAsync(Path.Combine(projectDirectory, "ActionNames.cs"),
            CreateActionNames(project), cancellationToken);
        manifest.Project = project.Name;
        manifest.Preset = preset.ToString();
        manifest.Actions = project.Actions.Select(action => action.Name).ToArray();
        manifest.GeneratedAt = DateTimeOffset.UtcNow;
        await ReplaceFileAsync(Path.Combine(projectDirectory, ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    public async Task RepairProjectFileAsync(
        UnityInspectionResult inspection,
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        var manifest = await UnityModDeployer.LoadManifestAsync(projectDirectory, cancellationToken);
        if (!manifest.Runtime.Equals(inspection.Runtime.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The generated mod runtime no longer matches the inspected game runtime. Generate a new project.");
        if (!string.IsNullOrWhiteSpace(manifest.BepInExFlavor) &&
            !manifest.BepInExFlavor.Equals(inspection.BepInEx.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The BepInEx flavor changed since generation. Generate a new project so Plugin.cs uses the correct loader API.");

        var targetFramework = string.IsNullOrWhiteSpace(inspection.RecommendedTargetFramework)
            ? manifest.TargetFramework
            : inspection.RecommendedTargetFramework;
        await ReplaceFileAsync(Path.Combine(projectDirectory, "IntegrationMod.csproj"),
            CreateProjectFile(inspection, targetFramework), cancellationToken);
        await ReplaceFileAsync(Path.Combine(projectDirectory, "RuntimeObserver.cs"),
            CreateRuntimeObserver(manifest.PluginGuid, inspection.Runtime,
                HasRuntimeAssembly(inspection, "PlayMaker.dll")), cancellationToken);
        await ReplaceFileAsync(Path.Combine(projectDirectory, "EdiClient.cs"),
            CreateEdiClient(), cancellationToken);
        manifest.Architecture = inspection.Architecture;
        manifest.BepInExFlavor = inspection.BepInEx.ToString();
        manifest.TargetFramework = targetFramework;
        manifest.GeneratedAt = DateTimeOffset.UtcNow;
        await ReplaceFileAsync(Path.Combine(projectDirectory, ManifestFileName),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private static async Task ReplaceFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, content, Encoding.UTF8, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ValidateProjectActions(StudioProject project)
    {
        var identifierCollisions = project.Actions
            .GroupBy(action => ToIdentifier(action.Name), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (identifierCollisions.Length > 0)
            throw new InvalidDataException("Scene names generate duplicate C# identifiers: " + string.Join(", ", identifierCollisions));
        var matchNameCollisions = project.Actions
            .GroupBy(action => NormalizeMatchName(action.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (matchNameCollisions.Length > 0)
            throw new InvalidDataException("Scene names generate ambiguous Unity preset match names: " + string.Join(", ", matchNameCollisions));
    }

    private static string CreateProjectFile(UnityInspectionResult inspection, string targetFramework)
    {
        var gameRoot = SecurityElement.Escape(Path.GetDirectoryName(inspection.ExecutablePath)!)!;
        var dataDirectory = SecurityElement.Escape(inspection.DataDirectory)!;
        var references = new StringBuilder();
        if (inspection.Runtime == UnityRuntimeKind.Mono)
        {
            if (inspection.BepInEx == BepInExFlavor.Mono6)
            {
                AppendReference(references, "BepInEx.Core", @"$(GameRoot)\BepInEx\core\BepInEx.Core.dll");
                AppendReference(references, "BepInEx.Unity.Mono", @"$(GameRoot)\BepInEx\core\BepInEx.Unity.Mono.dll");
            }
            else
            {
                AppendReference(references, "BepInEx", @"$(GameRoot)\BepInEx\core\BepInEx.dll");
            }
            AppendReference(references, "0Harmony", @"$(GameRoot)\BepInEx\core\0Harmony.dll");
            var managedDirectory = Path.Combine(inspection.DataDirectory, "Managed");
            AppendReferenceIfExists(references, "UnityEngine", @"$(DataRoot)\Managed\UnityEngine.dll",
                Path.Combine(managedDirectory, "UnityEngine.dll"));
            AppendReferenceIfExists(references, "UnityEngine.CoreModule", @"$(DataRoot)\Managed\UnityEngine.CoreModule.dll",
                Path.Combine(managedDirectory, "UnityEngine.CoreModule.dll"));
            AppendReferenceIfExists(references, "UnityEngine.AnimationModule", @"$(DataRoot)\Managed\UnityEngine.AnimationModule.dll",
                Path.Combine(managedDirectory, "UnityEngine.AnimationModule.dll"));
            AppendReferenceIfExists(references, "UnityEngine.SceneManagementModule", @"$(DataRoot)\Managed\UnityEngine.SceneManagementModule.dll",
                Path.Combine(managedDirectory, "UnityEngine.SceneManagementModule.dll"));
            AppendReferenceIfExists(references, "PlayMaker", @"$(DataRoot)\Managed\PlayMaker.dll",
                Path.Combine(managedDirectory, "PlayMaker.dll"));
        }
        else
        {
            AppendReference(references, "BepInEx.Core", @"$(GameRoot)\BepInEx\core\BepInEx.Core.dll");
            AppendReference(references, "BepInEx.Unity.IL2CPP", @"$(GameRoot)\BepInEx\core\BepInEx.Unity.IL2CPP.dll");
            AppendReference(references, "Il2CppInterop.Runtime", @"$(GameRoot)\BepInEx\core\Il2CppInterop.Runtime.dll");
            AppendReference(references, "0Harmony", @"$(GameRoot)\BepInEx\core\0Harmony.dll");
            AppendReference(references, "UnityEngine.CoreModule", @"$(GameRoot)\BepInEx\interop\UnityEngine.CoreModule.dll");
            AppendReference(references, "UnityEngine.AnimationModule", @"$(GameRoot)\BepInEx\interop\UnityEngine.AnimationModule.dll");
            AppendReference(references, "UnityEngine.SceneManagementModule", @"$(GameRoot)\BepInEx\interop\UnityEngine.SceneManagementModule.dll");
            AppendReferenceIfExists(references, "PlayMaker", @"$(GameRoot)\BepInEx\interop\PlayMaker.dll",
                Path.Combine(Path.GetDirectoryName(inspection.ExecutablePath)!, "BepInEx", "interop", "PlayMaker.dll"));
        }

        return $$"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>{{targetFramework}}</TargetFramework>
            <AssemblyName>{{AssemblyName}}</AssemblyName>
            <LangVersion>latest</LangVersion>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <GameRoot>{{gameRoot}}</GameRoot>
            <DataRoot>{{dataDirectory}}</DataRoot>
          </PropertyGroup>
          <ItemGroup>
        {{references.ToString().TrimEnd()}}
          </ItemGroup>
        </Project>
        """;
    }

    private static void AppendReference(StringBuilder builder, string include, string hintPath)
    {
        builder.Append("    <Reference Include=\"").Append(include).AppendLine("\">")
            .Append("      <HintPath>").Append(hintPath).AppendLine("</HintPath>")
            .AppendLine("      <Private>false</Private>")
            .AppendLine("      <ExternallyResolved>true</ExternallyResolved>")
            .AppendLine("    </Reference>");
    }

    private static void AppendReferenceIfExists(StringBuilder builder, string include, string hintPath, string physicalPath)
    {
        if (File.Exists(physicalPath)) AppendReference(builder, include, hintPath);
    }

    private static bool HasRuntimeAssembly(UnityInspectionResult inspection, string fileName)
    {
        var path = inspection.Runtime == UnityRuntimeKind.Mono
            ? Path.Combine(inspection.DataDirectory, "Managed", fileName)
            : Path.Combine(Path.GetDirectoryName(inspection.ExecutablePath)!, "BepInEx", "interop", fileName);
        return File.Exists(path);
    }

    private static string CreateMonoPlugin(
        string pluginName,
        string pluginGuid,
        StudioProject project,
        UnityInspectionResult inspection,
        string ediBaseUrl)
    {
        var mono6Using = inspection.BepInEx == BepInExFlavor.Mono6 ? "using BepInEx.Unity.Mono;\n" : string.Empty;
        return $$"""
        using BepInEx;
        {{mono6Using}}using HarmonyLib;
        using UnityEngine;

        [BepInPlugin("{{pluginGuid}}", {{CSharpLiteral(project.Name + " EDI Integration")}}, "0.2.0")]
        [BepInProcess({{CSharpLiteral(Path.GetFileName(inspection.ExecutablePath))}})]
        public sealed class {{pluginName}} : BaseUnityPlugin
        {
            internal static EdiClient Edi { get; private set; } = null!;
            private Harmony? _harmony;
            private RuntimeObserver? _observer;

            private void Awake()
            {
                Edi = new EdiClient({{CSharpLiteral(ediBaseUrl)}}, message => Logger.LogWarning(message));
                RuntimeObserver.Configure(Edi, message => Logger.LogInfo(message));
                _harmony = new Harmony("{{pluginGuid}}");
                try { _harmony.PatchAll(); }
                catch (Exception exception) { Logger.LogWarning($"Optional runtime patches failed: {exception}"); }
                _observer = gameObject.AddComponent<RuntimeObserver>();
                Logger.LogInfo("EDI integration loaded; runtime scene, Animator, and optional PlayMaker discovery is active.");
            }

            private void OnDestroy()
            {
                if (_observer != null) Destroy(_observer);
                _harmony?.UnpatchSelf();
                Edi.Dispose();
            }
        }

        // Add game-specific Harmony patches only after discovery identifies stable semantic methods.
        """;
    }

    private static string CreateIl2CppPlugin(
        string pluginName,
        string pluginGuid,
        StudioProject project,
        UnityInspectionResult inspection,
        string ediBaseUrl) => $$"""
        using BepInEx;
        using BepInEx.Unity.IL2CPP;
        using HarmonyLib;
        using UnityEngine;

        [BepInPlugin("{{pluginGuid}}", {{CSharpLiteral(project.Name + " EDI Integration")}}, "0.2.0")]
        [BepInProcess({{CSharpLiteral(Path.GetFileName(inspection.ExecutablePath))}})]
        public sealed class {{pluginName}} : BasePlugin
        {
            internal static EdiClient Edi { get; private set; } = null!;
            private Harmony? _harmony;
            private RuntimeObserver? _observer;

            public override void Load()
            {
                Edi = new EdiClient({{CSharpLiteral(ediBaseUrl)}}, message => Log.LogWarning(message));
                RuntimeObserver.Configure(Edi, message => Log.LogInfo(message));
                _harmony = new Harmony("{{pluginGuid}}");
                try { _harmony.PatchAll(); }
                catch (Exception exception) { Log.LogWarning($"Optional runtime patches failed: {exception}"); }
                _observer = AddComponent<RuntimeObserver>();
                Log.LogInfo("EDI IL2CPP integration loaded; runtime scene, Animator, and optional PlayMaker discovery is active.");
            }

            public override bool Unload()
            {
                if (_observer != null) UnityEngine.Object.Destroy(_observer);
                _harmony?.UnpatchSelf();
                Edi.Dispose();
                return true;
            }
        }

        // Keep IL2CPP object access on Unity's main thread. Queue only plain values to EdiClient.
        """;

    private static string CreateRuntimeObserver(string pluginGuid, UnityRuntimeKind runtime, bool hasPlayMaker)
    {
        var il2CppConstructor = runtime == UnityRuntimeKind.Il2Cpp
            ? "    public RuntimeObserver(IntPtr pointer) : base(pointer) { }\n"
            : string.Empty;
        var playMakerFields = hasPlayMaker
            ? "    private static RuntimeObserver? _instance;\n" +
              "    private readonly Dictionary<int, PlayMakerFSM> _playMakerFsms = new();\n" +
              "    private readonly Dictionary<int, FsmSnapshot> _playMakerStates = new();"
            : string.Empty;
        var playMakerAwakeStatement = hasPlayMaker ? "        _instance = this;\n" : string.Empty;
        var playMakerDestroyStatement = hasPlayMaker
            ? "        if (ReferenceEquals(_instance, this)) _instance = null;\n"
            : string.Empty;
        var playMakerScanStatement = hasPlayMaker
            ? "        RunObserverStep(\"playmaker-scan\", () => playMakerCount = ScanPlayMakerFsms());\n"
            : string.Empty;
        var playMakerResetStatements = hasPlayMaker
            ? "        _playMakerFsms.Clear();\n        _playMakerStates.Clear();\n"
            : string.Empty;
        var playMakerPlaybackResetStatement = hasPlayMaker
            ? "        PreparePlayMakerResume(_activeAnimatorActions);\n"
            : string.Empty;
        var playMakerMethods = hasPlayMaker ? CreatePlayMakerObserverMethods() : string.Empty;
        var playMakerPatch = hasPlayMaker ? CreatePlayMakerPatch() : string.Empty;
        return $$"""
        using System.Collections.Generic;
        using System.Globalization;
        using System.IO;
        using System.Linq;
        using System.Runtime.InteropServices;
        using BepInEx;
        using HarmonyLib;
        using UnityEngine;
        using UnityEngine.SceneManagement;

        internal sealed class RuntimeObserver : MonoBehaviour
        {
            private static EdiClient? _edi;
            private static Action<string>? _log;
            private readonly Dictionary<int, Animator> _animators = new();
        {{playMakerFields}}
            private readonly Dictionary<string, StateSnapshot> _states = new();
            private readonly Dictionary<string, string> _activeAnimatorActions = new();
            private readonly Dictionary<string, float> _observerErrorNextAt = new();
            private StreamWriter? _telemetry;
            private int _lastTickFrame = -1;
            private float _nextAnimatorPollAt;
            private float _nextScanAt;
            private float _nextTelemetryFlushAt;
            private string _activeSceneAction = string.Empty;
            private string _currentRuntimeActionKey = string.Empty;
            private string _lastScanSummary = string.Empty;
            private bool _applicationPaused;
            private bool _playbackSuspended;
            private bool _hotkeysUnavailable;
            private bool _hasFocus = true;
            private bool _key1Down;
            private bool _key2Down;
            private bool _key3Down;
            private bool _key4Down;

            [DllImport("user32.dll")]
            private static extern short GetAsyncKeyState(int virtualKey);

        {{il2CppConstructor}}    internal static void Configure(EdiClient edi, Action<string> log)
            {
                _edi = edi;
                _log = log;
            }

            private void Awake()
            {
                try
                {
                    Directory.CreateDirectory(Paths.ConfigPath);
                    var path = Path.Combine(Paths.ConfigPath, GamePreset.TelemetryFileName);
                    _telemetry = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    {
                        AutoFlush = false
                    };
                }
                catch (Exception exception)
                {
                    _log?.Invoke($"Could not open discovery telemetry: {exception.Message}");
                }

                SceneManager.sceneLoaded += OnSceneLoaded;
                SceneManager.activeSceneChanged += OnActiveSceneChanged;
                Application.onBeforeRender += Tick;
        {{playMakerAwakeStatement}}        var scene = SceneManager.GetActiveScene();
                _hasFocus = Application.isFocused;
                SyncHotkeyState();
                ObserveScene(scene.name, "startup", triggerMapping: true);
                _nextScanAt = 0f;
                RunObserverStep("startup-scan", ScanRuntimeObjects);
            }

            private void OnDestroy()
            {
                SceneManager.sceneLoaded -= OnSceneLoaded;
                SceneManager.activeSceneChanged -= OnActiveSceneChanged;
                Application.onBeforeRender -= Tick;
        {{playMakerDestroyStatement}}        StopAllMappedPlayback("observer-stopped");
                Emit("SESSION", SceneManager.GetActiveScene().name, string.Empty, "observer-stopped", string.Empty);
                FlushTelemetry();
                _telemetry?.Dispose();
                _telemetry = null;
            }

            private void Update() => Tick();

            private void Tick()
            {
                var frame = Time.frameCount;
                if (frame == _lastTickFrame) return;
                _lastTickFrame = frame;
                PollHotkeys();
                var now = Time.unscaledTime;
                if (now >= _nextTelemetryFlushAt)
                {
                    _nextTelemetryFlushAt = now + 1f;
                    FlushTelemetry();
                }
                if (now >= _nextScanAt)
                {
                    _nextScanAt = now + 1f;
                    RunObserverStep("runtime-scan", ScanRuntimeObjects);
                }
                if (!_hasFocus || !Application.isFocused || _applicationPaused) return;
                if (now < _nextAnimatorPollAt) return;
                _nextAnimatorPollAt = now + 1f / 30f;
                RunObserverStep("animator-poll", PollAnimators);
            }

            private void FlushTelemetry()
            {
                try { _telemetry?.Flush(); }
                catch (Exception exception) { _log?.Invoke($"Telemetry flush failed: {exception.Message}"); }
            }

            private void RunObserverStep(string stage, Action action)
            {
                try { action(); }
                catch (Exception exception) { ReportObserverError(stage, exception); }
            }

            private void ReportObserverError(string stage, Exception exception)
            {
                var signature = stage + ":" + exception.GetType().Name;
                var now = Time.unscaledTime;
                if (_observerErrorNextAt.TryGetValue(signature, out var nextAt) && now < nextAt) return;
                _observerErrorNextAt[signature] = now + 10f;
                _log?.Invoke($"Runtime discovery {stage} failed: {exception}");
                Emit("OBSERVER_ERROR", SceneManager.GetActiveScene().name, string.Empty, stage,
                    $"type={exception.GetType().Name};message={exception.Message}");
            }

            private void PollHotkeys()
            {
                if (_hotkeysUnavailable) return;
                try
                {
                    var key1Down = IsHotkeyDown(0x31, 0x61);
                    var key2Down = IsHotkeyDown(0x32, 0x62);
                    var key3Down = IsHotkeyDown(0x33, 0x63);
                    var key4Down = IsHotkeyDown(0x34, 0x64);
                    if (_hasFocus && Application.isFocused && key1Down && !_key1Down)
                    {
                        _edi?.Pause();
                        _log?.Invoke("EDI hotkey 1: playback paused.");
                    }
                    if (_hasFocus && Application.isFocused && key2Down && !_key2Down)
                    {
                        _edi?.Resume();
                        _log?.Invoke("EDI hotkey 2: playback resumed.");
                    }
                    if (_hasFocus && Application.isFocused && key3Down && !_key3Down)
                    {
                        _edi?.SetIntensity(40);
                        _log?.Invoke("EDI hotkey 3: intensity set to 40%.");
                    }
                    if (_hasFocus && Application.isFocused && key4Down && !_key4Down)
                    {
                        _edi?.SetIntensity(100);
                        _log?.Invoke("EDI hotkey 4: intensity set to 100%.");
                    }
                    _key1Down = key1Down;
                    _key2Down = key2Down;
                    _key3Down = key3Down;
                    _key4Down = key4Down;
                }
                catch (Exception exception)
                {
                    DisableHotkeys(exception);
                }
            }

            private void SyncHotkeyState()
            {
                if (_hotkeysUnavailable) return;
                try
                {
                    _key1Down = IsHotkeyDown(0x31, 0x61);
                    _key2Down = IsHotkeyDown(0x32, 0x62);
                    _key3Down = IsHotkeyDown(0x33, 0x63);
                    _key4Down = IsHotkeyDown(0x34, 0x64);
                }
                catch (Exception exception)
                {
                    DisableHotkeys(exception);
                }
            }

            private static bool IsHotkeyDown(int mainKey, int numpadKey) =>
                (GetAsyncKeyState(mainKey) & 0x8000) != 0 || (GetAsyncKeyState(numpadKey) & 0x8000) != 0;

            private void DisableHotkeys(Exception exception)
            {
                _hotkeysUnavailable = true;
                _log?.Invoke($"EDI hotkeys unavailable: {exception.Message}");
            }

            private void OnApplicationFocus(bool hasFocus)
            {
                _hasFocus = hasFocus;
                SyncHotkeyState();
                if (!hasFocus) StopAllMappedPlayback("application-focus-lost", preserveSceneAction: true);
                else ResumeMappedScene("application-focus-restored");
            }

            private void OnApplicationPause(bool paused)
            {
                _applicationPaused = paused;
                if (paused) StopAllMappedPlayback("application-paused", preserveSceneAction: true);
                else ResumeMappedScene("application-resumed");
            }

            private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
            {
                if (mode == LoadSceneMode.Additive)
                {
                    _nextScanAt = 0f;
                    Emit("LOADED_SCENE", SceneManager.GetActiveScene().name, string.Empty, scene.name, "Additive");
                    return;
                }
                ResetRuntimeDiscovery();
                ObserveScene(scene.name, mode.ToString(), triggerMapping: false);
            }

            private void OnActiveSceneChanged(Scene previous, Scene current)
            {
                StopAllMappedPlayback("active-scene-changed");
                ResetRuntimeDiscovery();
                Emit("ACTIVE_SCENE", current.name, string.Empty, current.name, $"from={previous.name}");
                ObserveScene(current.name, "active", triggerMapping: true);
            }

            private void ResetRuntimeDiscovery()
            {
                StopRuntimeMappings("runtime-discovery-reset");
                _animators.Clear();
        {{playMakerResetStatements}}        _states.Clear();
                _lastScanSummary = string.Empty;
                _nextAnimatorPollAt = 0f;
                _nextScanAt = 0f;
            }

            private void ObserveScene(string sceneName, string details, bool triggerMapping)
            {
                Emit("SCENE", sceneName, string.Empty, sceneName, details);
                if (triggerMapping && GamePreset.TryMatchScene(sceneName, out var action))
                {
                    _log?.Invoke($"Scene preset matched '{sceneName}' -> '{action}'.");
                    _activeSceneAction = action;
                    if (!CanDrivePlayback())
                    {
                        _playbackSuspended = true;
                        return;
                    }
                    _edi?.Play(action);
                    Emit("SCRIPT_PLAY", sceneName, string.Empty, action, "source=scene;seekMilliseconds=0");
                }
            }

            private bool CanDrivePlayback() => _hasFocus && Application.isFocused && !_applicationPaused;

            private void ResumeMappedScene(string reason)
            {
                if (!CanDrivePlayback() || !_playbackSuspended) return;
                _playbackSuspended = false;
                if (string.IsNullOrWhiteSpace(_activeSceneAction)) return;
                _edi?.Play(_activeSceneAction);
                Emit("SCRIPT_PLAY", SceneManager.GetActiveScene().name, string.Empty, _activeSceneAction,
                    $"source=scene;reason={reason};seekMilliseconds=0");
            }

            private void ScanRuntimeObjects()
            {
                RunObserverStep("animator-scan", ScanAnimators);
                var playMakerCount = 0;
        {{playMakerScanStatement}}        var summary = $"animators={_animators.Count};playMakerAvailable={{hasPlayMaker.ToString().ToLowerInvariant()}};playMakerFsms={playMakerCount}";
                if (summary == _lastScanSummary) return;
                _lastScanSummary = summary;
                Emit("OBSERVER_SCAN", SceneManager.GetActiveScene().name, string.Empty, summary, string.Empty);
            }

            private void ScanAnimators()
            {
                foreach (var animator in UnityEngine.Object.FindObjectsOfType<Animator>())
                {
                    try
                    {
                        if (animator == null || animator.gameObject == null || !animator.gameObject.scene.IsValid() ||
                            !animator.isActiveAndEnabled || !animator.gameObject.activeInHierarchy) continue;
                        _animators[animator.GetInstanceID()] = animator;
                    }
                    catch (Exception exception) { ReportObserverError("animator-scan-item", exception); }
                }
            }

            private void PollAnimators()
            {
                foreach (var id in _animators.Keys.ToArray())
                {
                    try
                    {
                        if (!_animators.TryGetValue(id, out var animator) || animator == null)
                        {
                            RemoveTrackedAnimator(id, "animator-destroyed", string.Empty, string.Empty);
                            continue;
                        }
                        var sceneName = animator.gameObject.scene.name;
                        var objectPath = BuildPath(animator.transform);
                        if (!animator.isActiveAndEnabled || !animator.gameObject.activeInHierarchy)
                        {
                            RemoveTrackedAnimator(id, "animator-inactive", sceneName, objectPath);
                            continue;
                        }
                        for (var layer = 0; layer < animator.layerCount; layer++) ObserveAnimatorLayer(animator, layer);
                    }
                    catch (Exception exception)
                    {
                        ReportObserverError("animator-poll-item", exception);
                        RemoveTrackedAnimator(id, "animator-failed", string.Empty, string.Empty);
                    }
                }
            }

            private void ObserveAnimatorLayer(Animator animator, int layer)
            {
                var state = animator.GetCurrentAnimatorStateInfo(layer);
                var clips = animator.GetCurrentAnimatorClipInfo(layer);
                var clipName = string.Empty;
                AnimationClip? dominantClip = null;
                var bestWeight = -1f;
                for (var index = 0; index < clips.Length; index++)
                {
                    if (clips[index].clip != null && clips[index].weight > bestWeight)
                    {
                        bestWeight = clips[index].weight;
                        dominantClip = clips[index].clip;
                        clipName = dominantClip.name;
                    }
                }

                var key = animator.GetInstanceID() + ":" + layer;
                var sceneName = animator.gameObject.scene.name;
                var objectPath = BuildPath(animator.transform);
                var clipLength = dominantClip != null ? dominantClip.length : 0f;
                var stateLength = Math.Max(0f, state.length);
                var animatorSpeed = animator.speed;
                var effectiveSpeed = Math.Abs(animatorSpeed * state.speed * state.speedMultiplier);
                var cycleDuration = stateLength > 0f
                    ? stateLength
                    : clipLength / Math.Max(0.001f, effectiveSpeed);
                var isLooping = state.loop || dominantClip != null && dominantClip.isLooping;
                var normalizedTime = Math.Max(0f, state.normalizedTime);
                var loopIndex = (int)Math.Floor(normalizedTime);
                var phase = normalizedTime - loopIndex;
                var phaseSeconds = cycleDuration * phase;
                var seekMilliseconds = Math.Max(0, (int)Math.Round(phaseSeconds * 1000));
                var cycleDurationMilliseconds = cycleDuration > 0f
                    ? Math.Max(1, (int)Math.Round(cycleDuration * 1000))
                    : 0;
                var mapped = GamePreset.TryMatchAnimation(
                    clipName, objectPath, cycleDurationMilliseconds, out var mappedAction);
                var details = TimingDetails(layer, state, clipLength, cycleDuration, isLooping, loopIndex,
                    phaseSeconds, animatorSpeed, mapped ? mappedAction : string.Empty);

                if (_states.TryGetValue(key, out var previous) && previous.StateHash == state.fullPathHash &&
                    previous.ClipName == clipName)
                {
                    var observedAt = Time.unscaledTime;
                    var restarted = normalizedTime + 0.05f < previous.LastNormalizedTime;
                    var progressed = Math.Abs(normalizedTime - previous.LastNormalizedTime) > 0.0001f;
                    previous.LastNormalizedTime = normalizedTime;
                    if (progressed) previous.LastProgressAt = observedAt;
                    var currentMappedAction = mapped ? mappedAction : string.Empty;
                    if (!previous.MappedAction.Equals(currentMappedAction, StringComparison.OrdinalIgnoreCase))
                    {
                        previous.MappedAction = currentMappedAction;
                        previous.StoppedForInactivity = false;
                        Emit("ANIMATOR_VARIANT", sceneName, objectPath, clipName, details);
                        if (mapped && !previous.Completed && (isLooping || normalizedTime < 1f))
                            StartMappedAnimation(key, mappedAction, seekMilliseconds, sceneName, objectPath, "timing-variant");
                        else
                            StopMappedAnimation(key, "timing-variant-exit", sceneName, objectPath);
                    }
                    if (previous.StoppedForInactivity)
                    {
                        if (!progressed) return;
                        previous.StoppedForInactivity = false;
                        previous.LoopIndex = loopIndex;
                        previous.Completed = false;
                        Emit("ANIMATOR_RESUME", sceneName, objectPath, clipName, details);
                        if (mapped) StartMappedAnimation(key, mappedAction, seekMilliseconds, sceneName, objectPath, "resume");
                        return;
                    }
                    var stallTimeout = Math.Max(0.5f, Math.Min(2f, cycleDuration * 0.25f));
                    if (mapped && observedAt - previous.LastProgressAt >= stallTimeout)
                    {
                        previous.StoppedForInactivity = true;
                        Emit("ANIMATOR_STALLED", sceneName, objectPath, clipName,
                            details + $";stallSeconds={Number(observedAt - previous.LastProgressAt)}");
                        StopMappedAnimation(key, "animation-stalled", sceneName, objectPath);
                        return;
                    }
                    if (isLooping && loopIndex > previous.LoopIndex)
                    {
                        previous.LoopIndex = loopIndex;
                        if (observedAt - previous.LastLoopTelemetryAt >= 1f)
                        {
                            previous.LastLoopTelemetryAt = observedAt;
                            Emit("ANIMATOR_LOOP", sceneName, objectPath, clipName, details);
                        }
                        return;
                    }
                    if (restarted)
                    {
                        previous.LoopIndex = loopIndex;
                        previous.Completed = false;
                        Emit("ANIMATOR_RESTART", sceneName, objectPath, clipName, details);
                        if (mapped) StartMappedAnimation(key, mappedAction, seekMilliseconds, sceneName, objectPath, "restart");
                        return;
                    }
                    if (!isLooping && normalizedTime >= 1f && !previous.Completed)
                    {
                        previous.Completed = true;
                        Emit("ANIMATOR_END", sceneName, objectPath, clipName, details);
                        StopMappedAnimation(key, "animation-complete", sceneName, objectPath);
                    }
                    return;
                }

                var hadMappedAction = _activeAnimatorActions.ContainsKey(key);
                var completed = !isLooping && normalizedTime >= 1f;
                _states[key] = new StateSnapshot(state.fullPathHash, clipName, normalizedTime, loopIndex, completed,
                    Time.unscaledTime, mapped ? mappedAction : string.Empty);
                Emit("ANIMATOR", sceneName, objectPath, clipName, details);
                if (mapped && !completed)
                {
                    StartMappedAnimation(key, mappedAction, seekMilliseconds, sceneName, objectPath, "state-enter");
                }
                else if (hadMappedAction)
                {
                    StopMappedAnimation(key, "state-exit", sceneName, objectPath);
                }
            }

            private void StartMappedAnimation(string key, string action, int seekMilliseconds, string scene,
                string objectPath, string reason)
            {
                if (GamePreset.IsReaction(action))
                {
                    foreach (var reactionKey in _activeAnimatorActions
                                 .Where(pair => GamePreset.IsReaction(pair.Value))
                                 .Select(pair => pair.Key)
                                 .ToArray())
                        _activeAnimatorActions.Remove(reactionKey);
                }
                else
                {
                    _activeSceneAction = string.Empty;
                    _activeAnimatorActions.Clear();
                }
                _activeAnimatorActions[key] = action;
                _currentRuntimeActionKey = key;
                _log?.Invoke($"Animation preset matched -> '{action}' ({reason}, seek {seekMilliseconds} ms).");
                _edi?.Play(action, seekMilliseconds);
                Emit("SCRIPT_PLAY", scene, objectPath, action,
                    $"source=animation;reason={reason};seekMilliseconds={seekMilliseconds}");
            }

            private void StopMappedAnimation(string key, string reason, string scene, string objectPath)
            {
                if (!_activeAnimatorActions.TryGetValue(key, out var action)) return;
                _activeAnimatorActions.Remove(key);
                if (_currentRuntimeActionKey == key)
                    StopCurrentRuntimeAction(action);
                Emit("SCRIPT_STOP", scene, objectPath, string.Empty, $"reason={reason}");
            }

            private void StopCurrentRuntimeAction(string action)
            {
                if (GamePreset.IsReaction(action))
                {
                    _currentRuntimeActionKey = _activeAnimatorActions
                        .FirstOrDefault(pair => !GamePreset.IsReaction(pair.Value)).Key ?? string.Empty;
                    var hasTrackedPrimary = !string.IsNullOrWhiteSpace(_currentRuntimeActionKey) ||
                                            !string.IsNullOrWhiteSpace(_activeSceneAction);
                    _edi?.Stop(stopUnderlying: !hasTrackedPrimary);
                    return;
                }
                _currentRuntimeActionKey = string.Empty;
                if (string.IsNullOrWhiteSpace(_activeSceneAction)) _edi?.Stop();
            }

            private void RemoveAnimatorMappings(string keyPrefix, string reason, string scene, string objectPath)
            {
                var keys = _activeAnimatorActions.Keys.Where(key => key.StartsWith(keyPrefix, StringComparison.Ordinal)).ToArray();
                var stoppedCurrent = keys.Contains(_currentRuntimeActionKey);
                var currentAction = stoppedCurrent && _activeAnimatorActions.TryGetValue(_currentRuntimeActionKey, out var action)
                    ? action
                    : string.Empty;
                foreach (var key in keys) _activeAnimatorActions.Remove(key);
                if (stoppedCurrent)
                    StopCurrentRuntimeAction(currentAction);
                if (keys.Length > 0) Emit("SCRIPT_STOP", scene, objectPath, string.Empty, $"reason={reason}");
            }

            private void RemoveTrackedAnimator(int id, string reason, string scene, string objectPath)
            {
                _animators.Remove(id);
                var keyPrefix = id + ":";
                foreach (var key in _states.Keys.Where(key => key.StartsWith(keyPrefix, StringComparison.Ordinal)).ToArray())
                    _states.Remove(key);
                RemoveAnimatorMappings(keyPrefix, reason, scene, objectPath);
            }

        {{playMakerMethods}}

            private void StopRuntimeMappings(string reason)
            {
                if (_activeAnimatorActions.Count == 0)
                {
                    _currentRuntimeActionKey = string.Empty;
                    return;
                }
                var currentAction = _activeAnimatorActions.TryGetValue(_currentRuntimeActionKey, out var action)
                    ? action
                    : string.Empty;
                var shouldStop = !string.IsNullOrWhiteSpace(_currentRuntimeActionKey) &&
                                 (GamePreset.IsReaction(currentAction) || string.IsNullOrWhiteSpace(_activeSceneAction));
                var stopUnderlying = GamePreset.IsReaction(currentAction) &&
                                     string.IsNullOrWhiteSpace(_activeSceneAction);
                _activeAnimatorActions.Clear();
                _currentRuntimeActionKey = string.Empty;
                if (!shouldStop) return;
                _edi?.Stop(stopUnderlying);
                Emit("SCRIPT_STOP", SceneManager.GetActiveScene().name, string.Empty, string.Empty, $"reason={reason}");
            }

            private void StopAllMappedPlayback(string reason, bool preserveSceneAction = false)
            {
                if (preserveSceneAction && _playbackSuspended) return;
                if (_activeAnimatorActions.Count == 0 && string.IsNullOrWhiteSpace(_activeSceneAction))
                {
                    if (preserveSceneAction) _playbackSuspended = true;
                    return;
                }
                foreach (var pair in _activeAnimatorActions)
                {
                    if (!GamePreset.IsReaction(pair.Value) && _states.TryGetValue(pair.Key, out var state))
                        state.StoppedForInactivity = true;
                }
                var currentAction = _activeAnimatorActions.TryGetValue(_currentRuntimeActionKey, out var action)
                    ? action
                    : _activeSceneAction;
        {{playMakerPlaybackResetStatement}}        _activeAnimatorActions.Clear();
                _currentRuntimeActionKey = string.Empty;
                if (!preserveSceneAction) _activeSceneAction = string.Empty;
                _playbackSuspended = preserveSceneAction || !CanDrivePlayback();
                _edi?.Stop(stopUnderlying: GamePreset.IsReaction(currentAction));
                Emit("SCRIPT_STOP", SceneManager.GetActiveScene().name, string.Empty, string.Empty, $"reason={reason}");
            }

            private static string TimingDetails(int layer, AnimatorStateInfo state, float clipLength,
                float cycleDuration, bool isLooping, int loopIndex, float phaseSeconds, float animatorSpeed,
                string mappedAction) => string.Join(";",
                $"layer={layer}",
                $"stateHash={state.fullPathHash}",
                $"normalizedTime={Number(state.normalizedTime)}",
                $"loopIndex={loopIndex}",
                $"loop={isLooping.ToString().ToLowerInvariant()}",
                $"clipLengthSeconds={Number(clipLength)}",
                $"stateLengthSeconds={Number(state.length)}",
                $"cycleDurationSeconds={Number(cycleDuration)}",
                $"phaseSeconds={Number(phaseSeconds)}",
                $"animatorSpeed={Number(animatorSpeed)}",
                $"stateSpeed={Number(state.speed)}",
                $"speedMultiplier={Number(state.speedMultiplier)}",
                $"mappedAction={mappedAction}");

            private static string Number(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

            private void Emit(string kind, string scene, string objectPath, string candidate, string details)
            {
                try
                {
                    var line = string.Join("\t", DateTimeOffset.UtcNow.ToString("O"), Clean(kind), Clean(scene),
                        Clean(objectPath), Clean(candidate), Clean(details));
                    _telemetry?.WriteLine(line);
                }
                catch (Exception exception) { _log?.Invoke($"Telemetry write failed: {exception.Message}"); }
            }

            private static string BuildPath(Transform transform)
            {
                var names = new Stack<string>();
                Transform? current = transform;
                while (current != null)
                {
                    names.Push(current.name);
                    current = current.parent;
                }
                return string.Join("/", names.ToArray());
            }

            private static string Clean(string? value) =>
                (value ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

            private sealed class StateSnapshot
            {
                public StateSnapshot(int stateHash, string clipName, float normalizedTime, int loopIndex, bool completed,
                    float observedAt, string mappedAction)
                {
                    StateHash = stateHash;
                    ClipName = clipName;
                    LastNormalizedTime = normalizedTime;
                    LoopIndex = loopIndex;
                    Completed = completed;
                    LastProgressAt = observedAt;
                    LastLoopTelemetryAt = observedAt;
                    MappedAction = mappedAction;
                }

                public int StateHash { get; }
                public string ClipName { get; }
                public float LastNormalizedTime { get; set; }
                public int LoopIndex { get; set; }
                public bool Completed { get; set; }
                public float LastProgressAt { get; set; }
                public float LastLoopTelemetryAt { get; set; }
                public string MappedAction { get; set; }
                public bool StoppedForInactivity { get; set; }
            }
        }
        {{playMakerPatch}}
        """;
    }

    private static string CreatePlayMakerObserverMethods() => """
            private int ScanPlayMakerFsms()
            {
                var activeIds = new HashSet<int>();
                foreach (var fsm in UnityEngine.Object.FindObjectsOfType<PlayMakerFSM>())
                {
                    try
                    {
                        if (fsm == null || fsm.gameObject == null || !fsm.gameObject.scene.IsValid() ||
                            !fsm.isActiveAndEnabled || !fsm.gameObject.activeInHierarchy) continue;
                        var id = fsm.GetInstanceID();
                        activeIds.Add(id);
                        _playMakerFsms[id] = fsm;
                        ObservePlayMakerFsm(fsm);
                    }
                    catch (Exception exception) { ReportObserverError("playmaker-scan-item", exception); }
                }
                foreach (var id in _playMakerFsms.Keys.Where(id => !activeIds.Contains(id)).ToArray())
                    RemoveTrackedPlayMakerFsm(id, "fsm-missing", string.Empty, string.Empty);
                return activeIds.Count;
            }

            internal static void ObservePatchedPlayMakerState(HutongGames.PlayMaker.Fsm fsm)
            {
                var observer = _instance;
                if (observer is null) return;
                try
                {
                    var component = fsm.FsmComponent;
                    if (component != null) observer.ObservePlayMakerFsm(component);
                }
                catch (Exception exception) { observer.ReportObserverError("playmaker-state-patch", exception); }
            }

            private void ObservePlayMakerFsm(PlayMakerFSM fsm)
            {
                var id = fsm.GetInstanceID();
                _playMakerFsms[id] = fsm;
                var sceneName = fsm.gameObject.scene.name;
                var objectPath = BuildPath(fsm.transform);
                if (!fsm.isActiveAndEnabled || !fsm.gameObject.activeInHierarchy)
                {
                    RemoveTrackedPlayMakerFsm(id, "fsm-inactive", sceneName, objectPath);
                    return;
                }

                var stateName = fsm.ActiveStateName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(stateName))
                {
                    EndPlayMakerState(id, "state-empty", sceneName, objectPath);
                    return;
                }
                var fsmName = fsm.FsmName ?? string.Empty;
                var candidate = string.IsNullOrWhiteSpace(fsmName) ? stateName : fsmName + " / " + stateName;
                _playMakerStates.TryGetValue(id, out var previousState);
                var previousCandidate = previousState?.Candidate ?? string.Empty;
                if (candidate == previousCandidate && previousState?.NeedsResume != true) return;

                var key = "fsm:" + id + ":";
                var hadMappedAction = _activeAnimatorActions.ContainsKey(key);
                var snapshot = new FsmSnapshot(candidate, sceneName, objectPath);
                _playMakerStates[id] = snapshot;
                var mapped = GamePreset.TryMatchAnimation(candidate, objectPath, 0, out var mappedAction);
                Emit("FSM_STATE", sceneName, objectPath, candidate, string.Join(";",
                    $"stream={id}", $"fsm={fsmName}", $"state={stateName}",
                    $"previous={previousCandidate}",
                    $"mappedAction={(mapped ? mappedAction : string.Empty)}"));
                if (mapped && CanDrivePlayback())
                {
                    StartMappedAnimation(key, mappedAction, 0, sceneName, objectPath, "fsm-state-enter");
                }
                else if (mapped)
                {
                    snapshot.NeedsResume = true;
                }
                else if (hadMappedAction)
                {
                    StopMappedAnimation(key, "fsm-state-exit", sceneName, objectPath);
                }
            }

            private void EndPlayMakerState(int id, string reason, string scene, string objectPath)
            {
                if (!_playMakerStates.TryGetValue(id, out var previousState)) return;
                _playMakerStates.Remove(id);
                var key = "fsm:" + id + ":";
                var exitScene = string.IsNullOrWhiteSpace(scene) ? previousState.Scene : scene;
                var exitObjectPath = string.IsNullOrWhiteSpace(objectPath) ? previousState.ObjectPath : objectPath;
                StopMappedAnimation(key, reason, exitScene, exitObjectPath);
                Emit("FSM_EXIT", exitScene, exitObjectPath, previousState.Candidate, $"stream={id};reason={reason}");
            }

            private void RemoveTrackedPlayMakerFsm(int id, string reason, string scene, string objectPath)
            {
                EndPlayMakerState(id, reason, scene, objectPath);
                _playMakerFsms.Remove(id);
            }

            private void PreparePlayMakerResume(IEnumerable<KeyValuePair<string, string>> actions)
            {
                const string prefix = "fsm:";
                foreach (var pair in actions)
                {
                    if (GamePreset.IsReaction(pair.Value) ||
                        !pair.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var idText = pair.Key.Substring(prefix.Length).TrimEnd(':');
                    if (int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                        _playMakerStates.TryGetValue(id, out var state)) state.NeedsResume = true;
                }
            }

            private sealed class FsmSnapshot
            {
                public FsmSnapshot(string candidate, string scene, string objectPath)
                {
                    Candidate = candidate;
                    Scene = scene;
                    ObjectPath = objectPath;
                }

                public string Candidate { get; }
                public string Scene { get; }
                public string ObjectPath { get; }
                public bool NeedsResume { get; set; }
            }

        """;

    private static string CreatePlayMakerPatch() => """

        [HarmonyPatch(typeof(HutongGames.PlayMaker.Fsm), nameof(HutongGames.PlayMaker.Fsm.SwitchState),
            new[] { typeof(HutongGames.PlayMaker.FsmState) })]
        internal static class PlayMakerStatePatch
        {
            [HarmonyPostfix]
            private static void AfterSwitchState(HutongGames.PlayMaker.Fsm __instance) =>
                RuntimeObserver.ObservePatchedPlayMakerState(__instance);
        }
        """;

    private static string CreateGamePreset(StudioProject project, string pluginGuid, UnityModPresetKind preset)
    {
        var conventionEntries = new StringBuilder();
        var reactionEntries = new StringBuilder();
        foreach (var action in project.Actions)
        {
            conventionEntries.Append("        [")
                .Append(CSharpLiteral(NormalizeMatchName(action.Name)))
                .Append("] = ActionNames.")
                .Append(ToIdentifier(action.Name))
                .AppendLine(",");
            if (action.Type == EdiGalleryType.Reaction)
                reactionEntries.Append("        ActionNames.")
                    .Append(ToIdentifier(action.Name))
                    .AppendLine(",");
        }
        var actions = project.Actions.ToDictionary(action => action.Name, StringComparer.OrdinalIgnoreCase);
        var mappings = project.Game.TriggerMappings
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Candidate) && actions.ContainsKey(mapping.ActionName))
            .GroupBy(mapping => $"{mapping.Kind}:{NormalizeMatchName(mapping.Candidate)}:{mapping.ObjectPath}:" +
                                mapping.CycleDurationMilliseconds, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .ToArray();
        var sceneEntries = CreateMappingEntries(mappings.Where(mapping => mapping.Kind == UnityTriggerKind.Scene), actions);
        var animationEntries = CreateAnimationMappingEntries(
            mappings.Where(mapping => mapping.Kind == UnityTriggerKind.AnimationClip), actions);
        var matchScenes = preset is UnityModPresetKind.SceneNames or UnityModPresetKind.SceneAndAnimationNames;
        var matchAnimations = preset is UnityModPresetKind.AnimationNames or UnityModPresetKind.SceneAndAnimationNames;
        return $$"""
        using System.Text;

        internal static class GamePreset
        {
            public const string TelemetryFileName = "{{pluginGuid}}.telemetry.tsv";
            private const bool MatchScenes = {{matchScenes.ToString().ToLowerInvariant()}};
            private const bool MatchAnimations = {{matchAnimations.ToString().ToLowerInvariant()}};
            private static readonly Dictionary<string, string> ConventionActions = new(StringComparer.OrdinalIgnoreCase)
            {
        {{conventionEntries.ToString().TrimEnd()}}
            };
            private static readonly HashSet<string> ReactionActions = new(StringComparer.OrdinalIgnoreCase)
            {
        {{reactionEntries.ToString().TrimEnd()}}
            };
            private static readonly Dictionary<string, string> SceneMappings = new(StringComparer.OrdinalIgnoreCase)
            {
        {{sceneEntries}}
            };
            private static readonly AnimationMapping[] AnimationMappings =
            {
        {{animationEntries}}
            };

            public static bool TryMatchScene(string sceneName, out string action) =>
                TryMatch(SceneMappings, MatchScenes, sceneName, out action);

            public static bool IsReaction(string action) => ReactionActions.Contains(action);

            public static bool TryMatchAnimation(string clipName, string objectPath, int cycleDurationMilliseconds,
                out string action)
            {
                var normalized = Normalize(clipName);
                AnimationMapping? best = null;
                var bestPathSpecificity = -1;
                var bestTimingSpecificity = -1;
                var bestDifference = int.MaxValue;
                foreach (var mapping in AnimationMappings)
                {
                    if (!mapping.Candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase)) continue;
                    var pathSpecificity = string.IsNullOrWhiteSpace(mapping.ObjectPath) ? 0 : 1;
                    if (pathSpecificity > 0 &&
                        !mapping.ObjectPath.Equals(objectPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var timingSpecificity = mapping.CycleDurationMilliseconds > 0 ? 1 : 0;
                    var difference = int.MaxValue;
                    if (timingSpecificity > 0)
                    {
                        if (cycleDurationMilliseconds <= 0) continue;
                        difference = Math.Abs(mapping.CycleDurationMilliseconds - cycleDurationMilliseconds);
                        var tolerance = Math.Max(25, mapping.CycleDurationMilliseconds / 10);
                        if (difference > tolerance) continue;
                    }
                    if (best is not null && (pathSpecificity < bestPathSpecificity ||
                        pathSpecificity == bestPathSpecificity && timingSpecificity < bestTimingSpecificity ||
                        pathSpecificity == bestPathSpecificity && timingSpecificity == bestTimingSpecificity &&
                        difference >= bestDifference)) continue;
                    best = mapping;
                    bestPathSpecificity = pathSpecificity;
                    bestTimingSpecificity = timingSpecificity;
                    bestDifference = difference;
                }
                if (best is not null)
                {
                    action = best.Action;
                    return true;
                }
                if (MatchAnimations && ConventionActions.TryGetValue(normalized, out var matched))
                {
                    action = matched;
                    return true;
                }
                action = string.Empty;
                return false;
            }

            private static bool TryMatch(Dictionary<string, string> explicitMappings, bool conventionEnabled,
                string candidate, out string action)
            {
                var normalized = Normalize(candidate);
                if (explicitMappings.TryGetValue(normalized, out var explicitAction))
                {
                    action = explicitAction;
                    return true;
                }
                if (conventionEnabled && ConventionActions.TryGetValue(normalized, out var matched))
                {
                    action = matched;
                    return true;
                }
                action = string.Empty;
                return false;
            }

            private static string Normalize(string value)
            {
                var builder = new StringBuilder(value.Length);
                foreach (var character in value)
                {
                    if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
                }
                return builder.ToString();
            }

            private sealed class AnimationMapping
            {
                public AnimationMapping(string candidate, string objectPath, int cycleDurationMilliseconds, string action)
                {
                    Candidate = candidate;
                    ObjectPath = objectPath;
                    CycleDurationMilliseconds = cycleDurationMilliseconds;
                    Action = action;
                }

                public string Candidate { get; }
                public string ObjectPath { get; }
                public int CycleDurationMilliseconds { get; }
                public string Action { get; }
            }
        }
        """;
    }

    private static string CreateMappingEntries(
        IEnumerable<UnityTriggerMapping> mappings,
        IReadOnlyDictionary<string, AuthoredAction> actions)
    {
        var builder = new StringBuilder();
        foreach (var mapping in mappings)
        {
            builder.Append("        [")
                .Append(CSharpLiteral(NormalizeMatchName(mapping.Candidate)))
                .Append("] = ActionNames.")
                .Append(ToIdentifier(actions[mapping.ActionName].Name))
                .AppendLine(",");
        }
        return builder.ToString().TrimEnd();
    }

    private static string CreateAnimationMappingEntries(
        IEnumerable<UnityTriggerMapping> mappings,
        IReadOnlyDictionary<string, AuthoredAction> actions)
    {
        var builder = new StringBuilder();
        foreach (var mapping in mappings
                     .OrderByDescending(mapping => mapping.CycleDurationMilliseconds.HasValue)
                     .ThenByDescending(mapping => !string.IsNullOrWhiteSpace(mapping.ObjectPath)))
        {
            builder.Append("        new AnimationMapping(")
                .Append(CSharpLiteral(NormalizeMatchName(mapping.Candidate))).Append(", ")
                .Append(CSharpLiteral(mapping.ObjectPath.Trim())).Append(", ")
                .Append(mapping.CycleDurationMilliseconds ?? 0).Append(", ActionNames.")
                .Append(ToIdentifier(actions[mapping.ActionName].Name))
                .AppendLine("),");
        }
        return builder.ToString().TrimEnd();
    }

    private static string CreateEdiClient() => """
    using System.Collections.Concurrent;
    using System.Net.Http;

    public sealed class EdiClient : IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
        private readonly ConcurrentQueue<Func<Task>> _commands = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly object _enqueueGate = new();
        private readonly string _baseUrl;
        private readonly Action<string> _log;
        private readonly Task _worker;
        private long _playbackRevision;
        private int _wakePending;

        public EdiClient(string baseUrl, Action<string> log)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _log = log;
            _worker = Task.Run(WorkAsync);
        }

        public void Play(string name, int seekMilliseconds = 0)
        {
            var route = $"/Play/{Uri.EscapeDataString(name)}?seek={Math.Max(0, seekMilliseconds)}";
            lock (_enqueueGate)
            {
                var revision = Interlocked.Increment(ref _playbackRevision);
                EnqueueCore(async () =>
                {
                    if (revision != Interlocked.Read(ref _playbackRevision)) return;
                    await PostAsync(route).ConfigureAwait(false);
                });
            }
        }

        public void Stop(bool stopUnderlying = false)
        {
            lock (_enqueueGate)
            {
                Interlocked.Increment(ref _playbackRevision);
                EnqueueCore(async () =>
                {
                    await PostAsync("/Stop").ConfigureAwait(false);
                    if (stopUnderlying) await PostAsync("/Stop").ConfigureAwait(false);
                });
            }
        }
        public void Pause() => Enqueue(() => PostAsync("/Pause?untilResume=true"));
        public void Resume() => Enqueue(() => PostAsync("/Resume?AtCurrentTime=false"));
        public void SetIntensity(int percent)
        {
            var clamped = Math.Max(0, Math.Min(100, percent));
            Enqueue(() => PostAsync($"/Intensity/{clamped}"));
        }

        private void Enqueue(Func<Task> command)
        {
            lock (_enqueueGate) EnqueueCore(command);
        }

        private void EnqueueCore(Func<Task> command)
        {
            _commands.Enqueue(command);
            if (Interlocked.Exchange(ref _wakePending, 1) == 0) _signal.Release();
        }

        private async Task WorkAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _wakePending, 0);
                    while (_commands.TryDequeue(out var command))
                    {
                        try { await command().ConfigureAwait(false); }
                        catch (Exception exception) { _log($"EDI request failed: {exception.Message}"); }
                    }
                    if (!_commands.IsEmpty && Interlocked.Exchange(ref _wakePending, 1) == 0) _signal.Release();
                }
            }
            catch (OperationCanceledException) { }
        }

        private async Task PostAsync(string route)
        {
            using var response = await _http.PostAsync(_baseUrl + route, new StringContent(string.Empty), _shutdown.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }

        public void Dispose()
        {
            _shutdown.Cancel();
            try { _worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _shutdown.Dispose();
            _signal.Dispose();
            _http.Dispose();
        }
    }
    """;

    private static string CreateActionNames(StudioProject project)
    {
        var builder = new StringBuilder("public static class ActionNames\n{\n");
        foreach (var action in project.Actions)
        {
            builder.Append("    public const string ")
                .Append(ToIdentifier(action.Name))
                .Append(" = ")
                .Append(CSharpLiteral(action.Name))
                .AppendLine(";");
        }
        if (project.Actions.Count == 0) builder.AppendLine("    public const string Example = \"replace-me\";");
        return builder.AppendLine("}").ToString();
    }

    private static string CreateReadme(
        StudioProject project,
        UnityInspectionResult inspection,
        string gameRoot,
        string targetFramework,
        UnityModPresetKind preset,
        string telemetryFile) => $$"""
        # {{project.Name}} EDI Integration

        Generated for Unity {{inspection.Runtime}} ({{inspection.Architecture}}), `{{targetFramework}}`, preset `{{preset}}`.

        1. Install the matching BepInEx build into `{{gameRoot}}` and run the game once.
        2. For IL2CPP, wait for `BepInEx\interop` generation to finish.
        3. Build with `dotnet build IntegrationMod.csproj -c Release` or use **Build + install** in EDI Integration Studio.
        4. Install only `bin\Release\{{targetFramework}}\IntegrationMod.dll` into a dedicated `BepInEx\plugins` folder.
        5. Start EDI, run the game, and watch discovery telemetry in the studio.

        Playback hotkeys (top row or numpad): `1` pause, `2` resume, `3` intensity 40%, `4` intensity 100%.

        Runtime discovery records scene changes, Animator clip/state transitions, and PlayMaker FSM states when available in:

        `{{telemetryFile}}`

        `Discovery` records candidates and applies only explicit mappings created in the studio. The scene/animation convention presets additionally map a discovered name to an EDI action when both normalize to the same letters and digits. Use discovery first to avoid false triggers, then add game-specific Harmony patches for semantic events that scene, Animator, and FSM observation cannot identify reliably.

        Reference policy: never add the game's `mscorlib.dll`, `netstandard.dll`, `System*.dll`, or a second `UnityEngine.dll`. The generated compile-only references use `ExternallyResolved=true` so MSBuild cannot walk into Unity's incompatible framework facade set. Regenerate or update references after game/BepInEx upgrades.
        """;

    private static string NormalizeMatchName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private static string ToIdentifier(string value)
    {
        var builder = new StringBuilder();
        var startOfWord = true;
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character))
            {
                startOfWord = true;
                continue;
            }
            builder.Append(startOfWord ? char.ToUpperInvariant(character) : character);
            startOfWord = false;
        }
        var identifier = builder.Length == 0 ? "Integration" : builder.ToString();
        return char.IsDigit(identifier[0]) ? "_" + identifier : identifier;
    }

    private static string CSharpLiteral(string value) => JsonSerializer.Serialize(value);
}
