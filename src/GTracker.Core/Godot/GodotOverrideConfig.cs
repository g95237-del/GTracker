using System.Text;

namespace GTracker.Core.Godot;

internal static class GodotOverrideConfig
{
    internal const string AutoloadName = "GTrackerDiscovery";
    private const string BeginMarker = "; GTRACKER-BEGIN discovery-autoload v1";
    private const string EndMarker = "; GTRACKER-END discovery-autoload v1";

    public static byte[] AddAutoload(byte[] original, string absoluteScriptPath)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteScriptPath);
        var hasBom = original.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var text = Encoding.UTF8.GetString(original, hasBom ? Encoding.UTF8.Preamble.Length : 0,
            original.Length - (hasBom ? Encoding.UTF8.Preamble.Length : 0));
        if (text.Contains(BeginMarker, StringComparison.Ordinal) || text.Contains(EndMarker, StringComparison.Ordinal))
            throw new InvalidDataException("override.cfg already contains GTracker ownership markers.");

        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        var autoloadStart = -1;
        var autoloadEnd = lines.Count;
        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith(AutoloadName + "=", StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Refusing to replace an existing unowned {AutoloadName} autoload.");
            if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']')) continue;
            if (trimmed.Equals("[autoload]", StringComparison.OrdinalIgnoreCase))
            {
                if (autoloadStart >= 0) throw new InvalidDataException("override.cfg contains duplicate [autoload] sections.");
                autoloadStart = index;
                autoloadEnd = lines.Count;
            }
            else if (autoloadStart >= 0 && autoloadEnd == lines.Count)
            {
                autoloadEnd = index;
            }
        }

        var resourcePath = absoluteScriptPath.Replace('\\', '/').Replace("\"", "\\\"", StringComparison.Ordinal);
        var block = new[] { BeginMarker, $"{AutoloadName}=\"*{resourcePath}\"", EndMarker };
        if (autoloadStart < 0)
        {
            while (lines.Count > 0 && string.IsNullOrEmpty(lines[^1])) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add(string.Empty);
            lines.Add("[autoload]");
            lines.Add(string.Empty);
            lines.AddRange(block);
        }
        else
        {
            lines.InsertRange(autoloadEnd, block.Prepend(string.Empty));
        }

        var outputText = string.Join(newline, lines).TrimEnd('\r', '\n') + newline;
        var output = Encoding.UTF8.GetBytes(outputText);
        return hasBom ? Encoding.UTF8.Preamble.ToArray().Concat(output).ToArray() : output;
    }
}
