using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace GTracker.Core.Unity;

public sealed record UnityModBuildResult(
    bool Success,
    int ExitCode,
    string Output,
    string AssemblyPath,
    UnityScaffoldManifest Manifest);

public sealed record UnityModInstallResult(string PluginPath, string OwnershipManifestPath);

public sealed class UnityModDeployer
{
    private const string OwnershipManifestFileName = "edi-integration-studio.install.json";

    public async Task<UnityModBuildResult> BuildAsync(string projectDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        projectDirectory = Path.GetFullPath(projectDirectory);
        var projectFile = Path.Combine(projectDirectory, "IntegrationMod.csproj");
        if (!File.Exists(projectFile)) throw new FileNotFoundException("IntegrationMod.csproj was not found.", projectFile);
        var manifest = await LoadManifestAsync(projectDirectory, cancellationToken);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = projectDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectFile);
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--nologo");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the .NET SDK build process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }

        var output = (await standardOutput + Environment.NewLine + await standardError).Trim();
        var assemblyPath = Path.Combine(projectDirectory, "bin", "Release", manifest.TargetFramework,
            manifest.AssemblyName + ".dll");
        var success = process.ExitCode == 0 && File.Exists(assemblyPath);
        if (process.ExitCode == 0 && !File.Exists(assemblyPath))
            output += $"{Environment.NewLine}Build completed but the expected plugin was not found: {assemblyPath}";
        return new(success, process.ExitCode, output, assemblyPath, manifest);
    }

    public UnityModInstallResult Install(UnityModBuildResult build)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (!build.Success || !File.Exists(build.AssemblyPath))
            throw new InvalidOperationException("A successful mod build is required before installation.");

        var manifest = build.Manifest;
        if (!File.Exists(manifest.GameExecutable))
            throw new FileNotFoundException("The target game executable no longer exists.", manifest.GameExecutable);
        var processName = Path.GetFileNameWithoutExtension(manifest.GameExecutable);
        var running = Process.GetProcessesByName(processName);
        try
        {
            if (running.Length > 0) throw new InvalidOperationException("Close the target game before installing the plugin.");
        }
        finally
        {
            foreach (var process in running) process.Dispose();
        }

        var coreDirectory = Path.Combine(manifest.GameRoot, "BepInEx", "core");
        if (!Directory.Exists(coreDirectory))
            throw new DirectoryNotFoundException("BepInEx\\core was not found. Install BepInEx and run the game once first.");
        var pluginDirectory = Path.Combine(manifest.GameRoot, "BepInEx", "plugins", manifest.PluginGuid);
        Directory.CreateDirectory(pluginDirectory);
        var destination = Path.Combine(pluginDirectory, manifest.AssemblyName + ".dll");
        var ownershipPath = Path.Combine(pluginDirectory, OwnershipManifestFileName);
        if (File.Exists(destination) && !File.Exists(ownershipPath))
            throw new IOException("Refusing to replace an existing plugin that is not owned by EDI Integration Studio.");
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(build.AssemblyPath, temporary, overwrite: false);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        var ownership = new
        {
            manifest.PluginGuid,
            manifest.AssemblyName,
            pluginPath = destination,
            sourceProject = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(build.AssemblyPath)))),
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination))),
            installedAt = DateTimeOffset.UtcNow
        };
        File.WriteAllText(ownershipPath, JsonSerializer.Serialize(ownership, new JsonSerializerOptions { WriteIndented = true }));
        return new(destination, ownershipPath);
    }

    public static async Task<UnityScaffoldManifest> LoadManifestAsync(
        string projectDirectory,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetFullPath(projectDirectory), UnityModScaffolder.ManifestFileName);
        if (!File.Exists(path)) throw new FileNotFoundException("The generated scaffold manifest was not found.", path);
        await using var stream = File.OpenRead(path);
        var manifest = await JsonSerializer.DeserializeAsync<UnityScaffoldManifest>(stream,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
        if (manifest is null || string.IsNullOrWhiteSpace(manifest.TargetFramework) ||
            string.IsNullOrWhiteSpace(manifest.PluginGuid) || string.IsNullOrWhiteSpace(manifest.GameRoot))
            throw new InvalidDataException("The generated scaffold manifest is incomplete.");
        return manifest;
    }
}
