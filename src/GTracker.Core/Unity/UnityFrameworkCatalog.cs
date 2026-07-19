using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using GTracker.Core.Projects;

namespace GTracker.Core.Unity;

public enum UnityFrameworkSupport
{
    RuntimeObserver,
    DetectionOnly
}

public sealed record UnityFrameworkCapability(
    string Id,
    string DisplayName,
    UnityFrameworkSupport Support,
    string Evidence)
{
    public bool HasRuntimeObserver => Support == UnityFrameworkSupport.RuntimeObserver;
}

public static class UnityFrameworkCatalog
{
    public const string Animator = "unity.animator";
    public const string LegacyAnimation = "unity.legacy-animation";
    public const string Timeline = "unity.playable-director";
    public const string PlayMaker = "playmaker";
    public const string Spine = "spine-unity";

    public static IReadOnlyList<UnityFrameworkCapability> Detect(UnityInspectionResult inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var gameRoot = Path.GetDirectoryName(inspection.ExecutablePath)!;
        var referenceDirectory = inspection.Runtime == UnityRuntimeKind.Il2Cpp
            ? Path.Combine(gameRoot, "BepInEx", "interop")
            : Path.Combine(inspection.DataDirectory, "Managed");
        var assemblies = IndexAssemblies(referenceDirectory);
        var sourceTypes = ReadSourceTypes(assemblies);
        var capabilities = new List<UnityFrameworkCapability>();

        string? Assembly(params string[] names)
        {
            foreach (var name in names)
            {
                if (assemblies.TryGetValue(name, out var path)) return Path.GetFileName(path);
            }
            return null;
        }

        string? Type(params string[] names)
        {
            foreach (var name in names)
            {
                if (sourceTypes.Contains(name)) return name;
            }
            return null;
        }

        void Add(string id, string displayName, UnityFrameworkSupport support, string? evidence)
        {
            if (!string.IsNullOrWhiteSpace(evidence)) capabilities.Add(new(id, displayName, support, evidence));
        }

        var animationEvidence = Assembly("UnityEngine.AnimationModule.dll", "UnityEngine.dll");
        Add(Animator, "Unity Animator (Mecanim)", UnityFrameworkSupport.RuntimeObserver, animationEvidence);
        Add(LegacyAnimation, "Unity Legacy Animation", UnityFrameworkSupport.RuntimeObserver, animationEvidence);
        Add(Timeline, "Unity PlayableDirector / Timeline", UnityFrameworkSupport.RuntimeObserver,
            Assembly("UnityEngine.DirectorModule.dll"));
        Add(PlayMaker, "PlayMaker", UnityFrameworkSupport.RuntimeObserver, Assembly("PlayMaker.dll"));

        Add("unity.visual-scripting", "Unity Visual Scripting / Bolt", UnityFrameworkSupport.DetectionOnly,
            Assembly("Unity.VisualScripting.Flow.dll", "Unity.VisualScripting.State.dll"));
        var spineUnityAssembly = Assembly("spine-unity.dll", "Spine.Unity.dll");
        var spineCoreAssembly = Assembly("spine-csharp.dll", "Spine.dll");
        var spineEvidence = spineUnityAssembly is not null && spineCoreAssembly is not null
            ? spineUnityAssembly + "+" + spineCoreAssembly
            : spineUnityAssembly ?? Type("Spine.Unity.SkeletonAnimation");
        Add(Spine, "Spine-Unity",
            inspection.Runtime == UnityRuntimeKind.Mono &&
            spineUnityAssembly?.Equals("spine-unity.dll", StringComparison.OrdinalIgnoreCase) == true &&
            spineCoreAssembly?.Equals("spine-csharp.dll", StringComparison.OrdinalIgnoreCase) == true
                ? UnityFrameworkSupport.RuntimeObserver
                : UnityFrameworkSupport.DetectionOnly,
            spineEvidence);
        Add("live2d-cubism", "Live2D Cubism", UnityFrameworkSupport.DetectionOnly,
            Assembly("Live2D.Cubism.dll", "Live2D.Cubism.Framework.dll") ??
            Type("Live2D.Cubism.Framework.Motion.CubismMotionController"));
        Add("animancer", "Animancer", UnityFrameworkSupport.DetectionOnly,
            Assembly("Kybernetik.Animancer.dll", "Animancer.dll", "Animancer.Lite.dll") ??
            Type("Animancer.AnimancerComponent"));
        Add("dragonbones", "DragonBones", UnityFrameworkSupport.DetectionOnly,
            Assembly("DragonBones.dll") ?? Type("DragonBones.UnityArmatureComponent"));
        Add("dotween", "DOTween", UnityFrameworkSupport.DetectionOnly,
            Assembly("DOTween.dll", "DOTweenPro.dll") ?? Type("DG.Tweening.DOTween"));
        Add("leantween", "LeanTween", UnityFrameworkSupport.DetectionOnly,
            Assembly("LeanTween.dll") ?? Type("LeanTween"));
        Add("fungus", "Fungus", UnityFrameworkSupport.DetectionOnly,
            Assembly("Fungus.dll") ?? Type("Fungus.Flowchart"));
        Add("yarn-spinner", "Yarn Spinner", UnityFrameworkSupport.DetectionOnly,
            Assembly("YarnSpinner.Unity.dll") ?? Type("Yarn.Unity.DialogueRunner"));
        Add("adventure-creator", "Adventure Creator", UnityFrameworkSupport.DetectionOnly,
            Assembly("AC.Runtime.dll", "AdventureCreator.dll") ?? Type("AC.EventManager"));
        Add("pixel-crushers-dialogue", "Pixel Crushers Dialogue System", UnityFrameworkSupport.DetectionOnly,
            Assembly("PixelCrushers.DialogueSystem.dll") ??
            Type("PixelCrushers.DialogueSystem.DialogueSystemController"));
        Add("naninovel", "Naninovel", UnityFrameworkSupport.DetectionOnly,
            Assembly("Elringus.Naninovel.Runtime.dll", "Naninovel.Runtime.dll") ?? Type("Naninovel.Engine"));
        Add("nodecanvas", "NodeCanvas", UnityFrameworkSupport.DetectionOnly,
            Assembly("NodeCanvas.Framework.dll") ?? Type("NodeCanvas.StateMachines.FSMOwner"));
        Add("behavior-designer", "Behavior Designer", UnityFrameworkSupport.DetectionOnly,
            Assembly("BehaviorDesigner.Runtime.dll") ?? Type("BehaviorDesigner.Runtime.BehaviorTree"));
        Add("ink", "Ink", UnityFrameworkSupport.DetectionOnly,
            Assembly("Ink-Libraries.dll", "Ink.Runtime.dll") ?? Type("Ink.Runtime.Story"));

        return capabilities;
    }

    private static Dictionary<string, string> IndexAssemblies(string directory)
    {
        var assemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory)) return assemblies;
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly))
                assemblies.TryAdd(Path.GetFileName(path), path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        return assemblies;
    }

    private static HashSet<string> ReadSourceTypes(IReadOnlyDictionary<string, string> assemblies)
    {
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in new[] { "Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll" })
        {
            if (!assemblies.TryGetValue(name, out var path)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                using var peReader = new PEReader(stream);
                if (!peReader.HasMetadata) continue;
                var metadata = peReader.GetMetadataReader();
                foreach (var handle in metadata.TypeDefinitions)
                {
                    var definition = metadata.GetTypeDefinition(handle);
                    var typeName = metadata.GetString(definition.Name);
                    if (typeName == "<Module>") continue;
                    var typeNamespace = metadata.GetString(definition.Namespace);
                    types.Add(string.IsNullOrWhiteSpace(typeNamespace) ? typeName : typeNamespace + "." + typeName);
                }
            }
            catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return types;
    }
}
