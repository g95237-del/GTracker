using System.Text;
using GTracker.Core.Godot;
using GTracker.Core.Projects;

namespace GTracker.Core.Tests;

public sealed class GodotDiscoveryProvisionerTests
{
    [Fact]
    public void RuntimeCompiler_ExpandsMappingsIntoProjectSpeedVariants()
    {
        var action = new AuthoredAction
        {
            Name = "Boss1 - p1fin",
            FileName = "boss1-p1fin",
            DurationMilliseconds = 2250
        };
        var project = new StudioProject
        {
            SpeedMultipliers = [0.75, 1, 2],
            Actions = [action]
        };
        project.Game.SetTriggerMapping(UnityTriggerKind.AnimationClip, "p1fin", action.Name,
            "/root/Gallery/Units/Boss1/AnimationPlayer", 2250, "res://Gallery.tscn");

        var runtime = GodotRuntimeConfigurationCompiler.Create(project, "http://127.0.0.1:5000/Edi");

        Assert.Equal(3, runtime.Mappings.Count);
        var slow = Assert.Single(runtime.Mappings, mapping => mapping.ActionName.EndsWith("[0.75x]"));
        Assert.Equal(3000, slow.CycleDurationMilliseconds);
        Assert.Equal(3000, slow.ActionDurationMilliseconds);
        Assert.True(slow.AllowNearestDuration);
        var fast = Assert.Single(runtime.Mappings, mapping => mapping.ActionName.EndsWith("[2x]"));
        Assert.Equal(1125, fast.CycleDurationMilliseconds);
        Assert.Equal(1125, fast.ActionDurationMilliseconds);
    }

    [Fact]
    public void InstallAndUninstall_FreshTargetIsReversibleAndPreservesTelemetry()
    {
        var directory = CreateGodot3Target();
        try
        {
            var executable = Path.Combine(directory, "Game.exe");
            var provisioner = new GodotDiscoveryProvisioner();

            var result = provisioner.Install(executable);

            Assert.False(result.ReplacedExistingOverride);
            Assert.True(File.Exists(result.OverrideConfigPath));
            Assert.True(File.Exists(result.ScriptPath));
            Assert.True(File.Exists(result.TelemetryPath));
            Assert.True(File.Exists(result.ManifestPath));
            var hotkeyConfigPath = Path.Combine(directory, "GTrackerRuntime", "Godot", "hotkeys.cfg");
            Assert.True(File.Exists(hotkeyConfigPath));
            var hotkeyConfig = File.ReadAllText(hotkeyConfigPath);
            Assert.Contains("Pause=\"1 | NumPad1\"", hotkeyConfig);
            Assert.Contains("ActivateFiller=\"5 | NumPad5\"", hotkeyConfig);
            var config = File.ReadAllText(result.OverrideConfigPath);
            Assert.Contains("[autoload]", config);
            Assert.Contains("GTrackerDiscovery=\"*", config);
            Assert.Contains(result.ScriptPath.Replace('\\', '/'), config);
            var script = File.ReadAllText(result.ScriptPath);
            Assert.Contains("extends Node", script);
            Assert.Contains("ANIMATION_START", script);
            Assert.Contains("get_playing_speed()", script);
            Assert.Contains("func _wrapped", script);
            Assert.Contains("telemetry.tsv", script);
            Assert.Contains("hotkeys.cfg", script);
            Assert.Contains("Input.is_key_pressed", script);
            Assert.Contains("OS.is_window_focused()", script);

            provisioner.Uninstall(executable);

            Assert.False(File.Exists(result.OverrideConfigPath));
            Assert.False(File.Exists(result.ScriptPath));
            Assert.False(File.Exists(result.ManifestPath));
            Assert.True(File.Exists(result.TelemetryPath));
            Assert.True(File.Exists(hotkeyConfigPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Uninstall_RestoresExistingOverrideByteForByte()
    {
        var directory = CreateGodot3Target();
        try
        {
            var executable = Path.Combine(directory, "Game.exe");
            var overridePath = Path.Combine(directory, "override.cfg");
            var original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
                "[display]\r\nwindow/size/width=1280\r\n\r\n[autoload]\r\nOther=\"*res://other.gd\"\r\n")).ToArray();
            File.WriteAllBytes(overridePath, original);
            var provisioner = new GodotDiscoveryProvisioner();

            var result = provisioner.Install(executable);
            Assert.True(result.ReplacedExistingOverride);
            Assert.Contains("Other=", File.ReadAllText(overridePath));

            provisioner.Uninstall(executable);

            Assert.Equal(original, File.ReadAllBytes(overridePath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Install_RefusesUnownedAutoloadCollision()
    {
        var directory = CreateGodot3Target();
        try
        {
            var executable = Path.Combine(directory, "Game.exe");
            File.WriteAllText(Path.Combine(directory, "override.cfg"),
                "[autoload]\nGTrackerDiscovery=\"*res://foreign.gd\"\n");

            var exception = Assert.Throws<IOException>(() => new GodotDiscoveryProvisioner().Install(executable));

            Assert.Contains("unowned", exception.Message);
            Assert.False(Directory.Exists(Path.Combine(directory, "GTrackerRuntime")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Uninstall_RefusesToOverwritePostInstallOverrideEdits()
    {
        var directory = CreateGodot3Target();
        try
        {
            var executable = Path.Combine(directory, "Game.exe");
            var provisioner = new GodotDiscoveryProvisioner();
            var result = provisioner.Install(executable);
            File.AppendAllText(result.OverrideConfigPath, "[foreign]\nvalue=true\n");

            var exception = Assert.Throws<IOException>(() => provisioner.Uninstall(executable));

            Assert.Contains("changed after installation", exception.Message);
            Assert.True(File.Exists(result.ScriptPath));
            Assert.True(File.Exists(result.ManifestPath));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Reinstall_PreservesTelemetryFromPreviousInstallation()
    {
        var directory = CreateGodot3Target();
        try
        {
            var executable = Path.Combine(directory, "Game.exe");
            var provisioner = new GodotDiscoveryProvisioner();
            var first = provisioner.Install(executable);
            File.WriteAllText(first.TelemetryPath, "preserved telemetry\n");
            provisioner.Uninstall(executable);

            var second = provisioner.Install(executable);

            Assert.Equal("preserved telemetry\n", File.ReadAllText(second.TelemetryPath).Replace("\r\n", "\n"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Reinstall_PreservesEditedHotkeyConfiguration()
    {
        var directory = CreateGodot3Target();
        try
        {
            var executable = Path.Combine(directory, "Game.exe");
            var hotkeyConfigPath = Path.Combine(directory, "GTrackerRuntime", "Godot", "hotkeys.cfg");
            var provisioner = new GodotDiscoveryProvisioner();
            provisioner.Install(executable);
            File.WriteAllText(hotkeyConfigPath, "[Hotkeys]\nPause=\"F6\"\n");
            provisioner.Uninstall(executable);

            provisioner.Install(executable);

            Assert.Equal("[Hotkeys]\nPause=\"F6\"\n", File.ReadAllText(hotkeyConfigPath).Replace("\r\n", "\n"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void UpdateRuntime_CompilesMappingsWithoutChangingOverrideOrTelemetry()
    {
        var directory = CreateGodot3Target();
        try
        {
            var executable = Path.Combine(directory, "Game.exe");
            var provisioner = new GodotDiscoveryProvisioner();
            var installed = provisioner.Install(executable);
            var originalOverride = File.ReadAllBytes(installed.OverrideConfigPath);
            File.WriteAllText(installed.TelemetryPath, "preserved\n");
            var hotkeyConfigPath = Path.Combine(directory, "GTrackerRuntime", "Godot", "hotkeys.cfg");
            File.Delete(hotkeyConfigPath); // Simulate an installation created before hotkey support.
            var runtime = new GodotRuntimeConfiguration("http://127.0.0.1:5000/Edi/",
            [
                new(UnityTriggerKind.Scene, "res://Main Menu.tscn", "menu action", "", null, false),
                new(UnityTriggerKind.AnimationClip, "Idle", "idle/action", "/root/Main/Player", 1200, false,
                    "res://scenes/Battle.tscn", true),
                new(UnityTriggerKind.AnimationClip, "p1fin", "Maid - p1fin", "/root/Gallery/Units/Maid/AnimationPlayer",
                    1050, false, "res://Gallery.tscn")
            ]);

            provisioner.UpdateRuntime(executable, runtime);

            var script = File.ReadAllText(installed.ScriptPath);
            var status = provisioner.GetRuntimeStatus(executable);
            Assert.Contains("http://127.0.0.1:5000/Edi", script);
            Assert.Contains("resmainmenutscn", script);
            Assert.Contains("menu action", script);
            Assert.Contains("root/Main/Player", script);
            Assert.Contains("resscenesbattletscn", script);
            Assert.Contains("mapping.scene == scene", script);
            Assert.Contains("\"action_loop\": true", script);
            Assert.Contains("ANIMATION_RESTART", script);
            Assert.Contains("wrapped and already_active and mapping.action_loop", script);
            Assert.Contains("HTTPRequest.new()", script);
            Assert.Contains("HTTPClient.METHOD_POST", script);
            Assert.Contains("OS.get_system_time_msecs()", script);
            Assert.Contains("length / rate", script);
            Assert.Contains("func _resume_runtime", script);
            Assert.Contains("func _queue_play", script);
            Assert.Contains("\"portable\": true", script);
            Assert.Contains("\"portable\": false", script);
            Assert.Contains("\"owner\": \"maid\"", script);
            Assert.Contains("func _portable_animation_name", script);
            Assert.Contains("func _index_animation_mappings", script);
            Assert.Contains("_exact_mapping_index.get", script);
            Assert.Contains("_portable_mapping_index.get", script);
            Assert.DoesNotContain("for mapping in _animation_mappings:\n        var mapped_path", script);
            Assert.Contains("const TELEMETRY_UPDATE_SECONDS = 1.0", script);
            Assert.Contains("func _flush_telemetry", script);
            Assert.Contains("_telemetry_lines.append", script);
            Assert.Contains("func _owner_resource", script);
            Assert.Contains("ownerResource=", script);
            Assert.Contains("_owner_matches(mapping.owner, owner)", script);
            Assert.Contains("\"action_duration\":", script);
            Assert.Contains("\"nearest_duration\":", script);
            Assert.Contains("func _scaled_seek", script);
            Assert.Contains("animation-speed-change", script);
            Assert.Contains("func _poll_hotkeys", script);
            Assert.Contains("func _parse_hotkey", script);
            Assert.Contains("/Pause?untilResume=true", script);
            Assert.Contains("/Resume?AtCurrentTime=false", script);
            Assert.Contains("/Intensity/40", script);
            Assert.Contains("_play(\"filler\", 0, \"hotkey\")", script);
            Assert.Equal(3, status.MappingCount);
            Assert.NotNull(status.UpdatedAt);
            Assert.Equal(originalOverride, File.ReadAllBytes(installed.OverrideConfigPath));
            Assert.Equal("preserved\n", File.ReadAllText(installed.TelemetryPath).Replace("\r\n", "\n"));
            Assert.Contains("Pause=\"1 | NumPad1\"", File.ReadAllText(hotkeyConfigPath));

            provisioner.Uninstall(executable);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateGodot3Target()
    {
        var directory = Path.Combine(Path.GetTempPath(), "EdiIntegrationStudio.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "Game.exe");
        File.Copy(Environment.ProcessPath!, executable);
        WritePack(Path.Combine(directory, "Game.pck"));
        return directory;
    }

    private static void WritePack(string path)
    {
        var entries = new[] { "project.binary", "main.gd" };
        var directorySize = sizeof(uint) + entries.Sum(entry =>
            sizeof(uint) + Encoding.UTF8.GetByteCount(entry) + sizeof(ulong) * 2 + 16);
        var payloadOffset = 84 + directorySize;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        writer.Write(0x43504447u);
        writer.Write(1u);
        writer.Write(3u);
        writer.Write(5u);
        writer.Write(1u);
        writer.Write(new byte[64]);
        writer.Write((uint)entries.Length);
        foreach (var entry in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(entry);
            writer.Write((uint)bytes.Length);
            writer.Write(bytes);
            writer.Write((ulong)payloadOffset);
            writer.Write(1ul);
            writer.Write(new byte[16]);
        }
        writer.Write((byte)0);
    }
}
