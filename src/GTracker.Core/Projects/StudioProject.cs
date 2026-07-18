namespace GTracker.Core.Projects;

public enum EdiGalleryType
{
    Gallery,
    Reaction,
    Filler
}

public enum EdiAxis
{
    Default,
    Surge,
    Sway,
    Twist,
    Roll,
    Pitch,
    Vibrate,
    Valve,
    Suction,
    Rotate
}

public enum UnityRuntimeKind
{
    Unknown,
    Mono,
    Il2Cpp
}

public enum UnityModPresetKind
{
    Discovery,
    SceneNames,
    AnimationNames,
    SceneAndAnimationNames
}

public enum UnityTriggerKind
{
    Scene,
    AnimationClip
}

public sealed class StudioProject
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Untitled Integration";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public GameTarget Game { get; set; } = new();
    public List<AuthoredAction> Actions { get; set; } = [];
    public List<BundleDefinition> Bundles { get; set; } = [];
}

public sealed class GameTarget
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public UnityRuntimeKind Runtime { get; set; }
    public string Architecture { get; set; } = string.Empty;
    public string UnityVersion { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
    public string BepInExFlavor { get; set; } = string.Empty;
    public UnityModPresetKind ModPreset { get; set; }
    public string ModProjectPath { get; set; } = string.Empty;
    public string TelemetryPath { get; set; } = string.Empty;
    public string InstalledPluginPath { get; set; } = string.Empty;
    public List<UnityTriggerMapping> TriggerMappings { get; set; } = [];
    public LinearSimulatorLayout Simulator { get; set; } = new();

    public void SetTriggerMapping(UnityTriggerKind kind, string candidate, string actionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);
        candidate = candidate.Trim();
        actionName = actionName.Trim();
        TriggerMappings.RemoveAll(mapping => mapping.Kind == kind &&
            mapping.Candidate.Equals(candidate, StringComparison.OrdinalIgnoreCase));
        TriggerMappings.Add(new UnityTriggerMapping
        {
            Kind = kind,
            Candidate = candidate,
            ActionName = actionName
        });
    }
}

public sealed class UnityTriggerMapping
{
    public UnityTriggerKind Kind { get; set; }
    public string Candidate { get; set; } = string.Empty;
    public string ActionName { get; set; } = string.Empty;
}

public sealed class LinearSimulatorLayout
{
    public bool IsVisible { get; set; } = true;
    public double CenterX { get; set; } = 0.5;
    public double CenterY { get; set; } = 0.38;
    public double Width { get; set; } = 0.42;
    public double Height { get; set; } = 0.1;
    public double RotationDegrees { get; set; } = -12;
}

public sealed class AuthoredAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "new-action";
    public string FileName { get; set; } = "new-action";
    public EdiGalleryType Type { get; set; } = EdiGalleryType.Gallery;
    public bool Loop { get; set; } = true;
    public bool IsLocked { get; set; }
    public string Description { get; set; } = string.Empty;
    public string Variant { get; set; } = "default";
    public int DurationMilliseconds { get; set; } = 1000;
    public string SourceClipPath { get; set; } = string.Empty;
    public DateTimeOffset? SourceStartedAtUtc { get; set; }
    public DateTimeOffset? SourceEndedAtUtc { get; set; }
    public string UnitySceneName { get; set; } = string.Empty;
    public string UnityAnimationName { get; set; } = string.Empty;
    public List<ActionTrack> Tracks { get; set; } = [new()];

    public override string ToString() => $"{Name}  [{Type.ToString().ToLowerInvariant()}]" + (IsLocked ? "  [LOCKED]" : string.Empty);
}

public sealed class ActionTrack
{
    public EdiAxis Axis { get; set; } = EdiAxis.Default;
    public List<FunscriptPoint> Points { get; set; } = [];
}

public readonly record struct FunscriptPoint(int At, int Pos);

public sealed class BundleDefinition
{
    public string Name { get; set; } = "default";
    public List<string> Actions { get; set; } = [];
}
