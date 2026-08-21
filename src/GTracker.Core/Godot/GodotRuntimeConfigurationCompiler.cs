using GTracker.Core.Projects;

namespace GTracker.Core.Godot;

public static class GodotRuntimeConfigurationCompiler
{
    public static GodotRuntimeConfiguration Create(StudioProject project, string ediBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(project);
        var actions = project.GetLogicalActions().ToDictionary(action => action.Name, StringComparer.OrdinalIgnoreCase);
        var mappings = project.Game.TriggerMappings.Where(mapping => actions.ContainsKey(mapping.ActionName))
            .SelectMany(mapping => Expand(mapping, actions[mapping.ActionName], project.SpeedMultipliers))
            .ToArray();
        return new(ediBaseUrl.Trim(), mappings);
    }

    private static IEnumerable<GodotRuntimeMapping> Expand(
        UnityTriggerMapping mapping,
        AuthoredAction action,
        IReadOnlyList<double> speedMultipliers)
    {
        var speedVariants = mapping.Kind == UnityTriggerKind.AnimationClip && mapping.CycleDurationMilliseconds > 0
            ? EdiSpeedVariants.Create(action, speedMultipliers)
            : EdiSpeedVariants.Create(action, [1.0]);
        var allowNearestDuration = speedVariants.Count > 1;
        foreach (var speedVariant in speedVariants)
        {
            yield return new(
                mapping.Kind,
                mapping.Candidate,
                speedVariant.ActionName,
                mapping.ObjectPath,
                mapping.CycleDurationMilliseconds is { } duration
                    ? EdiSpeedVariants.ScaleDuration(duration, speedVariant.Multiplier)
                    : null,
                action.Type == EdiGalleryType.Reaction,
                mapping.SceneName,
                action.Loop,
                speedVariant.DurationMilliseconds,
                allowNearestDuration);
        }
    }
}
