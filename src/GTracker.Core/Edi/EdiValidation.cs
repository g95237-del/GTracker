using GTracker.Core.Projects;
using System.Text.RegularExpressions;

namespace GTracker.Core.Edi;

public enum ValidationSeverity
{
    Warning,
    Error
}

public sealed record ValidationIssue(ValidationSeverity Severity, string Location, string Message)
{
    public override string ToString() => $"{Severity}: {Location}: {Message}";
}

public sealed class EdiValidator
{
    private static readonly Regex SafeStemPattern = new("^[A-Za-z0-9][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReservedWindowsNames = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ValidationIssue> Validate(StudioProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var issues = new List<ValidationIssue>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var scriptKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(project.Name))
        {
            issues.Add(Error("Project", "Project name is required."));
        }

        foreach (var action in project.Actions)
        {
            var location = string.IsNullOrWhiteSpace(action.Name) ? $"Scene {action.Id}" : action.Name;
            if (string.IsNullOrWhiteSpace(action.Name) || action.Name != action.Name.Trim())
            {
                issues.Add(Error(location, "Name is required and cannot have leading or trailing whitespace."));
            }
            else if (!names.Add(action.Name))
            {
                issues.Add(Error(location, "Scene names must be unique ignoring case."));
            }
            else if (action.Name.Any(char.IsControl))
            {
                issues.Add(Error(location, "Scene names cannot contain control characters or line breaks."));
            }

            if (!IsSafeEdiStem(action.FileName))
            {
                issues.Add(Error(location,
                    "FileName must use only letters, numbers, underscores, and hyphens; dots, tags, whitespace, and reserved names are unsafe in EDI discovery."));
            }
            else if (!fileNames.Add(action.FileName))
            {
                issues.Add(Error(location, "FileName must be globally unique in this project."));
            }

            var variant = NormalizeVariant(action.Variant);
            if (variant != "default" && !IsSafeEdiStem(variant))
            {
                issues.Add(Error(location, "Variant must be 'default' or a single safe folder name."));
            }
            else if (variant.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Error(location, "Variant 'None' is reserved by EDI as a device stop sentinel."));
            }

            if (action.DurationMilliseconds <= 0)
            {
                issues.Add(Error(location, "Duration must be greater than zero milliseconds."));
            }

            if (action.Tracks.Count == 0 || action.Tracks.All(track => track.Axis != EdiAxis.Default))
            {
                issues.Add(Error(location, "A Default axis track is required."));
            }

            foreach (var duplicateAxis in action.Tracks.GroupBy(track => track.Axis).Where(group => group.Count() > 1))
            {
                issues.Add(Error(location, $"Axis {duplicateAxis.Key} is present more than once."));
            }

            foreach (var track in action.Tracks)
            {
                var trackLocation = $"{location}/{track.Axis}";
                var scriptKey = $"{variant}/{action.FileName}/{track.Axis}";
                if (!scriptKeys.Add(scriptKey))
                {
                    issues.Add(Error(trackLocation, "Another scene exports to the same variant, filename, and axis."));
                }

                if (track.Points.Count == 0)
                {
                    issues.Add(Error(trackLocation, "At least one funscript point is required."));
                    continue;
                }

                var timestamps = new HashSet<int>();
                foreach (var point in track.Points)
                {
                    if (point.At < 0 || point.At > action.DurationMilliseconds)
                    {
                        issues.Add(Error(trackLocation, $"Point timestamp {point.At} is outside 0..{action.DurationMilliseconds} ms."));
                    }

                    if (point.Pos is < 0 or > 100)
                    {
                        issues.Add(Error(trackLocation, $"Point position {point.Pos} is outside 0..100."));
                    }

                    if (!timestamps.Add(point.At))
                    {
                        issues.Add(Error(trackLocation, $"Point timestamp {point.At} is duplicated."));
                    }
                }

                var ordered = track.Points.OrderBy(point => point.At).ToArray();
                if (ordered[0].At != 0)
                {
                    issues.Add(Warning(trackLocation, "Exporter will insert a point at 0 ms."));
                }

                if (ordered[^1].At != action.DurationMilliseconds)
                {
                    issues.Add(Warning(trackLocation, "Exporter will insert a point at the definition end boundary."));
                }

                if (action.Loop && ordered[0].Pos != ordered[^1].Pos)
                {
                    issues.Add(Warning(trackLocation, "Exporter will make the final position equal the first position for a clean loop."));
                    var closureInterval = Math.Max(0, action.DurationMilliseconds - ordered[^1].At);
                    if (closureInterval < 50)
                    {
                        issues.Add(Warning(trackLocation,
                            $"Clean-loop repair changes position {ordered[^1].Pos} to {ordered[0].Pos} over only {closureInterval} ms. Add a deliberate closing point to avoid an abrupt device movement."));
                    }
                }
            }
        }

        foreach (var bundle in project.Bundles)
        {
            if (!IsSafeBundleName(bundle.Name))
            {
                issues.Add(Error("Bundles", $"Bundle name '{bundle.Name}' cannot be blank, begin with whitespace, or contain a line break."));
            }

            foreach (var actionName in bundle.Actions)
            {
                if (!names.Contains(actionName))
                {
                    issues.Add(Error($"Bundle {bundle.Name}", $"Unknown scene '{actionName}'."));
                }
                else if (actionName.StartsWith('#') || actionName.StartsWith('-') || actionName.Any(char.IsControl))
                {
                    issues.Add(Error($"Bundle {bundle.Name}", $"Scene '{actionName}' cannot be represented safely in BundleDefinition.txt."));
                }
            }
        }

        return issues;
    }

    internal static string NormalizeVariant(string? variant) =>
        string.IsNullOrWhiteSpace(variant) ? "default" : variant.Trim();

    private static bool IsSafeEdiStem(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && SafeStemPattern.IsMatch(value) &&
        !ReservedWindowsNames.Contains(value);

    private static bool IsSafeBundleName(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value == value.Trim() && !value.Contains('\r') && !value.Contains('\n');

    private static ValidationIssue Error(string location, string message) =>
        new(ValidationSeverity.Error, location, message);

    private static ValidationIssue Warning(string location, string message) =>
        new(ValidationSeverity.Warning, location, message);
}
