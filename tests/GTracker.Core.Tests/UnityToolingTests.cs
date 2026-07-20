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
    public void Inspect_ReportsSupportedAndDetectedFrameworks()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "FrameworkGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            var managed = Path.Combine(directory, "FrameworkGame_Data", "Managed");
            Directory.CreateDirectory(managed);
            foreach (var name in new[]
                     {
                         "Assembly-CSharp.dll", "UnityEngine.AnimationModule.dll", "UnityEngine.DirectorModule.dll",
                         "Unity.VisualScripting.Flow.dll", "PlayMaker.dll", "spine-unity.dll", "spine-csharp.dll"
                     })
                File.WriteAllBytes(Path.Combine(managed, name), []);

            var result = new UnityGameInspector().Inspect(executable);

            Assert.Contains(result.Frameworks, framework =>
                framework.Id == UnityFrameworkCatalog.LegacyAnimation && framework.HasRuntimeObserver);
            Assert.Contains(result.Frameworks, framework =>
                framework.Id == UnityFrameworkCatalog.Timeline && framework.HasRuntimeObserver);
            Assert.Contains(result.Frameworks, framework =>
                framework.Id == UnityFrameworkCatalog.PlayMaker && framework.HasRuntimeObserver);
            Assert.Contains(result.Frameworks, framework =>
                framework.Id == UnityFrameworkCatalog.Spine && framework.HasRuntimeObserver);
            Assert.Contains(result.Frameworks, framework =>
                framework.Id == "unity.visual-scripting" && !framework.HasRuntimeObserver);
            Assert.Contains(result.Findings, finding => finding.Contains("Unity Visual Scripting"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Inspect_RequiresBothSpineAssembliesForMonoObservation()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var executable = Path.Combine(directory, "FrameworkGame.exe");
            File.Copy(Environment.ProcessPath!, executable);
            var managed = Path.Combine(directory, "FrameworkGame_Data", "Managed");
            Directory.CreateDirectory(managed);
            File.WriteAllBytes(Path.Combine(managed, "spine-unity.dll"), []);

            var result = new UnityGameInspector().Inspect(executable);

            Assert.Contains(result.Frameworks, framework =>
                framework.Id == UnityFrameworkCatalog.Spine && !framework.HasRuntimeObserver);
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
            Assert.DoesNotContain("PlayMaker", projectFile);
            Assert.Contains("BasePlugin", plugin);
            Assert.Contains("Optional runtime patches failed", plugin);
            Assert.Contains("GetAsyncKeyState", observer);
            Assert.Contains("IsHotkeyDown(0x31, 0x61)", observer);
            Assert.Contains("IsHotkeyDown(0x34, 0x64)", observer);
            Assert.DoesNotContain("Input.GetKey", observer);
            Assert.Contains("_edi?.Pause()", observer);
            Assert.Contains("_edi?.Resume()", observer);
            Assert.Contains("_edi?.SetIntensity(40)", observer);
            Assert.Contains("_edi?.SetIntensity(100)", observer);
            Assert.DoesNotContain("PlayMakerFSM", observer);
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
            foreach (var name in new[]
                     {
                         "UnityEngine.dll", "UnityEngine.CoreModule.dll", "UnityEngine.AnimationModule.dll",
                         "UnityEngine.DirectorModule.dll", "Unity.VisualScripting.Flow.dll", "PlayMaker.dll",
                         "spine-unity.dll", "spine-csharp.dll"
                     })
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
                Actions =
                [
                    new AuthoredAction { Name = "attack", FileName = "attack" },
                    new AuthoredAction
                    {
                        Name = "fast-attack",
                        FileName = "fast-attack",
                        Type = EdiGalleryType.Reaction
                    }
                ],
                Game = new GameTarget
                {
                    TriggerMappings =
                    [
                        new UnityTriggerMapping
                        {
                            Kind = UnityTriggerKind.Scene,
                            Candidate = "Boss Arena",
                            ActionName = "attack"
                        },
                        new UnityTriggerMapping
                        {
                            Kind = UnityTriggerKind.AnimationClip,
                            Candidate = "Shared Loop",
                            ActionName = "attack",
                            ObjectPath = "Root/Animator",
                            CycleDurationMilliseconds = 817
                        },
                        new UnityTriggerMapping
                        {
                            Kind = UnityTriggerKind.AnimationClip,
                            Candidate = "Shared Loop",
                            ActionName = "fast-attack",
                            ObjectPath = "Root/Animator",
                            CycleDurationMilliseconds = 408
                        }
                    ]
                }
            };
            await new UnityModScaffolder().GenerateAsync(project, inspection, output, UnityModPresetKind.AnimationNames);

            var projectFile = await File.ReadAllTextAsync(Path.Combine(output, "IntegrationMod.csproj"));
            var preset = await File.ReadAllTextAsync(Path.Combine(output, "GamePreset.cs"));
            Assert.Contains("UnityEngine.AnimationModule", projectFile);
            Assert.Contains("UnityEngine.DirectorModule", projectFile);
            Assert.Contains("<Reference Include=\"PlayMaker\"", projectFile);
            Assert.Contains("<Reference Include=\"spine-csharp\"", projectFile);
            Assert.Contains("<Reference Include=\"spine-unity\"", projectFile);
            Assert.DoesNotContain("UnityEngine.InputLegacyModule", projectFile);
            Assert.Contains("<Reference Include=\"UnityEngine\"", projectFile);
            Assert.DoesNotContain("UnityEngine.SceneManagementModule", projectFile);
            Assert.DoesNotContain("Assembly-CSharp", projectFile);
            Assert.Contains("MatchAnimations = true", preset);
            Assert.Contains("MatchScenes = false", preset);
            Assert.Contains("[\"bossarena\"] = ActionNames.Attack", preset);
            Assert.Contains("new AnimationMapping(\"sharedloop\", \"Root/Animator\", 817, ActionNames.Attack)", preset);
            Assert.Contains("new AnimationMapping(\"sharedloop\", \"Root/Animator\", 408, ActionNames.FastAttack)", preset);
            Assert.Contains("Math.Max(25, mapping.CycleDurationMilliseconds / 10)", preset);
            Assert.Contains("IsReaction", preset);
            var policyStart = preset.IndexOf("ReactionActions", StringComparison.Ordinal);
            Assert.True(policyStart >= 0);
            var policyEnd = preset.IndexOf("};", policyStart, StringComparison.Ordinal);
            Assert.True(policyEnd > policyStart);
            var playbackPolicy = preset[policyStart..policyEnd];
            Assert.DoesNotContain("ActionNames.Attack", playbackPolicy);
            Assert.Contains("ActionNames.FastAttack", playbackPolicy);

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
            Assert.Contains("ANIMATOR_VARIANT", repairedObserver);
            Assert.Contains("ANIMATOR_STALLED", repairedObserver);
            Assert.Contains("FSM_STATE", repairedObserver);
            Assert.Contains("LEGACY_ANIMATION", repairedObserver);
            Assert.Contains("TIMELINE", repairedObserver);
            Assert.Contains("Object.FindObjectsOfType<Animation>", repairedObserver);
            Assert.Contains("Object.FindObjectsOfType<PlayableDirector>", repairedObserver);
            Assert.Contains("Object.FindObjectsOfType<SkeletonAnimation>", repairedObserver);
            Assert.Contains("SPINE_ANIMATION", repairedObserver);
            Assert.Contains("state.Tracks", repairedObserver);
            Assert.Contains("Unity Visual Scripting / Bolt", repairedObserver);
            Assert.Contains("support=detected-only", repairedObserver);
            Assert.Contains("Object.FindObjectsOfType<PlayMakerFSM>", repairedObserver);
            Assert.DoesNotContain("Resources.FindObjectsOfTypeAll", repairedObserver);
            Assert.DoesNotContain("PollPlayMakerFsms", repairedObserver);
            Assert.Contains("OBSERVER_SCAN", repairedObserver);
            Assert.Contains("OBSERVER_ERROR", repairedObserver);
            Assert.Contains("_nextScanAt = now + 1f", repairedObserver);
            Assert.Contains("_nextAnimatorPollAt = now + 1f / 30f", repairedObserver);
            Assert.Contains("AutoFlush = false", repairedObserver);
            Assert.Contains("FlushTelemetry", repairedObserver);
            Assert.Contains("stream={id}", repairedObserver);
            Assert.Contains("StopRuntimeMappings", repairedObserver);
            Assert.Contains("!Application.isFocused || _applicationPaused", repairedObserver);
            Assert.Contains("LOADED_SCENE", repairedObserver);
            Assert.Contains("preserveSceneAction: true", repairedObserver);
            Assert.Contains("PreparePlayMakerResume", repairedObserver);
            Assert.Contains("Application.onBeforeRender += Tick", repairedObserver);
            Assert.Contains("HarmonyPatch(typeof(HutongGames.PlayMaker.Fsm)", repairedObserver);
            Assert.Contains("ObservePatchedPlayMakerState", repairedObserver);
            Assert.Contains("new[] { typeof(HutongGames.PlayMaker.FsmState) }", repairedObserver);
            Assert.Contains("mapped && CanDrivePlayback()", repairedObserver);
            Assert.Contains("animator.isActiveAndEnabled", repairedObserver);
            Assert.Contains("StopAllMappedPlayback", repairedObserver);
            Assert.Contains("cycleDurationSeconds", repairedObserver);
            Assert.Contains("var cycleDuration = stateLength > 0f", repairedObserver);
            Assert.Contains("kind + \"_RESTART\"", repairedObserver);
            Assert.Contains("UpdateMappedAnimation", repairedObserver);
            Assert.Contains("ResyncMappedAnimation(key, \"loop-resync\")", repairedObserver);
            Assert.Contains("resume-suppressed", repairedObserver);
            Assert.Contains("resume-after-reaction", repairedObserver);
            Assert.Contains("Play(action, seekMilliseconds, GamePreset.IsReaction(action))", repairedObserver);
            Assert.Contains("Stop(stopUnderlying: GamePreset.IsReaction(currentAction))", repairedObserver);
            Assert.Contains("GetAsyncKeyState", repairedObserver);
            Assert.DoesNotContain("Input.GetKey", repairedObserver);
            Assert.DoesNotContain("Math.Clamp", repairedObserver);
            var mappedStart = repairedObserver.IndexOf("private void StartMappedAnimation", StringComparison.Ordinal);
            var mappedStop = repairedObserver.IndexOf("private void StopMappedAnimation", mappedStart, StringComparison.Ordinal);
            Assert.True(mappedStart >= 0 && mappedStop > mappedStart);
            Assert.DoesNotContain("_activeAnimatorActions.Clear", repairedObserver[mappedStart..mappedStop]);
            var mappedUpdate = repairedObserver.IndexOf("private void UpdateMappedAnimation", mappedStop, StringComparison.Ordinal);
            Assert.True(mappedUpdate > mappedStop);
            var mappedStopPolicy = repairedObserver[mappedStop..mappedUpdate];
            Assert.Contains("if (_currentRuntimeActionKey != key) return", mappedStopPolicy);
            var stopTelemetry = mappedStopPolicy.IndexOf("Emit(\"SCRIPT_STOP\"", StringComparison.Ordinal);
            var stopArbitration = mappedStopPolicy.IndexOf("StopCurrentRuntimeAction(playback.Action)", StringComparison.Ordinal);
            Assert.True(stopTelemetry >= 0 && stopArbitration > stopTelemetry);
            var arbitrationStart = repairedObserver.IndexOf("private void StopCurrentRuntimeAction", mappedUpdate, StringComparison.Ordinal);
            var removalStart = repairedObserver.IndexOf("private void RemoveAnimatorMappings", arbitrationStart, StringComparison.Ordinal);
            Assert.True(arbitrationStart > mappedUpdate && removalStart > arbitrationStart);
            var arbitrationPolicy = repairedObserver[arbitrationStart..removalStart];
            var reactionStop = arbitrationPolicy.IndexOf("_edi?.Stop(stopUnderlying: !hasTrackedPrimary)", StringComparison.Ordinal);
            var fallbackPlay = arbitrationPolicy.IndexOf("_edi?.Play(playback.Action, playback.SeekMilliseconds)", StringComparison.Ordinal);
            Assert.True(reactionStop >= 0 && fallbackPlay > reactionStop);
            var repairedClient = await File.ReadAllTextAsync(ediClientPath);
            Assert.Contains("/Intensity/{clamped}", repairedClient);
            Assert.Contains("Interlocked.Increment(ref _playbackRevision)", repairedClient);
            Assert.Contains("bool preservePendingPlay = false", repairedClient);
            Assert.Contains("if (!preservePendingPlay && revision !=", repairedClient);
            Assert.Contains("await PostAsync(\"/Stop\")", repairedClient);
            Assert.Contains("await PostAsync(route)", repairedClient);
            Assert.Contains("bool stopUnderlying = false", repairedClient);
            Assert.Contains("if (stopUnderlying) await PostAsync(\"/Stop\")", repairedClient);
            Assert.Contains("lock (_enqueueGate)", repairedClient);
            Assert.DoesNotContain("clearPending", repairedClient);
            Assert.DoesNotContain("_commands.Count", repairedClient);
            var playStart = repairedClient.IndexOf("public void Play", StringComparison.Ordinal);
            Assert.True(playStart >= 0);
            var stopStart = repairedClient.IndexOf("public void Stop", playStart, StringComparison.Ordinal);
            Assert.True(stopStart > playStart);
            Assert.DoesNotContain("/Stop", repairedClient[playStart..stopStart]);
            var repairedProject = await File.ReadAllTextAsync(Path.Combine(output, "IntegrationMod.csproj"));
            Assert.Contains("<Reference Include=\"UnityEngine\"", repairedProject);
            Assert.Contains("<Reference Include=\"UnityEngine.DirectorModule\"", repairedProject);
            Assert.Contains("<Reference Include=\"PlayMaker\"", repairedProject);
            Assert.Contains("<Reference Include=\"spine-csharp\"", repairedProject);
            Assert.Contains("<Reference Include=\"spine-unity\"", repairedProject);
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
            var managed = Path.Combine(gameRoot, "FixtureGame_Data", "Managed");
            Directory.CreateDirectory(managed);
            foreach (var name in new[]
                     {
                         "UnityEngine.AnimationModule.dll", "UnityEngine.DirectorModule.dll", "PlayMaker.dll",
                         "spine-unity.dll", "spine-csharp.dll"
                     })
                File.WriteAllBytes(Path.Combine(managed, name), []);
            var inspection = new UnityInspectionResult(executable, UnityRuntimeKind.Mono, "Amd64",
                Path.Combine(gameRoot, "FixtureGame_Data"), [])
            {
                RecommendedTargetFramework = "netstandard2.0",
                BepInEx = BepInExFlavor.Mono5,
                IsModularUnity = true
            };
            var project = new StudioProject
            {
                Name = "Compile Fixture",
                Actions = [new AuthoredAction { Name = "attack", FileName = "attack" }],
                Game = new GameTarget
                {
                    TriggerMappings =
                    [
                        new UnityTriggerMapping
                        {
                            Kind = UnityTriggerKind.AnimationClip,
                            Candidate = "Shared Loop",
                            ActionName = "attack",
                            ObjectPath = "GalleryRoot/Player/Animator",
                            CycleDurationMilliseconds = 1000
                        }
                    ]
                }
            };
            await new UnityModScaffolder().GenerateAsync(project, inspection, output);
            await File.WriteAllTextAsync(Path.Combine(output, "IntegrationMod.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>netstandard2.0</TargetFramework>
                    <AssemblyName>IntegrationMod</AssemblyName>
                    <LangVersion>latest</LangVersion>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(Path.Combine(output, "UnityStubs.cs"), MonoUnityStubs);

            var build = await new UnityModDeployer().BuildAsync(output);

            Assert.True(build.Success, build.Output);
            var loadContext = new System.Runtime.Loader.AssemblyLoadContext(
                "GeneratedMonoFixture-" + Guid.NewGuid().ToString("N"), isCollectible: true);
            try
            {
                using var assemblyStream = File.OpenRead(build.AssemblyPath);
                var assembly = loadContext.LoadFromStream(assemblyStream);
                var preset = assembly.GetType("GamePreset", throwOnError: true)!;
                var matchAnimation = preset.GetMethod("TryMatchAnimation")!;
                object?[] wrappedPathArguments = ["Shared Loop", "Player/Animator", 1000, null];
                Assert.True((bool)matchAnimation.Invoke(null, wrappedPathArguments)!);
                Assert.Equal("attack", wrappedPathArguments[3]);
                object?[] differentPathArguments = ["Shared Loop", "Enemy/Animator", 1000, null];
                Assert.False((bool)matchAnimation.Invoke(null, differentPathArguments)!);
                object?[] leafOnlyArguments = ["Shared Loop", "Animator", 1000, null];
                Assert.False((bool)matchAnimation.Invoke(null, leafOnlyArguments)!);
            }
            finally
            {
                loadContext.Unload();
            }
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
            var interop = Path.Combine(gameRoot, "BepInEx", "interop");
            Directory.CreateDirectory(interop);
            foreach (var name in new[]
                     {
                         "UnityEngine.AnimationModule.dll", "UnityEngine.DirectorModule.dll", "PlayMaker.dll",
                         "spine-unity.dll", "spine-csharp.dll"
                     })
                File.WriteAllBytes(Path.Combine(interop, name), []);
            var inspection = new UnityInspectionResult(executable, UnityRuntimeKind.Il2Cpp, "Amd64",
                Path.Combine(gameRoot, "FixtureGame_Data"), [])
            {
                RecommendedTargetFramework = "net6.0",
                BepInEx = BepInExFlavor.Il2Cpp6
            };
            await new UnityModScaffolder().GenerateAsync(new StudioProject { Name = "IL2CPP Fixture" }, inspection, output);
            var generatedProject = await File.ReadAllTextAsync(Path.Combine(output, "IntegrationMod.csproj"));
            var generatedObserver = await File.ReadAllTextAsync(Path.Combine(output, "RuntimeObserver.cs"));
            Assert.DoesNotContain("spine-unity", generatedProject);
            Assert.DoesNotContain("SkeletonAnimation", generatedObserver);
            Assert.Contains("Spine-Unity", generatedObserver);
            Assert.Contains("id=spine-unity;support=detected-only", generatedObserver);
            await File.WriteAllTextAsync(Path.Combine(output, "IntegrationMod.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net6.0</TargetFramework>
                    <AssemblyName>IntegrationMod</AssemblyName>
                    <LangVersion>latest</LangVersion>
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
            [AttributeUsage(AttributeTargets.Class)]
            public sealed class HarmonyPatchAttribute : Attribute
            {
                public HarmonyPatchAttribute(Type type, string methodName) { }
                public HarmonyPatchAttribute(Type type, string methodName, Type[] argumentTypes) { }
            }
            [AttributeUsage(AttributeTargets.Method)]
            public sealed class HarmonyPostfixAttribute : Attribute { }
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
                public static T[] FindObjectsOfType<T>() => [];
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
            public class Behaviour : Component
            {
                public bool isActiveAndEnabled { get; set; } = true;
            }
            public class MonoBehaviour : Behaviour
            {
                public MonoBehaviour() { }
                public MonoBehaviour(IntPtr pointer) { }
                protected static void Destroy(Object value) => Object.Destroy(value);
            }
            public class Transform : Component
            {
                public Transform? parent { get; set; }
            }
            public class Animator : Behaviour
            {
                public int layerCount => 1;
                public float speed { get; set; } = 1;
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
                public WrapMode wrapMode { get; set; }
            }
            public enum WrapMode { Once, Loop, PingPong, Default, ClampForever }
            public class AnimationState
            {
                public string name { get; set; } = string.Empty;
                public AnimationClip? clip { get; set; }
                public float normalizedTime { get; set; }
                public float length { get; set; }
                public float speed { get; set; } = 1;
                public bool enabled { get; set; }
                public float weight { get; set; }
                public WrapMode wrapMode { get; set; }
            }
            public class Animation : Behaviour, System.Collections.IEnumerable
            {
                public WrapMode wrapMode { get; set; }
                public System.Collections.IEnumerator GetEnumerator() => Array.Empty<AnimationState>().GetEnumerator();
            }
            public static class Resources
            {
                public static T[] FindObjectsOfTypeAll<T>() => [];
            }
            public static class Time
            {
                public static float unscaledTime => 0;
                public static int frameCount => 0;
            }
            public static class Application
            {
                public static bool isFocused => true;
                public static event Action? onBeforeRender;
            }
        }

        namespace UnityEngine.Playables
        {
            public enum PlayState { Playing, Paused, Delayed }
            public enum DirectorWrapMode { Hold, Loop, None }
            public class PlayableAsset : UnityEngine.Object { }
            public class PlayableDirector : UnityEngine.Behaviour
            {
                public PlayableAsset? playableAsset { get; set; }
                public PlayState state { get; set; }
                public double time { get; set; }
                public double duration { get; set; }
                public DirectorWrapMode extrapolationMode { get; set; }
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

        public class PlayMakerFSM : UnityEngine.MonoBehaviour
        {
            public string FsmName { get; set; } = string.Empty;
            public string ActiveStateName { get; set; } = string.Empty;
        }

        namespace HutongGames.PlayMaker
        {
            public sealed class FsmState { }
            public sealed class Fsm
            {
                public PlayMakerFSM FsmComponent { get; } = new();
                public void SwitchState(FsmState state) { }
            }
        }

        namespace Spine
        {
            public sealed class ExposedList<T>
            {
                public int Count { get; set; }
                public T[] Items { get; set; } = [];
            }
            public sealed class Animation
            {
                public string Name { get; set; } = string.Empty;
            }
            public sealed class TrackEntry
            {
                public Animation? Animation { get; set; }
                public float Delay { get; set; }
                public bool Loop { get; set; }
                public float TrackTime { get; set; }
                public float AnimationStart { get; set; }
                public float AnimationEnd { get; set; }
                public float TimeScale { get; set; } = 1;
                public float Alpha { get; set; } = 1;
            }
            public sealed class AnimationState
            {
                public float TimeScale { get; set; } = 1;
                public ExposedList<TrackEntry?> Tracks { get; } = new();
            }
        }

        namespace Spine.Unity
        {
            public sealed class SkeletonAnimation : UnityEngine.Behaviour
            {
                public float timeScale { get; set; } = 1;
                public Spine.AnimationState? AnimationState { get; set; }
            }
        }
        """;
}
