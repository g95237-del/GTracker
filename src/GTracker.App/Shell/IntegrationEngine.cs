namespace GTracker.App.Shell;

public enum IntegrationEngine
{
    Unity,
    Godot,
    Unreal,
    RpgMaker
}

public sealed record EngineUiProfile(
    IntegrationEngine Engine,
    string DisplayName,
    string Marker,
    string WorkflowTitle,
    string WorkflowSummary,
    string WorkflowSteps,
    IReadOnlyDictionary<string, string> Palette);

public static class EngineUiCatalog
{
    public static IReadOnlyList<string> PaletteKeys { get; } =
    [
        "WindowBrush", "PanelBrush", "SurfaceBrush", "InputBrush", "PreviewBrush", "PreviewOverlayBrush",
        "EdgeBrush", "StrongEdgeBrush", "TextBrush", "MutedTextBrush", "SectionTextBrush", "AccentBrush",
        "AccentDarkBrush", "AccentHoverBrush", "DangerBrush", "DangerHoverBrush", "DangerTextBrush", "WarningTextBrush"
    ];

    public static IReadOnlyList<EngineUiProfile> Profiles { get; } =
    [
        Create(IntegrationEngine.Unity, "Unity", "BEPINEX", "UNITY MOD TARGET",
            "The production Unity workflow is active.",
            "Analyze runtime  /  Provision BepInEx  /  Generate observer  /  Build + install",
            "#0B1120", "#111827", "#172033", "#0F172A", "#030712", "#E60B1120",
            "#334155", "#475569", "#F1F5F9", "#A8B4C5", "#C7D5E8", "#14B8A6", "#0F766E", "#0D9488"),
        Create(IntegrationEngine.Godot, "Godot", "PCK + AUTOLOAD", "GODOT INTEGRATION",
            "Read-only target analysis is available for Godot 3 and 4 Windows exports; autoload installation remains disabled pending candidate validation.",
            "Inspect PCK  /  Verify export  /  Install autoload  /  Discover scene + animation signals",
            "#071525", "#0B2035", "#102A45", "#0A1A2C", "#030B14", "#E6071525",
            "#244D70", "#3975A4", "#F2F8FC", "#A8C5DB", "#C8E2F3", "#478CBF", "#28628F", "#56A7DE"),
        Create(IntegrationEngine.Unreal, "Unreal Engine", "UE4SS PROFILE", "UNREAL INTEGRATION",
            "Provider foundation selected. Support will be compatibility-profile based for unprotected UE4 and UE5 games.",
            "Inspect build  /  Check protection  /  Match UE4SS profile  /  Discover reflected events",
            "#080D16", "#101925", "#162536", "#0C1520", "#02060B", "#E6080D16",
            "#2B435A", "#456A88", "#F4F8FB", "#A9BDCC", "#D2E2ED", "#38BDF8", "#0369A1", "#0EA5E9"),
        Create(IntegrationEngine.RpgMaker, "RPG Maker", "JS PLUGIN", "RPG MAKER INTEGRATION",
            "Provider foundation selected. Initial support will target MV and MZ through their official JavaScript plugin system.",
            "Detect generation  /  Inspect plugins  /  Install EDI plugin  /  Discover events + scenes",
            "#180B20", "#25102F", "#32173F", "#210D2A", "#0B040F", "#E6180B20",
            "#5B2B68", "#824593", "#FBF4FD", "#CDB2D4", "#E9CFEE", "#D866C8", "#96398B", "#E879D8")
    ];

    public static EngineUiProfile Get(IntegrationEngine engine) =>
        Profiles.First(profile => profile.Engine == engine);

    private static EngineUiProfile Create(
        IntegrationEngine engine,
        string displayName,
        string marker,
        string workflowTitle,
        string workflowSummary,
        string workflowSteps,
        params string[] colors)
    {
        if (colors.Length != 14) throw new ArgumentException("An engine palette must define fourteen engine colors.", nameof(colors));
        var palette = PaletteKeys.Zip(colors.Concat(
        [
            "#BE123C", "#E11D48", "#FDA4AF", "#FCD34D"
        ]), (key, color) => KeyValuePair.Create(key, color)).ToDictionary();
        return new(engine, displayName, marker, workflowTitle, workflowSummary, workflowSteps, palette);
    }
}
