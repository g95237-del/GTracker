using System.Text;
using System.Text.Json;
using GTracker.Core.Projects;

namespace GTracker.Core.Edi;

public sealed record ExportResult(string Directory, int DefinitionCount, int ScriptCount, IReadOnlyList<ValidationIssue> Issues);
public sealed record FunscriptPreview(
    int AuthoredPointCount,
    int ExportedPointCount,
    int DurationMilliseconds,
    bool BoundaryInserted,
    bool LoopClosureApplied,
    int? LoopClosureIntervalMilliseconds,
    string Json);

public sealed class EdiExporter
{
    private const string ManifestFileName = ".edi-integration-studio-export.json";
    private const string BuiltInFillerName = "filler";
    private const int BuiltInFillerDurationMilliseconds = 1200;
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly FunscriptPoint[] BuiltInFillerPoints = [new(0, 0), new(600, 40), new(1200, 0)];
    private static readonly JsonSerializerOptions ScriptJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly EdiValidator _validator = new();

    public async Task<ExportResult> ExportAsync(
        StudioProject project,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var issues = _validator.Validate(project);
        var errors = issues.Where(issue => issue.Severity == ValidationSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidDataException("EDI export validation failed:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors.Select(error => error.ToString())));
        }

        outputDirectory = Path.GetFullPath(outputDirectory);
        var parentDirectory = Directory.GetParent(outputDirectory)?.FullName
            ?? throw new InvalidOperationException("The export directory must have a parent directory.");
        Directory.CreateDirectory(parentDirectory);
        var stageDirectory = Path.Combine(parentDirectory, $".{Path.GetFileName(outputDirectory)}.stage.{Guid.NewGuid():N}");
        Directory.CreateDirectory(stageDirectory);
        try
        {
            var scriptCount = await WritePackageAsync(project, stageDirectory, cancellationToken);
            var newFiles = Directory.EnumerateFiles(stageDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(stageDirectory, path).Replace('\\', '/'))
                .OrderBy(path => path.Equals("Definitions.csv", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            ExportManifest? oldManifest = null;
            if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            {
                var manifestPath = Path.Combine(outputDirectory, ManifestFileName);
                if (File.Exists(manifestPath))
                {
                    await using var manifestStream = File.OpenRead(manifestPath);
                    oldManifest = await JsonSerializer.DeserializeAsync<ExportManifest>(manifestStream, cancellationToken: cancellationToken)
                        ?? throw new InvalidDataException("The previous export manifest is invalid.");
                    if (oldManifest.Version != 1)
                    {
                        throw new InvalidDataException($"Unsupported export manifest version {oldManifest.Version}.");
                    }
                    if (oldManifest.ProjectId != project.Id)
                    {
                        throw new InvalidOperationException("The selected export folder belongs to a different studio project.");
                    }
                }
                else
                {
                    var collisions = newFiles.Where(relativePath =>
                        File.Exists(Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)))).ToArray();
                    if (collisions.Length > 0)
                    {
                        throw new InvalidOperationException(
                            "The Gallery folder is not managed by EDI Integration Studio and already contains files this export would replace: " +
                            string.Join(", ", collisions));
                    }
                }
            }

            Directory.CreateDirectory(outputDirectory);
            foreach (var relativePath in newFiles)
            {
                var source = Path.Combine(stageDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var target = Path.Combine(outputDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var temporaryTarget = target + $".{Guid.NewGuid():N}.new";
                File.Copy(source, temporaryTarget, true);
                File.Move(temporaryTarget, target, true);
            }

            var newFileSet = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
            foreach (var stalePath in oldManifest?.Files ?? [])
            {
                if (newFileSet.Contains(stalePath)) continue;
                var fullPath = SafeManifestPath(outputDirectory, stalePath);
                if (File.Exists(fullPath)) File.Delete(fullPath);
            }
            RemoveEmptyDirectories(outputDirectory);

            var exportManifest = new ExportManifest(1, project.Id, DateTimeOffset.UtcNow, newFiles);
            var manifestTarget = Path.Combine(outputDirectory, ManifestFileName);
            var temporaryManifest = manifestTarget + $".{Guid.NewGuid():N}.new";
            await File.WriteAllTextAsync(temporaryManifest,
                JsonSerializer.Serialize(exportManifest, new JsonSerializerOptions { WriteIndented = true }), Utf8WithoutBom,
                CancellationToken.None);
            File.Move(temporaryManifest, manifestTarget, true);

            var definitionCount = project.Actions.Count + (ShouldWriteBuiltInFiller(project) ? 1 : 0);
            return new(outputDirectory, definitionCount, scriptCount, issues);
        }
        finally
        {
            if (Directory.Exists(stageDirectory)) Directory.Delete(stageDirectory, true);
        }
    }

    private static async Task<int> WritePackageAsync(
        StudioProject project,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        var definitionLines = new List<string>
        {
            "Name,FileName,StartTime,EndTime,Type,Loop,Description"
        };
        var scriptCount = 0;
        var variants = new HashSet<string>(
            project.Actions.Select(action => EdiValidator.NormalizeVariant(action.Variant)),
            StringComparer.OrdinalIgnoreCase);
        if (variants.Count == 0) variants.Add("default");

        foreach (var action in project.Actions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            definitionLines.Add(string.Join(',',
                EscapeCsv(action.Name),
                EscapeCsv(action.FileName),
                "0",
                action.DurationMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                action.Type.ToString().ToLowerInvariant(),
                action.Loop ? "true" : "false",
                EscapeCsv(action.Description)));

            var variant = EdiValidator.NormalizeVariant(action.Variant);
            var scriptDirectory = variant.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? outputDirectory
                : Path.Combine(outputDirectory, variant);
            Directory.CreateDirectory(scriptDirectory);

            foreach (var track in action.Tracks)
            {
                var suffix = track.Axis == EdiAxis.Default ? string.Empty : $".{track.Axis.ToString().ToLowerInvariant()}";
                var scriptPath = Path.Combine(scriptDirectory, $"{action.FileName}{suffix}.funscript");
                var preview = CreateFunscriptPreview(track.Points, action.DurationMilliseconds, action.Loop);
                await File.WriteAllTextAsync(scriptPath, preview.Json, Utf8WithoutBom, cancellationToken);
                scriptCount++;
            }
        }

        if (ShouldWriteBuiltInFiller(project))
        {
            definitionLines.Add(string.Join(',',
                BuiltInFillerName,
                BuiltInFillerName,
                "0",
                BuiltInFillerDurationMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "filler",
                "true",
                string.Empty));
            var filler = CreateFunscriptPreview(
                BuiltInFillerPoints, BuiltInFillerDurationMilliseconds, loop: true).Json;
            foreach (var variant in variants)
            {
                var scriptDirectory = variant.Equals("default", StringComparison.OrdinalIgnoreCase)
                    ? outputDirectory
                    : Path.Combine(outputDirectory, variant);
                Directory.CreateDirectory(scriptDirectory);
                await File.WriteAllTextAsync(
                    Path.Combine(scriptDirectory, BuiltInFillerName + ".funscript"),
                    filler,
                    Utf8WithoutBom,
                    cancellationToken);
                scriptCount++;
            }
        }

        await File.WriteAllLinesAsync(
            Path.Combine(outputDirectory, "Definitions.csv"), definitionLines, Utf8WithoutBom, cancellationToken);

        var bundlePath = Path.Combine(outputDirectory, "BundleDefinition.txt");
        if (project.Bundles.Count > 0)
        {
            var bundleText = new StringBuilder();
            foreach (var bundle in project.Bundles)
            {
                bundleText.Append('-').AppendLine(bundle.Name);
                foreach (var actionName in bundle.Actions)
                {
                    var canonicalName = project.Actions.First(action =>
                        action.Name.Equals(actionName, StringComparison.OrdinalIgnoreCase)).Name;
                    bundleText.AppendLine(canonicalName);
                }
                bundleText.AppendLine();
            }
            await File.WriteAllTextAsync(bundlePath, bundleText.ToString(), Utf8WithoutBom, cancellationToken);
        }

        return scriptCount;
    }

    private static bool ShouldWriteBuiltInFiller(StudioProject project) =>
        !project.Actions.Any(action =>
            action.Type == EdiGalleryType.Filler ||
            action.Name.Equals(BuiltInFillerName, StringComparison.OrdinalIgnoreCase) ||
            action.FileName.Equals(BuiltInFillerName, StringComparison.OrdinalIgnoreCase));

    private static string SafeManifestPath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The previous export manifest contains a path outside the export directory.");
        }
        return fullPath;
    }

    private static void RemoveEmptyDirectories(string root)
    {
        foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    public static IReadOnlyList<FunscriptPoint> PreparePoints(
        IEnumerable<FunscriptPoint> source,
        int durationMilliseconds,
        bool loop)
    {
        var points = source
            .Where(point => point.At >= 0 && point.At <= durationMilliseconds)
            .Select(point => new FunscriptPoint(point.At, Math.Clamp(point.Pos, 0, 100)))
            .GroupBy(point => point.At)
            .Select(group => group.Last())
            .OrderBy(point => point.At)
            .ToList();
        if (points.Count == 0)
        {
            return [];
        }

        if (points[0].At != 0)
        {
            points.Insert(0, new(0, points[0].Pos));
        }

        var finalPosition = loop ? points[0].Pos : points[^1].Pos;
        if (points[^1].At == durationMilliseconds)
        {
            points[^1] = new(durationMilliseconds, finalPosition);
        }
        else
        {
            points.Add(new(durationMilliseconds, finalPosition));
        }

        return points;
    }

    public static FunscriptPreview CreateFunscriptPreview(
        IEnumerable<FunscriptPoint> source,
        int durationMilliseconds,
        bool loop)
    {
        ArgumentNullException.ThrowIfNull(source);
        var authored = source.ToArray();
        var prepared = PreparePoints(authored, durationMilliseconds, loop);
        var orderedAuthored = authored
            .Where(point => point.At >= 0 && point.At <= durationMilliseconds)
            .OrderBy(point => point.At)
            .ToArray();
        var boundaryInserted = orderedAuthored.Length > 0 &&
                               (orderedAuthored[0].At != 0 || orderedAuthored[^1].At != durationMilliseconds);
        var loopClosureApplied = loop && orderedAuthored.Length > 0 &&
                                 (orderedAuthored[^1].At != durationMilliseconds ||
                                  orderedAuthored[^1].Pos != orderedAuthored[0].Pos);
        var loopClosureInterval = loopClosureApplied
            ? Math.Max(0, durationMilliseconds - orderedAuthored[^1].At)
            : (int?)null;
        var document = new FunscriptDocument
        {
            Actions = prepared.Select(point => new FunscriptAction { At = point.At, Pos = point.Pos }).ToList()
        };
        return new(authored.Length, prepared.Count, durationMilliseconds, boundaryInserted, loopClosureApplied, loopClosureInterval,
            JsonSerializer.Serialize(document, ScriptJsonOptions));
    }

    private static string EscapeCsv(string? value)
    {
        value ??= string.Empty;
        if (value.IndexOfAny([',', '"', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private sealed class FunscriptDocument
    {
        public string Version { get; set; } = "1.0";
        public bool Inverted { get; set; }
        public int Range { get; set; } = 100;
        public List<FunscriptAction> Actions { get; set; } = [];
    }

    private sealed class FunscriptAction
    {
        public int At { get; set; }
        public int Pos { get; set; }
    }

    private sealed record ExportManifest(int Version, Guid ProjectId, DateTimeOffset ExportedAt, string[] Files);
}
