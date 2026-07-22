using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GTracker.App.Capture;
using GTracker.App.Controls;
using GTracker.App.Edi;
using GTracker.App.Projects;
using GTracker.Core.Edi;
using GTracker.Core.Projects;
using GTracker.Core.Unity;
using Microsoft.Win32;

namespace GTracker.App;

public partial class MainWindow : Window
{
    private const int CaptureHotkeyId = 0x4544;
    private const int StopHotkeyId = 0x4545;
    private const int MaximumFollowedTelemetryEntries = 1000;
    private const string MonoBepInExPackage = "BepInEx-Unity.Mono-win-x64-6.0.0-be.785+6abdba4.zip";
    private const string MonoBepInExSha256 = "DB430F14D6661EB38BA96FCC13C07A163E87E553710821D87E5129F915A1B26B";
    private const string Il2CppBepInExPackage = "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785+6abdba4.zip";
    private const string Il2CppBepInExSha256 = "2A7CBF74D26ABE4765C3E662DB1721B923BAC39849EBFEF2CA5DC7DE7E2D9B7F";
    private readonly ProjectStore _projectStore = new();
    private readonly ClipArchive _clipArchive = new();
    private readonly RecentProjectStore _recentProjectStore = new();
    private readonly EdiValidator _validator = new();
    private readonly EdiExporter _exporter = new();
    private readonly EdiApiClient _ediApi = new();
    private readonly UnityGameInspector _gameInspector = new();
    private readonly UnityModScaffolder _modScaffolder = new();
    private readonly UnityModDeployer _modDeployer = new();
    private readonly UnityRuntimeProvisioner _runtimeProvisioner = new();
    private readonly ObservableCollection<UnityTelemetryEntry> _telemetryEntries = [];
    private readonly ObservableCollection<RecentProjectItem> _recentProjectItems = [];
    private readonly DispatcherTimer _clipTimer;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly Stopwatch _clipPlaybackClock = new();
    private readonly Stopwatch _captureRateClock = Stopwatch.StartNew();
    private readonly SemaphoreSlim _projectOperationGate = new(1, 1);
    private readonly SemaphoreSlim _projectSwitchGate = new(1, 1);
    private readonly SemaphoreSlim _captureLifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _unityOperationGate = new(1, 1);
    private readonly Dictionary<EdiAxis, List<FunscriptPoint>> _workingTracks = [];
    private readonly HashSet<string> _suppressedTelemetryKinds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _suppressedTelemetryStreams = new(StringComparer.OrdinalIgnoreCase);
    private StudioProject _project = new();
    private string? _projectDirectory;
    private CaptureSession? _captureSession;
    private CapturedClip? _workingClip;
    private UnityInspectionResult? _inspection;
    private CancellationTokenSource? _actionLoadCancellation;
    private CancellationTokenSource? _captureActionCancellation;
    private CancellationTokenSource? _unitySetupCancellation;
    private JpegFrame? _latestPreviewFrame;
    private EdiAxis _selectedAxis = EdiAxis.Default;
    private Guid? _editingActionId;
    private DateTimeOffset? _workingClipStartedAtUtc;
    private DateTimeOffset? _workingClipEndedAtUtc;
    private string _correlatedUnityScene = string.Empty;
    private string _correlatedUnityAnimation = string.Empty;
    private IReadOnlyList<string> _correlatedAnimationCandidates = [];
    private UnityTriggerMapping? _pendingCapturedTriggerMapping;
    private int _trimInMilliseconds;
    private int _trimOutMilliseconds;
    private int _playFromMilliseconds;
    private int _previewUpdatePending;
    private int _captureActionRunning;
    private int _captureStartRunning;
    private int _projectTransitionRunning;
    private long _lastCapturedFrames;
    private long _lastEncodedFrames;
    private bool _reviewMode;
    private bool _updatingUi;
    private bool _closing;
    private bool _telemetryOutputPaused;
    private bool _telemetryFollowTail = true;
    private bool _updatingRecentProjects;
    private int _projectGeneration;
    private int _telemetryLineCount;
    private string _watchedTelemetryPath = string.Empty;
    private double _playbackRate = 1;

    public MainWindow()
    {
        InitializeComponent();
        SimulatorOverlay.LayoutChanged += SimulatorOverlay_LayoutChanged;
        ActionTypeCombo.ItemsSource = Enum.GetValues<EdiGalleryType>();
        AxisCombo.ItemsSource = Enum.GetValues<EdiAxis>();
        ModPresetCombo.ItemsSource = Enum.GetValues<UnityModPresetKind>();
        CaptureFpsCombo.ItemsSource = new[] { 20, 30 };
        UnityTelemetryList.ItemsSource = _telemetryEntries;
        RecentProjectsCombo.ItemsSource = _recentProjectItems;
        ActionTypeCombo.SelectedItem = EdiGalleryType.Gallery;
        AxisCombo.SelectedItem = EdiAxis.Default;
        ModPresetCombo.SelectedItem = UnityModPresetKind.Discovery;
        CaptureFpsCombo.SelectedItem = 30;
        UpdateRecentProjectItems([]);
        Timeline.PointsChanged += (_, _) => UpdatePointCount();
        Timeline.CursorChanged += (_, milliseconds) => SeekCursor(milliseconds);
        _clipTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render,
            (_, _) => AdvanceClipPlayback(), Dispatcher);
        _telemetryTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => RefreshUnityTelemetry(), Dispatcher);
        UpdatePresetDescription();
        UpdateTriggerMappingStatus();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        RegisterGlobalHotkeys();
        ResetActionEditor();
        try
        {
            await RestoreMostRecentProjectAsync();
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (Exception exception)
        {
            SetStatus($"Recent projects could not be restored: {exception.Message}", true);
        }
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the parent folder for the new EDI integration project" };
        if (dialog.ShowDialog(this) != true) return;

        var name = string.IsNullOrWhiteSpace(ProjectNameText.Text) ? "Studio Integration" : ProjectNameText.Text.Trim();
        var directory = Path.Combine(dialog.FolderName, SafeDirectoryName(name));
        if (File.Exists(Path.Combine(directory, ProjectStore.ProjectFileName)))
        {
            SetStatus("A project already exists in that folder. Use Open instead.", true);
            return;
        }

        await _projectSwitchGate.WaitAsync();
        try
        {
            var projectDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var project = new StudioProject { Name = name };
            Directory.CreateDirectory(Path.Combine(projectDirectory, "clips"));
            await _projectStore.SaveAsync(projectDirectory, project);
            await ActivateProjectAsync(project, projectDirectory);
            await RememberRecentProjectAsync(projectDirectory);
            SetStatus($"Created project '{name}'.");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not create project: {exception.Message}", true);
        }
        finally
        {
            _projectSwitchGate.Release();
        }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = $"Choose a folder containing {ProjectStore.ProjectFileName}" };
        if (dialog.ShowDialog(this) != true) return;
        await OpenProjectDirectoryAsync(dialog.FolderName, removeInvalidRecentEntry: false);
    }

    private async void RecentProjectsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingRecentProjects || RecentProjectsCombo.SelectedItem is not RecentProjectItem item ||
            string.IsNullOrWhiteSpace(item.Directory)) return;
        _updatingRecentProjects = true;
        RecentProjectsCombo.SelectedIndex = 0;
        _updatingRecentProjects = false;
        await OpenProjectDirectoryAsync(item.Directory, removeInvalidRecentEntry: true);
    }

    private async Task<bool> OpenProjectDirectoryAsync(string directory, bool removeInvalidRecentEntry)
    {
        await _projectSwitchGate.WaitAsync();
        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            var loadedProject = await _projectStore.LoadAsync(normalized);
            if (_closing) return false;
            await ActivateProjectAsync(loadedProject, normalized);
            await RememberRecentProjectAsync(normalized);
            SetStatus($"Opened '{_project.Name}' with {_project.Actions.Count} authored scene(s).");
            return true;
        }
        catch (Exception exception)
        {
            if (removeInvalidRecentEntry) await ForgetRecentProjectAsync(directory);
            SetStatus($"Could not open project: {exception.Message}", true);
            return false;
        }
        finally
        {
            _projectSwitchGate.Release();
        }
    }

    private async Task RestoreMostRecentProjectAsync()
    {
        var paths = await _recentProjectStore.LoadAsync();
        var validProjects = new List<(string Directory, StudioProject Project)>();
        foreach (var path in paths)
        {
            if (_closing) return;
            try
            {
                if (!File.Exists(Path.Combine(path, ProjectStore.ProjectFileName))) continue;
                validProjects.Add((path, await _projectStore.LoadAsync(path)));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
            }
        }

        await _projectSwitchGate.WaitAsync();
        try
        {
            if (_projectDirectory is not null || _closing)
            {
                UpdateRecentProjectItems(await _recentProjectStore.LoadAsync());
                return;
            }
            var validPaths = validProjects.Select(item => item.Directory).ToArray();
            try { await _recentProjectStore.SaveAsync(validPaths); } catch (Exception) { }
            UpdateRecentProjectItems(validPaths);
            if (validProjects.Count == 0) return;
            await ActivateProjectAsync(validProjects[0].Project, validProjects[0].Directory);
            SetStatus($"Opened most recent project '{_project.Name}'.");
        }
        finally
        {
            _projectSwitchGate.Release();
        }
    }

    private async Task RememberRecentProjectAsync(string directory)
    {
        try
        {
            await _recentProjectStore.RememberAsync(directory);
            UpdateRecentProjectItems(await _recentProjectStore.LoadAsync());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private async Task ForgetRecentProjectAsync(string directory)
    {
        try
        {
            await _recentProjectStore.RemoveAsync(directory);
            UpdateRecentProjectItems(await _recentProjectStore.LoadAsync());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private void UpdateRecentProjectItems(IEnumerable<string> paths)
    {
        _updatingRecentProjects = true;
        _recentProjectItems.Clear();
        _recentProjectItems.Add(new("Recent projects...", string.Empty));
        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            _recentProjectItems.Add(new($"{(string.IsNullOrWhiteSpace(name) ? path : name)}  |  {path}", path));
        }
        RecentProjectsCombo.SelectedIndex = 0;
        RecentProjectsCombo.IsEnabled = _recentProjectItems.Count > 1;
        _updatingRecentProjects = false;
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        await SaveProjectAsync();
    }

    private async Task<bool> SaveProjectAsync()
    {
        var projectGeneration = Volatile.Read(ref _projectGeneration);
        if (Volatile.Read(ref _projectTransitionRunning) != 0) return false;
        if (_projectDirectory is null)
        {
            SetStatus("Create or open a project first.", true);
            return false;
        }

        var lockTaken = false;
        try
        {
            await _projectOperationGate.WaitAsync();
            lockTaken = true;
            if (projectGeneration != Volatile.Read(ref _projectGeneration) ||
                Volatile.Read(ref _projectTransitionRunning) != 0) return false;
            return await SaveProjectWhileLockedAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"Could not save project: {exception.Message}", true);
            return false;
        }
        finally
        {
            if (lockTaken) _projectOperationGate.Release();
        }
    }

    private async Task<bool> SaveProjectWhileLockedAsync()
    {
        if (_projectDirectory is null) return false;
        SyncSimulatorLayout();
        _project.Name = string.IsNullOrWhiteSpace(ProjectNameText.Text) ? _project.Name : ProjectNameText.Text.Trim();
        _project.Game.ExecutablePath = GameExecutableText.Text.Trim();
        if (ModPresetCombo.SelectedItem is UnityModPresetKind preset) _project.Game.ModPreset = preset;
        await _projectStore.SaveAsync(_projectDirectory, _project);
        ProjectPathText.Text = _projectDirectory;
        SetStatus("Project saved.");
        return true;
    }

    private async Task ActivateProjectAsync(StudioProject project, string projectDirectory)
    {
        await _projectOperationGate.WaitAsync();
        Interlocked.Exchange(ref _projectTransitionRunning, 1);
        try
        {
            Interlocked.Increment(ref _projectGeneration);
            _actionLoadCancellation?.Cancel();
            _actionLoadCancellation?.Dispose();
            _actionLoadCancellation = null;
            await StopCaptureAsync();
            _clipTimer.Stop();
            StopTelemetryWatch(clearOutput: true);
            _clipPlaybackClock.Reset();
            _workingClip = null;
            _workingClipStartedAtUtc = null;
            _workingClipEndedAtUtc = null;
            _correlatedUnityScene = string.Empty;
            _correlatedUnityAnimation = string.Empty;
            _correlatedAnimationCandidates = [];
            _pendingCapturedTriggerMapping = null;
            _inspection = null;
            _editingActionId = null;
            _reviewMode = false;
            _latestPreviewFrame = null;
            PreviewImage.Source = null;
            PreviewModeText.Text = "LIVE PREVIEW";
            _project = project;
            _projectDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDirectory));
            ApplyProjectToUi();
        }
        finally
        {
            Interlocked.Exchange(ref _projectTransitionRunning, 0);
            _projectOperationGate.Release();
        }
    }

    private void ApplyProjectToUi()
    {
        _updatingUi = true;
        _workingClip = null;
        _inspection = null;
        _reviewMode = false;
        ProjectNameText.Text = _project.Name;
        ProjectPathText.Text = _projectDirectory ?? "No project folder selected";
        GameExecutableText.Text = _project.Game.ExecutablePath;
        UnityStatusText.Text = _project.Game.Runtime == UnityRuntimeKind.Unknown
            ? "Not analyzed"
            : $"Unity {_project.Game.Runtime} / {_project.Game.Architecture} / {_project.Game.TargetFramework}";
        ModPresetCombo.SelectedItem = _project.Game.ModPreset;
        UpdatePresetDescription();
        UpdateTriggerMappingStatus();
        SimulatorOverlay.ApplyLayout(_project.Game.Simulator);
        SimulatorCheck.IsChecked = _project.Game.Simulator.IsVisible;
        SimulatorOverlay.Visibility = _project.Game.Simulator.IsVisible ? Visibility.Visible : Visibility.Collapsed;
        RefreshActionList();
        _updatingUi = false;
        ResetActionEditor();
    }

    private void RefreshActionList(Guid? selectId = null)
    {
        _updatingUi = true;
        var previousTelemetryAction = (TelemetryActionCombo.SelectedItem as AuthoredAction)?.Name;
        var orderedActions = _project.Actions.OrderBy(action => action.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        ActionList.ItemsSource = null;
        ActionList.ItemsSource = orderedActions;
        ActionList.SelectedItem = selectId is null ? null : _project.Actions.FirstOrDefault(action => action.Id == selectId);
        TelemetryActionCombo.ItemsSource = orderedActions;
        TelemetryActionCombo.SelectedItem = orderedActions.FirstOrDefault(action =>
            action.Name.Equals(previousTelemetryAction, StringComparison.OrdinalIgnoreCase)) ?? orderedActions.FirstOrDefault();
        _updatingUi = false;
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void RefreshWindows(WindowInfo? preferred = null)
    {
        var previousHandle = (WindowCombo.SelectedItem as WindowInfo)?.Handle;
        var windows = WindowCatalog.GetWindows().ToList();
        if (preferred is not null && windows.All(window => window.Handle != preferred.Handle)) windows.Add(preferred);
        windows = windows.OrderBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        var configuredExecutable = GameExecutableText.Text.Trim();
        WindowCombo.ItemsSource = windows;
        var configuredWindow = windows
            .Where(window => !string.IsNullOrWhiteSpace(configuredExecutable) &&
                             window.ExecutablePath.Equals(configuredExecutable, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(window => window.IsProcessMainWindow)
            .ThenBy(window => window.IsMinimized)
            .FirstOrDefault();
        WindowCombo.SelectedItem = preferred is not null
            ? windows.FirstOrDefault(window => window.Handle == preferred.Handle)
            : configuredWindow ?? windows.FirstOrDefault(window => window.Handle == previousHandle) ?? windows.FirstOrDefault();
    }

    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowCombo.SelectedItem is WindowInfo { ExecutablePath.Length: > 0 } window)
        {
            GameExecutableText.Text = window.ExecutablePath;
            _inspection = null;
            UnityStatusText.Text = "Not analyzed";
        }
    }

    private async void StartCapture_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        if (WindowCombo.SelectedItem is not WindowInfo window)
        {
            SetStatus("No game window was found. Start the game, or press Ctrl+Shift+F8 while it is fullscreen.", true);
            return;
        }

        await StartCaptureForWindowAsync(window, activateWindow: true);
    }

    private async Task<bool> StartCaptureForWindowAsync(WindowInfo window, bool activateWindow)
    {
        if (_closing || Volatile.Read(ref _projectTransitionRunning) != 0) return false;
        if (Interlocked.Exchange(ref _captureStartRunning, 1) != 0) return false;
        await _captureLifecycleGate.WaitAsync();
        try
        {
            if (_closing || Volatile.Read(ref _projectTransitionRunning) != 0) return false;
            _captureActionCancellation?.Cancel();
            await StopCaptureCoreAsync();
            StartCaptureButton.IsEnabled = false;
            var retentionSeconds = Math.Max(120, ParseSeconds(PreRollText.Text, 12, 1, 300) + ParseSeconds(PostRollText.Text, 3, 0, 60) + 15);
            var recordingFps = CaptureFpsCombo.SelectedItem is int selectedFps ? selectedFps : 30;
            var session = new CaptureSession(window.Handle, TimeSpan.FromSeconds(retentionSeconds), recordingFps);
            session.FrameEncoded += CaptureSession_FrameEncoded;
            session.Faulted += CaptureSession_Faulted;
            _captureSession = session;
            session.Start();
            StartCaptureButton.IsEnabled = false;
            CaptureFpsCombo.IsEnabled = false;
            _lastCapturedFrames = 0;
            _lastEncodedFrames = 0;
            _captureRateClock.Restart();
            if (!string.IsNullOrWhiteSpace(window.ExecutablePath)) GameExecutableText.Text = window.ExecutablePath;
            var activated = !activateWindow || WindowCatalog.RestoreAndActivate(window.Handle);
            SetStatus(activated
                ? $"Rolling capture started for '{window.Title}'. Encoded frames remain memory-only until a scene is saved."
                : $"Rolling capture started for '{window.Title}', but Windows blocked automatic focus. Return to the game to record frames.");
            return true;
        }
        catch (Exception exception)
        {
            _captureActionCancellation?.Cancel();
            await StopCaptureCoreAsync();
            SetStatus($"Capture failed: {exception.Message}", true);
            return false;
        }
        finally
        {
            _captureLifecycleGate.Release();
            Interlocked.Exchange(ref _captureStartRunning, 0);
        }
    }

    private async Task StartOrCaptureFromHotkeyAsync()
    {
        if (_closing || Volatile.Read(ref _projectTransitionRunning) != 0) return;
        if (_captureSession is not null)
        {
            await CaptureActionAsync();
            return;
        }

        var window = WindowCatalog.GetForegroundWindowInfo();
        if (window is null || window.ProcessId == (uint)Environment.ProcessId)
        {
            RefreshWindows();
            window = WindowCombo.SelectedItem as WindowInfo;
        }
        if (window is null)
        {
            SetStatus("Could not identify the foreground game window for rolling capture.", true);
            return;
        }

        RefreshWindows(window);
        if (await StartCaptureForWindowAsync(window, activateWindow: false))
            SetStatus($"Rolling capture started for foreground window '{window.Title}'. Press Ctrl+Shift+F8 again to capture a scene.");
    }

    private void CaptureSession_FrameEncoded(object? sender, JpegFrame frame)
    {
        if (!ReferenceEquals(sender, _captureSession)) return;
        Interlocked.Exchange(ref _latestPreviewFrame, frame);
        if (Interlocked.CompareExchange(ref _previewUpdatePending, 1, 0) != 0) return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!_reviewMode && _latestPreviewFrame is { } latest)
                {
                    PreviewImage.Source = CreateBitmap(latest.Data);
                    PreviewModeText.Text = "LIVE PREVIEW";
                }
                UpdateCaptureStats();
            }
            finally
            {
                Interlocked.Exchange(ref _previewUpdatePending, 0);
            }
        });
    }

    private void CaptureSession_Faulted(object? sender, Exception exception)
    {
        if (!ReferenceEquals(sender, _captureSession)) return;
        _ = Dispatcher.BeginInvoke(async () =>
        {
            SetStatus($"Capture stopped: {exception.Message}", true);
            await StopCaptureAsync();
        });
    }

    private void UpdateCaptureStats()
    {
        var session = _captureSession;
        if (session is null) return;
        var elapsed = _captureRateClock.Elapsed.TotalSeconds;
        if (elapsed < 0.5) return;
        var captured = session.CapturedFrames;
        var encoded = session.EncodedFrames;
        var captureFps = (captured - _lastCapturedFrames) / elapsed;
        var recordFps = (encoded - _lastEncodedFrames) / elapsed;
        _lastCapturedFrames = captured;
        _lastEncodedFrames = encoded;
        _captureRateClock.Restart();
        CaptureStatsText.Text = $"DXGI {captureFps,5:F1} FPS  |  buffer {recordFps,4:F1}/{session.RecordingFps} FPS" +
                                $"  |  dropped {session.DroppedBeforeEncodeFrames}  |  retained {session.Buffer.BufferedDuration.TotalSeconds:F1}s" +
                                $"  |  {session.Buffer.Bytes / 1048576d:F1} MiB";
    }

    private async void StopCapture_Click(object sender, RoutedEventArgs e) => await StopCaptureAsync();

    private async Task StopCaptureAsync()
    {
        _captureActionCancellation?.Cancel();
        await _captureLifecycleGate.WaitAsync();
        try
        {
            await StopCaptureCoreAsync();
        }
        finally
        {
            _captureLifecycleGate.Release();
        }
    }

    private async Task StopCaptureCoreAsync()
    {
        var session = Interlocked.Exchange(ref _captureSession, null);
        if (session is not null)
        {
            session.FrameEncoded -= CaptureSession_FrameEncoded;
            session.Faulted -= CaptureSession_Faulted;
            await session.DisposeAsync();
        }
        StartCaptureButton.IsEnabled = true;
        CaptureFpsCombo.IsEnabled = true;
        CaptureStatsText.Text = "Capture stopped";
    }

    private async void CaptureAction_Click(object sender, RoutedEventArgs e) => await CaptureActionAsync();

    private async Task CaptureActionAsync()
    {
        var session = _captureSession;
        if (session is null)
        {
            SetStatus("Start rolling capture before capturing a scene.", true);
            return;
        }
        if (Interlocked.Exchange(ref _captureActionRunning, 1) != 0) return;

        using var cancellation = new CancellationTokenSource();
        Interlocked.Exchange(ref _captureActionCancellation, cancellation)?.Cancel();
        CaptureActionButton.IsEnabled = false;
        CaptureTelemetryCycleButton.IsEnabled = false;
        var projectGeneration = Volatile.Read(ref _projectGeneration);
        try
        {
            var preRoll = ParseSeconds(PreRollText.Text, 12, 1, 300);
            var postRoll = ParseSeconds(PostRollText.Text, 3, 0, 60);
            var marker = session.Buffer.LatestTimestamp;
            var markerUtc = DateTimeOffset.UtcNow;
            if (marker is null)
            {
                SetStatus("The rolling buffer does not contain a frame yet.", true);
                return;
            }

            SetStatus($"Scene marked. Collecting {postRoll:F1} seconds of post-roll...");
            if (postRoll > 0) await Task.Delay(TimeSpan.FromSeconds(postRoll), cancellation.Token);
            if (!ReferenceEquals(session, _captureSession) || projectGeneration != Volatile.Read(ref _projectGeneration))
            {
                SetStatus("Capture session ended before the scene clip was complete.", true);
                return;
            }

            var endUtc = markerUtc.AddSeconds(postRoll);
            if (postRoll > 0 && !await session.WaitForEncodedThroughAsync(
                    endUtc, TimeSpan.FromSeconds(1), cancellation.Token))
            {
                SetStatus("Capture encoder did not reach the end of the event. No scene was created.", true);
                return;
            }
            if (!ReferenceEquals(session, _captureSession) || projectGeneration != Volatile.Read(ref _projectGeneration))
            {
                SetStatus("Capture session ended before the scene clip was complete.", true);
                return;
            }
            var clip = session.Buffer.Snapshot(markerUtc - TimeSpan.FromSeconds(preRoll), endUtc);
            if (clip.Frames.Count == 0 || clip.DurationMilliseconds <= 0)
            {
                SetStatus("No encoded frames were available in the requested range.", true);
                return;
            }

            PrepareNewSceneForCapture();
            SetWorkingClip(clip, resetTracks: true,
                clip.StartedAtUtc, clip.EndedAtUtc, autoApplyRuntimeName: true);
            SetStatus($"Captured {clip.DurationMilliseconds / 1000d:F2} seconds ({clip.Frames.Count} frames). Scrub, trim, and draw the script below.");
        }
        catch (OperationCanceledException)
        {
            if (!_closing && Volatile.Read(ref _projectTransitionRunning) == 0) SetStatus("Scene capture cancelled.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _captureActionCancellation, null, cancellation);
            if (!_closing)
            {
                CaptureActionButton.IsEnabled = true;
                CaptureTelemetryCycleButton.IsEnabled = true;
            }
            Interlocked.Exchange(ref _captureActionRunning, 0);
        }
    }

    private void SetWorkingClip(
        CapturedClip clip,
        bool resetTracks,
        DateTimeOffset? sourceStartedAtUtc = null,
        DateTimeOffset? sourceEndedAtUtc = null,
        bool autoApplyRuntimeName = false)
    {
        _workingClip = clip;
        _workingClipStartedAtUtc = sourceStartedAtUtc;
        _workingClipEndedAtUtc = sourceEndedAtUtc;
        _reviewMode = true;
        _clipTimer.Stop();
        PlayClipButton.Content = "Play clip";
        ClipSlider.Maximum = Math.Max(1, clip.DurationMilliseconds);
        Timeline.DurationMilliseconds = Math.Max(1, clip.DurationMilliseconds);
        _trimInMilliseconds = 0;
        _trimOutMilliseconds = clip.DurationMilliseconds;
        if (resetTracks)
        {
            _correlatedUnityScene = string.Empty;
            _correlatedUnityAnimation = string.Empty;
            _correlatedAnimationCandidates = [];
            _workingTracks.Clear();
            _workingTracks[EdiAxis.Default] = [new(0, 50), new(clip.DurationMilliseconds, 50)];
            _selectedAxis = EdiAxis.Default;
            AxisCombo.SelectedItem = EdiAxis.Default;
            Timeline.SetPoints(_workingTracks[EdiAxis.Default]);
        }
        SetCursor(0, true);
        UpdateTrimText();
        PreviewModeText.Text = "SCENE REVIEW";
        UpdateRuntimeCorrelation(autoApplyRuntimeName);
    }

    private void ReturnLive_Click(object sender, RoutedEventArgs e)
    {
        _reviewMode = false;
        _clipTimer.Stop();
        PlayClipButton.Content = "Play clip";
        PreviewModeText.Text = "LIVE PREVIEW";
        if (_latestPreviewFrame is { } latest) PreviewImage.Source = CreateBitmap(latest.Data);
    }

    private void PlayClip_Click(object sender, RoutedEventArgs e)
    {
        ToggleClipPlayback();
    }

    private void ToggleClipPlayback()
    {
        if (_workingClip is null)
        {
            SetStatus("Capture or load a scene clip first.", true);
            return;
        }

        _reviewMode = true;
        if (_clipTimer.IsEnabled)
        {
            _clipTimer.Stop();
            PlayClipButton.Content = "Play clip";
            return;
        }

        _playFromMilliseconds = (int)ClipSlider.Value;
        _clipPlaybackClock.Restart();
        _clipTimer.Start();
        PlayClipButton.Content = "Pause clip";
    }

    private void AdvanceClipPlayback()
    {
        if (_workingClip is null || _workingClip.DurationMilliseconds <= 0) return;
        var cursor = _playFromMilliseconds + (int)Math.Round(_clipPlaybackClock.Elapsed.TotalMilliseconds * _playbackRate);
        if (cursor > _workingClip.DurationMilliseconds)
        {
            cursor %= _workingClip.DurationMilliseconds;
            _playFromMilliseconds = cursor;
            _clipPlaybackClock.Restart();
        }
        SetCursor(cursor, true);
    }

    private void ClipSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingUi) return;
        SeekCursor((int)Math.Round(e.NewValue), false);
    }

    private void SeekCursor(int milliseconds, bool updateSlider = true)
    {
        SetCursor(milliseconds, updateSlider);
        if (!_clipTimer.IsEnabled) return;
        _playFromMilliseconds = Timeline.CursorMilliseconds;
        _clipPlaybackClock.Restart();
    }

    private void SetCursor(int milliseconds, bool updateSlider)
    {
        var duration = _workingClip?.DurationMilliseconds ?? Timeline.DurationMilliseconds;
        milliseconds = Math.Clamp(milliseconds, 0, Math.Max(0, duration));
        _updatingUi = true;
        if (updateSlider) ClipSlider.Value = milliseconds;
        Timeline.CursorMilliseconds = milliseconds;
        TimelineCursorText.Text = $"{milliseconds} ms";
        ClipTimeText.Text = $"{FormatTime(milliseconds)} / {FormatTime(duration)}";
        _updatingUi = false;
        if (_reviewMode && _workingClip?.FindNearest(milliseconds) is { } frame)
        {
            PreviewImage.Source = CreateBitmap(frame.Data);
        }
        UpdateSimulatorValue();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (EditorInputHasKeyboardFocus()) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var repeats = key is Key.Left or Key.Right or Key.Up or Key.Down ||
                      modifiers == ModifierKeys.Control && key is Key.Z or Key.Y;
        if (e.IsRepeat && !repeats)
        {
            if (modifiers == ModifierKeys.None && key == Key.Space) e.Handled = true;
            return;
        }

        if (modifiers == ModifierKeys.Control && key == Key.S)
        {
            e.Handled = true;
            await SaveProjectAsync();
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.S)
        {
            e.Handled = true;
            Export_Click(this, new RoutedEventArgs());
            return;
        }
        if (modifiers == ModifierKeys.Control && key is Key.Z or Key.Y)
        {
            e.Handled = true;
            if (key == Key.Z) Timeline.Undo();
            else Timeline.Redo();
            return;
        }
        if (modifiers == ModifierKeys.Control && key is Key.Left or Key.Right)
        {
            e.Handled = true;
            StepClipFrames(key == Key.Left ? -1 : 1, 6);
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key is Key.Left or Key.Right)
        {
            e.Handled = true;
            var step = GetPointNudgeMilliseconds();
            NudgeNearestPoint(key == Key.Left ? -step : step, 0, true);
            return;
        }
        if (modifiers == ModifierKeys.Shift && key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            e.Handled = true;
            var step = GetPointNudgeMilliseconds();
            var timeDelta = key == Key.Left ? -step : key == Key.Right ? step : 0;
            var positionDelta = key == Key.Up ? 1 : key == Key.Down ? -1 : 0;
            NudgeNearestPoint(timeDelta, positionDelta, false);
            return;
        }
        if (modifiers != ModifierKeys.None) return;

        if (TryGetNumpadPosition(key, out var position))
        {
            e.Handled = true;
            Timeline.AddOrReplaceAtCursor(position);
            return;
        }

        switch (key)
        {
            case Key.Space:
                e.Handled = true;
                ToggleClipPlayback();
                break;
            case Key.Left:
                e.Handled = true;
                StepClipFrames(-1, 1);
                break;
            case Key.Right:
                e.Handled = true;
                StepClipFrames(1, 1);
                break;
            case Key.Down:
                e.Handled = true;
                NavigatePoint(-1);
                break;
            case Key.Up:
                e.Handled = true;
                NavigatePoint(1);
                break;
            case Key.Delete:
                e.Handled = true;
                Timeline.DeleteNearestAtCursor();
                break;
            case Key.Subtract:
                e.Handled = true;
                AdjustPlaybackRate(-0.1);
                break;
            case Key.Add:
                e.Handled = true;
                AdjustPlaybackRate(0.1);
                break;
            case Key.I:
                e.Handled = true;
                Timeline.InvertNearest();
                break;
            case Key.End:
                e.Handled = true;
                Timeline.MoveNearestToCursor();
                break;
        }
    }

    private static bool EditorInputHasKeyboardFocus() =>
        Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox or ListBox or ListBoxItem;

    private static bool TryGetNumpadPosition(Key key, out int position)
    {
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            position = ((int)key - (int)Key.NumPad0) * 10;
            return true;
        }
        if (key == Key.Divide)
        {
            position = 100;
            return true;
        }
        position = 0;
        return false;
    }

    private void StepClipFrames(int direction, int count)
    {
        if (_clipTimer.IsEnabled || _workingClip is not { Frames.Count: > 0 } clip) return;
        var cursor = Timeline.CursorMilliseconds;
        int index;
        if (direction > 0)
        {
            index = 0;
            while (index < clip.Frames.Count && clip.Frames[index].OffsetMilliseconds <= cursor) index++;
            if (index >= clip.Frames.Count) index = clip.Frames.Count - 1;
            index = Math.Min(clip.Frames.Count - 1, index + Math.Max(0, count - 1));
        }
        else
        {
            index = clip.Frames.Count - 1;
            while (index >= 0 && clip.Frames[index].OffsetMilliseconds >= cursor) index--;
            if (index < 0) index = 0;
            index = Math.Max(0, index - Math.Max(0, count - 1));
        }
        SeekCursor(clip.Frames[index].OffsetMilliseconds);
    }

    private void NavigatePoint(int direction)
    {
        var destination = direction > 0
            ? Timeline.FindNextPointTime(Timeline.CursorMilliseconds)
            : Timeline.FindPreviousPointTime(Timeline.CursorMilliseconds);
        if (destination is { } milliseconds) SeekCursor(milliseconds);
    }

    private void NudgeNearestPoint(int timeDelta, int positionDelta, bool follow)
    {
        if (Timeline.NudgeNearest(timeDelta, positionDelta) is { } moved && follow) SeekCursor(moved.At);
    }

    private int GetPointNudgeMilliseconds()
    {
        if (_workingClip is not { Frames.Count: > 1 } clip) return FunscriptTimeline.TimeStepMilliseconds;
        var intervals = clip.Frames.Zip(clip.Frames.Skip(1),
                (left, right) => right.OffsetMilliseconds - left.OffsetMilliseconds)
            .Where(interval => interval > 0)
            .OrderBy(interval => interval)
            .ToArray();
        return intervals.Length == 0 ? FunscriptTimeline.TimeStepMilliseconds : intervals[intervals.Length / 2];
    }

    private void AdjustPlaybackRate(double delta)
    {
        _playbackRate = Math.Clamp(Math.Round((_playbackRate + delta) * 10) / 10, 0.05, 3);
        PlaybackRateText.Text = $"{_playbackRate:0.0#}x";
        if (_clipTimer.IsEnabled)
        {
            _playFromMilliseconds = Timeline.CursorMilliseconds;
            _clipPlaybackClock.Restart();
        }
        SetStatus($"Playback speed: {_playbackRate:0.0#}x");
    }

    private void ShowShortcuts_Click(object sender, RoutedEventArgs e)
    {
        const string shortcuts = """
            OFS-compatible editor keys

            Space                 Play / pause
            Left / Right          Previous / next captured frame (paused)
            Ctrl+Left / Right     Step 6 captured frames (paused)
            Down / Up             Previous / next funscript point
            Numpad 0-9            Add/edit at positions 0-90
            Numpad /              Add/edit at position 100
            Delete                Delete nearest point
            Shift+Arrows          Nudge nearest point by one frame / 1 position
            Ctrl+Shift+Left/Right Nudge time and follow the point
            Ctrl+Z / Ctrl+Y       Undo / redo
            Numpad - / +          Playback speed -/+ 0.1x
            I                     Invert nearest point
            End                   Move nearest point to playhead
            Ctrl+S                Save project
            Ctrl+Shift+S          Export EDI gallery

            Mouse workflow
            Left-click empty space to add one point. Keep dragging to move that same point.
            Left-drag an existing point to move it. Right-click near a point to delete it.
            """;
        MessageBox.Show(this, shortcuts, "OFS-compatible editor controls", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void MarkIn_Click(object sender, RoutedEventArgs e)
    {
        if (_workingClip is null) return;
        _trimInMilliseconds = Math.Min((int)ClipSlider.Value, _trimOutMilliseconds - 1);
        UpdateTrimText();
    }

    private void MarkOut_Click(object sender, RoutedEventArgs e)
    {
        if (_workingClip is null) return;
        _trimOutMilliseconds = Math.Max((int)ClipSlider.Value, _trimInMilliseconds + 1);
        UpdateTrimText();
    }

    private void UpdateTrimText() => TrimText.Text = $"Trim: {_trimInMilliseconds} ms to {_trimOutMilliseconds} ms";

    private void ApplyTrim_Click(object sender, RoutedEventArgs e)
    {
        if (IsCurrentSceneLocked())
        {
            SetStatus("This scene is locked. Unlock it before changing its clip or curve.", true);
            return;
        }
        if (_workingClip is null || _trimInMilliseconds == 0 && _trimOutMilliseconds == _workingClip.DurationMilliseconds) return;
        SaveCurrentTrack();
        var oldDuration = _workingClip.DurationMilliseconds;
        foreach (var axis in _workingTracks.Keys.ToArray())
        {
            _workingTracks[axis] = TrimPoints(_workingTracks[axis], _trimInMilliseconds, _trimOutMilliseconds, oldDuration);
        }
        var sourceStartedAtUtc = _workingClipStartedAtUtc?.AddMilliseconds(_trimInMilliseconds);
        var sourceEndedAtUtc = _workingClipStartedAtUtc?.AddMilliseconds(_trimOutMilliseconds);
        var trimmed = _workingClip.Trim(_trimInMilliseconds, _trimOutMilliseconds);
        SetWorkingClip(trimmed, resetTracks: false, sourceStartedAtUtc, sourceEndedAtUtc, autoApplyRuntimeName: true);
        Timeline.SetPoints(_workingTracks.GetValueOrDefault(_selectedAxis) ?? []);
        SetStatus($"Trim applied. New duration: {trimmed.DurationMilliseconds} ms.");
    }

    private static List<FunscriptPoint> TrimPoints(IReadOnlyList<FunscriptPoint> source, int start, int end, int duration)
    {
        if (source.Count == 0) return [];
        start = Math.Clamp(start, 0, duration);
        end = Math.Clamp(end, start + 1, duration);
        var ordered = source.OrderBy(point => point.At).ToArray();
        var result = ordered.Where(point => point.At >= start && point.At <= end)
            .Select(point => new FunscriptPoint(point.At - start, point.Pos)).ToList();
        var startPos = Interpolate(ordered, start);
        var endPos = Interpolate(ordered, end);
        result.RemoveAll(point => point.At == 0 || point.At == end - start);
        result.Insert(0, new(0, startPos));
        result.Add(new(end - start, endPos));
        return result.OrderBy(point => point.At).ToList();
    }

    private static int Interpolate(IReadOnlyList<FunscriptPoint> points, int at)
    {
        return (int)Math.Round(FunscriptCurve.Evaluate(points, at));
    }

    private void UpdateRuntimeCorrelation(bool autoApplyName)
    {
        if (_workingClipStartedAtUtc is { } startedAt && _workingClipEndedAtUtc is { } endedAt &&
            !string.IsNullOrWhiteSpace(_project.Game.TelemetryPath))
        {
            try
            {
                var correlation = UnityTelemetryLog.Correlate(
                    UnityTelemetryLog.Read(_project.Game.TelemetryPath), startedAt, endedAt);
                if (!string.IsNullOrWhiteSpace(correlation.SceneName)) _correlatedUnityScene = correlation.SceneName;
                _correlatedUnityAnimation = correlation.AnimationName;
                _correlatedAnimationCandidates = correlation.AnimationCandidates;
                if (autoApplyName && !string.IsNullOrWhiteSpace(correlation.PreferredName) &&
                    IsPlaceholderSceneName(ActionNameText.Text))
                    ApplyRuntimeName(correlation.PreferredName);
            }
            catch (IOException exception)
            {
                RuntimeCorrelationText.Text = $"Runtime match delayed: {exception.Message}";
                return;
            }
        }
        UpdateRuntimeCorrelationText();
    }

    private void UpdateRuntimeCorrelationText()
    {
        var correlationText = !string.IsNullOrWhiteSpace(_correlatedUnityAnimation)
            ? $"Runtime match: animation '{_correlatedUnityAnimation}' in scene '{_correlatedUnityScene}'"
            : _correlatedAnimationCandidates.Count > 1
                ? $"Runtime match: scene '{_correlatedUnityScene}', {_correlatedAnimationCandidates.Count} animation candidates (select one below)"
                : !string.IsNullOrWhiteSpace(_correlatedUnityScene)
                    ? $"Runtime match: scene '{_correlatedUnityScene}'"
                    : "Runtime match: no Unity telemetry for this clip";
        RuntimeCorrelationText.Text = _pendingCapturedTriggerMapping is { } pending
            ? $"{correlationText} | Will auto-map {pending.Kind} '{pending.Candidate}' when this scene is saved."
            : correlationText;
    }

    private void UseDetectedName_Click(object sender, RoutedEventArgs e)
    {
        if (IsCurrentSceneLocked())
        {
            SetStatus("This scene is locked. Unlock it before renaming it.", true);
            return;
        }
        var candidate = !string.IsNullOrWhiteSpace(_correlatedUnityAnimation)
            ? _correlatedUnityAnimation
            : _correlatedUnityScene;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            SetStatus("No unambiguous Unity scene or animation name is correlated with this clip yet.", true);
            return;
        }
        ApplyRuntimeName(candidate);
        SetStatus($"Scene and script filename derived from Unity name '{candidate}'.");
    }

    private void UseTelemetryName_Click(object sender, RoutedEventArgs e)
    {
        if (IsCurrentSceneLocked())
        {
            SetStatus("This scene is locked. Unlock it before renaming it.", true);
            return;
        }
        if (UnityTelemetryList.SelectedItem is not UnityTelemetryEntry entry || string.IsNullOrWhiteSpace(entry.Candidate))
        {
            SetStatus("Select a discovered Unity scene, animation, or FSM state first.", true);
            return;
        }
        if (entry.Kind is "SCENE" or "ACTIVE_SCENE") _correlatedUnityScene = entry.Candidate;
        else if (UnityTelemetryLog.IsRuntimeCandidateEvent(entry.Kind)) _correlatedUnityAnimation = entry.Candidate;
        else
        {
            SetStatus("Only scene, animation, and FSM state entries can name an authored scene.", true);
            return;
        }
        ApplyRuntimeName(entry.Candidate);
        UpdateRuntimeCorrelationText();
        SetStatus($"Scene and script filename derived from Unity {entry.Kind.ToLowerInvariant()} '{entry.Candidate}'.");
    }

    private async void CaptureTelemetryCycle_Click(object sender, RoutedEventArgs e)
    {
        var session = _captureSession;
        if (session is null)
        {
            SetStatus("Start rolling capture before capturing a runtime playback cycle.", true);
            return;
        }
        if (UnityTelemetryList.SelectedItem is not UnityTelemetryEntry entry ||
            !UnityTelemetryLog.IsTimedPlaybackEvent(entry.Kind) ||
            string.IsNullOrWhiteSpace(entry.Candidate))
        {
            SetStatus("Select a timed Animator, Legacy Animation, Timeline, or Spine event first.", true);
            return;
        }
        var telemetryEvent = new UnityTelemetryEvent(entry.Timestamp, entry.Kind, entry.Scene, entry.ObjectPath,
            entry.Candidate, entry.Details);
        if (!UnityTelemetryLog.TryGetPlaybackTiming(telemetryEvent, out var timing))
        {
            SetStatus("The selected event has no cycle timing. Build + install the updated mod, then capture a new event.", true);
            return;
        }
        if (Interlocked.Exchange(ref _captureActionRunning, 1) != 0) return;

        CaptureActionButton.IsEnabled = false;
        CaptureTelemetryCycleButton.IsEnabled = false;
        var projectGeneration = Volatile.Read(ref _projectGeneration);
        try
        {
            var startUtc = timing.GetCycleStart(entry.Timestamp);
            var endUtc = startUtc + timing.CycleDuration;
            var durationText = timing.CycleDuration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            SetStatus($"Capturing '{entry.Candidate}' cycle ({durationText} seconds) from runtime timing...");

            var deadline = endUtc.AddSeconds(3);
            while (session.Buffer.LatestCapturedAtUtc is not { } latest || latest < endUtc)
            {
                if (DateTimeOffset.UtcNow >= deadline || !ReferenceEquals(session, _captureSession) ||
                    projectGeneration != Volatile.Read(ref _projectGeneration)) break;
                await Task.Delay(50);
            }
            if (!ReferenceEquals(session, _captureSession) || projectGeneration != Volatile.Read(ref _projectGeneration))
            {
                SetStatus("Capture session ended before the runtime playback cycle was complete.", true);
                return;
            }
            if (session.Buffer.EarliestCapturedAtUtc is not { } earliest || earliest > startUtc.AddMilliseconds(250))
            {
                SetStatus("The selected runtime playback cycle has fallen outside the rolling capture buffer.", true);
                return;
            }
            if (session.Buffer.LatestCapturedAtUtc is not { } latestCaptured || latestCaptured < endUtc)
            {
                SetStatus("Encoded frames did not reach the end of the selected runtime playback cycle.", true);
                return;
            }

            var clip = session.Buffer.Snapshot(startUtc, endUtc);
            if (clip.Frames.Count == 0)
            {
                SetStatus("No encoded frames were available for the selected runtime playback cycle.", true);
                return;
            }

            PrepareNewSceneForCapture();
            SetWorkingClip(clip, resetTracks: true, startUtc, endUtc);
            _correlatedUnityScene = entry.Scene;
            _correlatedUnityAnimation = entry.Candidate;
            _correlatedAnimationCandidates = [entry.Candidate];
            _pendingCapturedTriggerMapping = new UnityTriggerMapping
            {
                Kind = UnityTriggerKind.AnimationClip,
                Candidate = entry.Candidate,
                ObjectPath = entry.ObjectPath,
                CycleDurationMilliseconds = Math.Max(1, (int)Math.Round(timing.CycleDuration.TotalMilliseconds))
            };
            LoopCheck.IsChecked = timing.IsLooping;
            if (IsPlaceholderSceneName(ActionNameText.Text)) ApplyRuntimeName(entry.Candidate);
            UpdateRuntimeCorrelationText();
            UpdateTriggerMappingStatus();
            SetStatus($"Captured '{entry.Candidate}' as an exact {durationText}-second runtime playback cycle ({clip.Frames.Count} frames).");
        }
        finally
        {
            CaptureActionButton.IsEnabled = true;
            CaptureTelemetryCycleButton.IsEnabled = true;
            Interlocked.Exchange(ref _captureActionRunning, 0);
        }
    }

    private void ApplyRuntimeName(string candidate)
    {
        candidate = candidate.Trim();
        var displayName = candidate;
        var suffix = 2;
        while (_project.Actions.Any(action => action.Id != _editingActionId &&
               action.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase)))
            displayName = $"{candidate} {suffix++}";

        var baseStem = ToSafeFileStem(candidate);
        var fileStem = baseStem;
        suffix = 2;
        while (_project.Actions.Any(action => action.Id != _editingActionId &&
               action.FileName.Equals(fileStem, StringComparison.OrdinalIgnoreCase)))
            fileStem = $"{baseStem}-{suffix++}";
        ActionNameText.Text = displayName;
        FileNameText.Text = fileStem;
    }

    private static bool IsPlaceholderSceneName(string value) =>
        value.Equals("new-action", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("new-scene", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("action-", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("scene-", StringComparison.OrdinalIgnoreCase);

    private static string ToSafeFileStem(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var separatorPending = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && builder.Length > 0) builder.Append('-');
                builder.Append(char.ToLowerInvariant(character));
                separatorPending = false;
            }
            else
            {
                separatorPending = true;
            }
        }
        return builder.Length == 0 ? "scene" : builder.ToString();
    }

    private void NewAction_Click(object sender, RoutedEventArgs e)
    {
        ActionList.SelectedItem = null;
        ResetActionEditor();
        var next = $"scene-{_project.Actions.Count + 1}";
        ActionNameText.Text = next;
        FileNameText.Text = next;
        UpdateRuntimeCorrelation(autoApplyName: true);
        SetStatus("New scene editor ready. Capture a scene or draw against the current clip.");
    }

    private void PrepareNewSceneForCapture()
    {
        _actionLoadCancellation?.Cancel();
        _actionLoadCancellation?.Dispose();
        _actionLoadCancellation = null;
        _updatingUi = true;
        ActionList.SelectedItem = null;
        _updatingUi = false;
        _workingClip = null;
        _workingClipStartedAtUtc = null;
        _workingClipEndedAtUtc = null;
        _correlatedUnityScene = string.Empty;
        _correlatedUnityAnimation = string.Empty;
        _correlatedAnimationCandidates = [];
        _pendingCapturedTriggerMapping = null;
        ResetActionEditor();
        var next = $"scene-{_project.Actions.Count + 1}";
        ActionNameText.Text = next;
        FileNameText.Text = next;
    }

    private void ResetActionEditor()
    {
        _updatingUi = true;
        _editingActionId = null;
        _pendingCapturedTriggerMapping = null;
        ActionNameText.Text = "new-scene";
        FileNameText.Text = "new-scene";
        ActionTypeCombo.SelectedItem = EdiGalleryType.Gallery;
        VariantText.Text = "default";
        LoopCheck.IsChecked = true;
        DescriptionText.Text = string.Empty;
        _workingTracks.Clear();
        var duration = _workingClip?.DurationMilliseconds ?? 1000;
        _workingTracks[EdiAxis.Default] = [new(0, 50), new(duration, 50)];
        _selectedAxis = EdiAxis.Default;
        AxisCombo.SelectedItem = EdiAxis.Default;
        Timeline.DurationMilliseconds = duration;
        Timeline.SetPoints(_workingTracks[EdiAxis.Default]);
        if (_workingClip is null)
        {
            _workingClipStartedAtUtc = null;
            _workingClipEndedAtUtc = null;
            _correlatedUnityScene = string.Empty;
            _correlatedUnityAnimation = string.Empty;
            _correlatedAnimationCandidates = [];
        }
        UpdateRuntimeCorrelationText();
        _updatingUi = false;
        ApplySceneLockState(false);
        UpdateTriggerMappingStatus();
        UpdatePointCount();
    }

    private async void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || ActionList.SelectedItem is not AuthoredAction action) return;
        _actionLoadCancellation?.Cancel();
        _actionLoadCancellation?.Dispose();
        _actionLoadCancellation = new CancellationTokenSource();
        try
        {
            await LoadActionAsync(action, _actionLoadCancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task LoadActionAsync(AuthoredAction action, CancellationToken cancellationToken)
    {
        _updatingUi = true;
        _editingActionId = action.Id;
        _pendingCapturedTriggerMapping = null;
        ActionNameText.Text = action.Name;
        FileNameText.Text = action.FileName;
        ActionTypeCombo.SelectedItem = action.Type;
        VariantText.Text = action.Variant;
        LoopCheck.IsChecked = action.Loop;
        DescriptionText.Text = action.Description;
        _workingClipStartedAtUtc = action.SourceStartedAtUtc;
        _workingClipEndedAtUtc = action.SourceEndedAtUtc;
        _correlatedUnityScene = action.UnitySceneName;
        _correlatedUnityAnimation = action.UnityAnimationName;
        _correlatedAnimationCandidates = string.IsNullOrWhiteSpace(action.UnityAnimationName)
            ? []
            : [action.UnityAnimationName];
        UpdateRuntimeCorrelationText();
        UpdateTriggerMappingStatus();
        _workingTracks.Clear();
        foreach (var track in action.Tracks)
        {
            _workingTracks[track.Axis] = track.Points.OrderBy(point => point.At).ToList();
        }
        _selectedAxis = EdiAxis.Default;
        AxisCombo.SelectedItem = EdiAxis.Default;
        Timeline.DurationMilliseconds = action.DurationMilliseconds;
        Timeline.SetPoints(_workingTracks.GetValueOrDefault(EdiAxis.Default) ?? []);
        _updatingUi = false;
        ApplySceneLockState(action.IsLocked);
        UpdatePointCount();

        _workingClip = null;
        if (_projectDirectory is not null && !string.IsNullOrWhiteSpace(action.SourceClipPath))
        {
            try
            {
                var path = ResolveProjectAsset(action.SourceClipPath);
                var clip = await _clipArchive.LoadAsync(path, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (_editingActionId != action.Id || ActionList.SelectedItem is not AuthoredAction selected || selected.Id != action.Id)
                {
                    return;
                }
                SetWorkingClip(clip, resetTracks: false, action.SourceStartedAtUtc, action.SourceEndedAtUtc);
                Timeline.SetPoints(_workingTracks.GetValueOrDefault(_selectedAxis) ?? []);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                SetStatus($"Scene loaded, but its review clip could not be opened: {exception.Message}", true);
            }
        }
        else
        {
            ClipSlider.Maximum = action.DurationMilliseconds;
            _trimInMilliseconds = 0;
            _trimOutMilliseconds = action.DurationMilliseconds;
            UpdateTrimText();
        }
    }

    private async void DeleteAction_Click(object sender, RoutedEventArgs e)
    {
        var projectGeneration = Volatile.Read(ref _projectGeneration);
        if (Volatile.Read(ref _projectTransitionRunning) != 0) return;
        if (ActionList.SelectedItem is not AuthoredAction action || _projectDirectory is null) return;
        if (action.IsLocked)
        {
            SetStatus($"'{action.Name}' is locked. Unlock it before deleting it.", true);
            return;
        }
        if (MessageBox.Show(this, $"Delete scene '{action.Name}' from this project?", "Delete scene",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _projectOperationGate.WaitAsync();
        try
        {
            if (projectGeneration != Volatile.Read(ref _projectGeneration) ||
                Volatile.Read(ref _projectTransitionRunning) != 0) return;
            var candidate = CloneProject(
                _project.Actions.Where(item => item.Id != action.Id),
                _project.Bundles.Select(bundle => new BundleDefinition
                {
                    Name = bundle.Name,
                    Actions = bundle.Actions.Where(name => !name.Equals(action.Name, StringComparison.OrdinalIgnoreCase)).ToList()
                }));
            candidate.Game.TriggerMappings.RemoveAll(mapping =>
                mapping.ActionName.Equals(action.Name, StringComparison.OrdinalIgnoreCase));
            await _projectStore.SaveAsync(_projectDirectory, candidate);
            _project = candidate;
            if (!string.IsNullOrWhiteSpace(action.SourceClipPath))
            {
                try { File.Delete(ResolveProjectAsset(action.SourceClipPath)); } catch (IOException) { }
            }
            RefreshActionList();
            UpdateTriggerMappingStatus();
            ResetActionEditor();
            SetStatus($"Deleted '{action.Name}'.");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not delete scene: {exception.Message}", true);
        }
        finally
        {
            _projectOperationGate.Release();
        }
    }

    private void AxisCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingUi || AxisCombo.SelectedItem is not EdiAxis axis) return;
        SaveCurrentTrack();
        _selectedAxis = axis;
        if (!_workingTracks.TryGetValue(axis, out var points))
        {
            var duration = Math.Max(1, Timeline.DurationMilliseconds);
            points = [new(0, 50), new(duration, 50)];
            _workingTracks[axis] = points;
        }
        Timeline.SetPoints(points);
        UpdatePointCount();
    }

    private void SaveCurrentTrack() => _workingTracks[_selectedAxis] = Timeline.GetPoints().OrderBy(point => point.At).ToList();

    private void ClearCurve_Click(object sender, RoutedEventArgs e)
    {
        if (IsCurrentSceneLocked())
        {
            SetStatus("This scene is locked. Unlock it before editing its curve.", true);
            return;
        }
        Timeline.ClearPoints();
    }

    private void PreviewScript_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentTrack();
        var points = _workingTracks.GetValueOrDefault(_selectedAxis) ?? [];
        if (points.Count == 0)
        {
            SetStatus("The current scene axis has no funscript points to preview.", true);
            return;
        }
        var duration = _workingClip?.DurationMilliseconds ?? Timeline.DurationMilliseconds;
        var preview = EdiExporter.CreateFunscriptPreview(points, duration, LoopCheck.IsChecked == true);
        var suffix = _selectedAxis == EdiAxis.Default ? string.Empty : $".{_selectedAxis.ToString().ToLowerInvariant()}";
        var fileName = (string.IsNullOrWhiteSpace(FileNameText.Text) ? "scene" : FileNameText.Text) + suffix + ".funscript";
        var summary = $"File: {fileName}{Environment.NewLine}" +
                      $"Duration: {preview.DurationMilliseconds} ms{Environment.NewLine}" +
                      $"Points: {preview.AuthoredPointCount} authored -> {preview.ExportedPointCount} exported{Environment.NewLine}" +
                      $"Boundary inserted: {(preview.BoundaryInserted ? "yes" : "no")}{Environment.NewLine}" +
                      $"Clean loop closure applied: {(preview.LoopClosureApplied ? "yes" : "no")}" +
                      (preview.LoopClosureIntervalMilliseconds is { } closure
                          ? $" ({closure} ms available for closure)"
                          : string.Empty) + Environment.NewLine + Environment.NewLine +
                      preview.Json;
        MessageBox.Show(this, summary, $"Exact script preview - {_selectedAxis}", MessageBoxButton.OK,
            MessageBoxImage.Information);
        SetStatus($"Verified exact {fileName} export preview with {preview.ExportedPointCount} point(s).");
    }

    private async void SaveAction_Click(object sender, RoutedEventArgs e) => await SaveActionAsync(lockAfterSave: false);

    private async void SceneLock_Click(object sender, RoutedEventArgs e)
    {
        var existing = GetCurrentScene();
        if (existing?.IsLocked == true)
        {
            if (MessageBox.Show(this,
                    $"Unlock scene '{existing.Name}'? It will become editable and can be overwritten or deleted.",
                    "Unlock protected scene", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await SetSceneLockAsync(existing, isLocked: false);
            return;
        }
        await SaveActionAsync(lockAfterSave: true);
    }

    private async Task SaveActionAsync(bool lockAfterSave)
    {
        var projectGeneration = Volatile.Read(ref _projectGeneration);
        if (Volatile.Read(ref _projectTransitionRunning) != 0) return;
        if (_projectDirectory is null)
        {
            SetStatus("Create or open a project before saving a scene.", true);
            return;
        }
        if (_workingClip is null)
        {
            SetStatus("Capture or load a review clip before saving a scene.", true);
            return;
        }
        if (IsCurrentSceneLocked())
        {
            SetStatus("This scene is locked. Unlock it before saving changes.", true);
            return;
        }

        SaveCurrentTrack();
        var clip = _workingClip;
        var lockTaken = false;
        try
        {
            await _projectOperationGate.WaitAsync();
            lockTaken = true;
            if (projectGeneration != Volatile.Read(ref _projectGeneration) ||
                Volatile.Read(ref _projectTransitionRunning) != 0) return;
            var existing = _editingActionId is { } id ? _project.Actions.FirstOrDefault(item => item.Id == id) : null;
            var action = new AuthoredAction
            {
                Id = existing?.Id ?? Guid.NewGuid(),
                Name = ActionNameText.Text,
                FileName = FileNameText.Text,
                Type = ActionTypeCombo.SelectedItem is EdiGalleryType type ? type : EdiGalleryType.Gallery,
                Variant = VariantText.Text,
                Loop = LoopCheck.IsChecked == true,
                IsLocked = lockAfterSave,
                Description = DescriptionText.Text,
                DurationMilliseconds = clip.DurationMilliseconds,
                SourceStartedAtUtc = _workingClipStartedAtUtc,
                SourceEndedAtUtc = _workingClipEndedAtUtc,
                UnitySceneName = _correlatedUnityScene,
                UnityAnimationName = _correlatedUnityAnimation,
                Tracks = _workingTracks.Select(pair => new ActionTrack
                {
                    Axis = pair.Key,
                    Points = pair.Value.OrderBy(point => point.At).ToList()
                }).ToList()
            };
            action.SourceClipPath = $"clips/{action.Id:N}-{Guid.NewGuid():N}.ediclip";
            var candidate = CloneProjectWith(action);
            var automaticMapping = _pendingCapturedTriggerMapping;
            if (existing is not null && !existing.Name.Equals(action.Name, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var mapping in candidate.Game.TriggerMappings.Where(mapping =>
                             mapping.ActionName.Equals(existing.Name, StringComparison.OrdinalIgnoreCase)))
                    mapping.ActionName = action.Name;
            }
            if (automaticMapping is not null)
                candidate.Game.SetTriggerMapping(automaticMapping.Kind, automaticMapping.Candidate, action.Name,
                    automaticMapping.ObjectPath, automaticMapping.CycleDurationMilliseconds);
            var errors = _validator.Validate(candidate).Where(issue => issue.Severity == ValidationSeverity.Error).ToArray();
            if (errors.Length > 0)
            {
                ShowValidation(errors);
                return;
            }

            var newClipPath = ResolveProjectAsset(action.SourceClipPath);
            await _clipArchive.SaveAsync(newClipPath, clip);
            try
            {
                await _projectStore.SaveAsync(_projectDirectory, candidate);
            }
            catch
            {
                try { File.Delete(newClipPath); } catch (IOException) { }
                throw;
            }
            _project = candidate;
            if (existing is not null && !string.IsNullOrWhiteSpace(existing.SourceClipPath) &&
                !existing.SourceClipPath.Equals(action.SourceClipPath, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(ResolveProjectAsset(existing.SourceClipPath)); } catch (IOException) { }
            }
            _editingActionId = action.Id;
            _pendingCapturedTriggerMapping = null;
            RefreshActionList(action.Id);
            ApplySceneLockState(action.IsLocked);
            UpdateTriggerMappingStatus();
            UpdateRuntimeCorrelationText();
            var savedMessage = action.IsLocked
                ? $"Saved and locked scene '{action.Name}' with {action.Tracks.Sum(track => track.Points.Count)} point(s)."
                : $"Saved scene '{action.Name}' with {action.Tracks.Sum(track => track.Points.Count)} point(s) across {action.Tracks.Count} axis track(s).";
            if (automaticMapping is not null)
                savedMessage += $" Auto-mapped {automaticMapping.Kind} '{automaticMapping.Candidate}'. Export the EDI gallery, then use Build + install to apply it in game.";
            SetStatus(savedMessage);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not save scene: {exception.Message}", true);
        }
        finally
        {
            if (lockTaken) _projectOperationGate.Release();
        }
    }

    private async Task SetSceneLockAsync(AuthoredAction action, bool isLocked)
    {
        var projectGeneration = Volatile.Read(ref _projectGeneration);
        if (Volatile.Read(ref _projectTransitionRunning) != 0) return;
        if (_projectDirectory is null) return;
        var lockTaken = false;
        try
        {
            await _projectOperationGate.WaitAsync();
            lockTaken = true;
            if (projectGeneration != Volatile.Read(ref _projectGeneration) ||
                Volatile.Read(ref _projectTransitionRunning) != 0) return;
            var updated = CloneAction(action);
            updated.IsLocked = isLocked;
            var candidate = CloneProjectWith(updated);
            await _projectStore.SaveAsync(_projectDirectory, candidate);
            _project = candidate;
            _editingActionId = updated.Id;
            RefreshActionList(updated.Id);
            ApplySceneLockState(isLocked);
            SetStatus(isLocked ? $"Locked scene '{updated.Name}'." : $"Unlocked scene '{updated.Name}'.");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not update scene lock: {exception.Message}", true);
        }
        finally
        {
            if (lockTaken) _projectOperationGate.Release();
        }
    }

    private AuthoredAction? GetCurrentScene() =>
        _editingActionId is { } id ? _project.Actions.FirstOrDefault(action => action.Id == id) : null;

    private bool IsCurrentSceneLocked() => GetCurrentScene()?.IsLocked == true;

    private void ApplySceneLockState(bool isLocked)
    {
        ActionNameText.IsReadOnly = isLocked;
        FileNameText.IsReadOnly = isLocked;
        VariantText.IsReadOnly = isLocked;
        DescriptionText.IsReadOnly = isLocked;
        ActionTypeCombo.IsEnabled = !isLocked;
        LoopCheck.IsEnabled = !isLocked;
        Timeline.IsReadOnly = isLocked;
        ClearCurveButton.IsEnabled = !isLocked;
        UseDetectedNameButton.IsEnabled = !isLocked;
        SaveActionButton.IsEnabled = !isLocked;
        DeleteActionButton.IsEnabled = !isLocked;
        SceneLockButton.Content = isLocked ? "Unlock scene" : "Save + lock";
        SceneLockStatusText.Text = isLocked ? "LOCKED" : "Unlocked";
        SceneLockStatusText.Foreground = (Brush)FindResource(isLocked ? "WarningTextBrush" : "MutedTextBrush");
    }

    private StudioProject CloneProjectWith(AuthoredAction action)
    {
        var actions = _project.Actions.Where(item => item.Id != action.Id).ToList();
        actions.Add(action);
        return CloneProject(actions, _project.Bundles);
    }

    private static AuthoredAction CloneAction(AuthoredAction action) => new()
    {
        Id = action.Id,
        Name = action.Name,
        FileName = action.FileName,
        Type = action.Type,
        Loop = action.Loop,
        IsLocked = action.IsLocked,
        Description = action.Description,
        Variant = action.Variant,
        DurationMilliseconds = action.DurationMilliseconds,
        SourceClipPath = action.SourceClipPath,
        SourceStartedAtUtc = action.SourceStartedAtUtc,
        SourceEndedAtUtc = action.SourceEndedAtUtc,
        UnitySceneName = action.UnitySceneName,
        UnityAnimationName = action.UnityAnimationName,
        Tracks = action.Tracks.Select(track => new ActionTrack
        {
            Axis = track.Axis,
            Points = track.Points.ToList()
        }).ToList()
    };

    private StudioProject CloneProject(IEnumerable<AuthoredAction> actions, IEnumerable<BundleDefinition> bundles)
    {
        return new StudioProject
        {
            SchemaVersion = _project.SchemaVersion,
            Id = _project.Id,
            Name = _project.Name,
            CreatedAt = _project.CreatedAt,
            UpdatedAt = _project.UpdatedAt,
            Game = new GameTarget
            {
                ExecutablePath = _project.Game.ExecutablePath,
                ProcessName = _project.Game.ProcessName,
                Runtime = _project.Game.Runtime,
                Architecture = _project.Game.Architecture,
                UnityVersion = _project.Game.UnityVersion,
                TargetFramework = _project.Game.TargetFramework,
                BepInExFlavor = _project.Game.BepInExFlavor,
                ModPreset = _project.Game.ModPreset,
                ModProjectPath = _project.Game.ModProjectPath,
                TelemetryPath = _project.Game.TelemetryPath,
                InstalledPluginPath = _project.Game.InstalledPluginPath,
                TriggerMappings = _project.Game.TriggerMappings.Select(mapping => new UnityTriggerMapping
                {
                    Kind = mapping.Kind,
                    Candidate = mapping.Candidate,
                    ActionName = mapping.ActionName,
                    ObjectPath = mapping.ObjectPath,
                    CycleDurationMilliseconds = mapping.CycleDurationMilliseconds
                }).ToList(),
                Simulator = new LinearSimulatorLayout
                {
                    IsVisible = _project.Game.Simulator.IsVisible,
                    CenterX = _project.Game.Simulator.CenterX,
                    CenterY = _project.Game.Simulator.CenterY,
                    Width = _project.Game.Simulator.Width,
                    Height = _project.Game.Simulator.Height,
                    RotationDegrees = _project.Game.Simulator.RotationDegrees
                }
            },
            Actions = actions.ToList(),
            Bundles = bundles.ToList()
        };
    }

    private void UpdatePointCount()
    {
        PointCountText.Text = $"{Timeline.GetPoints().Count} points on {_selectedAxis}";
        UpdateSimulatorValue();
    }

    private void UpdateSimulatorValue()
    {
        SimulatorOverlay.Value = FunscriptCurve.Evaluate(Timeline.GetPoints(), Timeline.CursorMilliseconds);
    }

    private void SimulatorOverlay_LayoutChanged(object? sender, EventArgs e)
    {
        if (!_updatingUi) SyncSimulatorLayout();
    }

    private void SimulatorCheck_Changed(object sender, RoutedEventArgs e)
    {
        var visible = SimulatorCheck.IsChecked == true;
        SimulatorOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!_updatingUi) SyncSimulatorLayout();
    }

    private void ResetSimulator_Click(object sender, RoutedEventArgs e)
    {
        var layout = new LinearSimulatorLayout();
        SimulatorOverlay.ApplyLayout(layout);
        SimulatorCheck.IsChecked = true;
        SimulatorOverlay.Visibility = Visibility.Visible;
        SyncSimulatorLayout();
    }

    private void SyncSimulatorLayout()
    {
        _project.Game.Simulator = SimulatorOverlay.GetLayout(SimulatorCheck.IsChecked == true);
    }

    private void Validate_Click(object sender, RoutedEventArgs e)
    {
        var issues = _validator.Validate(_project);
        if (issues.Count == 0)
        {
            SetStatus($"Validation passed: {_project.Actions.Count} authored scene(s) are ready to export.");
            MessageBox.Show(this, "No EDI compatibility issues found.", "Validation", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ShowValidation(issues);
    }

    private void ShowValidation(IEnumerable<ValidationIssue> issues)
    {
        var list = issues.ToArray();
        var errors = list.Count(issue => issue.Severity == ValidationSeverity.Error);
        var warnings = list.Length - errors;
        var text = string.Join(Environment.NewLine, list.Take(30).Select(issue => issue.ToString()));
        if (list.Length > 30) text += $"{Environment.NewLine}...and {list.Length - 30} more.";
        MessageBox.Show(this, text, $"Validation: {errors} error(s), {warnings} warning(s)", MessageBoxButton.OK,
            errors > 0 ? MessageBoxImage.Error : MessageBoxImage.Warning);
        SetStatus($"Validation found {errors} error(s) and {warnings} warning(s).", errors > 0);
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_project.Actions.Count == 0)
        {
            SetStatus("Save at least one scene before exporting.", true);
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Choose the EDI Gallery collection folder (for example simple or detailed)" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var collectionDirectory = Path.GetFullPath(dialog.FolderName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var collectionName = Path.GetFileName(collectionDirectory);
            var galleryDirectory = Directory.GetParent(collectionDirectory)?.FullName
                ?? throw new InvalidOperationException("The selected collection folder must have a Gallery parent folder.");
            if (collectionName.Equals("Gallery", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Select a collection subfolder such as Gallery\\simple or Gallery\\detailed, not Gallery itself.");
            var exportProject = CreateCollectionExportProject(collectionName);
            var result = await _exporter.ExportAsync(exportProject, galleryDirectory);
            var removedLegacyFolder = OfferLegacyExportCleanup(collectionDirectory);
            SetStatus($"Exported {result.ScriptCount} script(s) directly to '{collectionDirectory}'. Definitions.csv is in '{galleryDirectory}'." +
                      (removedLegacyFolder ? " Removed the previous nested export folder." : string.Empty));
            if (result.Issues.Count > 0) ShowValidation(result.Issues);
        }
        catch (Exception exception)
        {
            SetStatus($"Export failed: {exception.Message}", true);
        }
    }

    private bool OfferLegacyExportCleanup(string collectionDirectory)
    {
        var legacyDirectory = Path.Combine(collectionDirectory, SafeDirectoryName(_project.Name) + "-Gallery");
        var manifestPath = Path.Combine(legacyDirectory, ".edi-integration-studio-export.json");
        if (!File.Exists(manifestPath)) return false;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!manifest.RootElement.TryGetProperty("ProjectId", out var projectId) ||
                !Guid.TryParse(projectId.GetString(), out var owner) || owner != _project.Id) return false;
            if (MessageBox.Show(this,
                    $"Remove the previous incorrectly nested export folder?{Environment.NewLine}{legacyDirectory}{Environment.NewLine}{Environment.NewLine}Choose No if you edited files inside it manually.",
                    "Remove legacy nested export", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return false;
            Directory.Delete(legacyDirectory, recursive: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            SetStatus($"Export succeeded, but the old nested folder could not be checked or removed: {exception.Message}", true);
            return false;
        }
    }

    private StudioProject CreateCollectionExportProject(string collectionName)
    {
        var scenes = _project.Actions.Select(scene => new AuthoredAction
        {
            Id = scene.Id,
            Name = scene.Name,
            FileName = scene.FileName,
            Type = scene.Type,
            Loop = scene.Loop,
            IsLocked = scene.IsLocked,
            Description = scene.Description,
            Variant = collectionName,
            DurationMilliseconds = scene.DurationMilliseconds,
            SourceClipPath = scene.SourceClipPath,
            SourceStartedAtUtc = scene.SourceStartedAtUtc,
            SourceEndedAtUtc = scene.SourceEndedAtUtc,
            UnitySceneName = scene.UnitySceneName,
            UnityAnimationName = scene.UnityAnimationName,
            Tracks = scene.Tracks.Select(track => new ActionTrack
            {
                Axis = track.Axis,
                Points = track.Points.ToList()
            }).ToList()
        }).ToList();
        return CloneProject(scenes, _project.Bundles.Select(bundle => new BundleDefinition
        {
            Name = bundle.Name,
            Actions = bundle.Actions.ToList()
        }));
    }

    private async void CheckEdi_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var definitions = await _ediApi.GetDefinitionsAsync(EdiBaseUrlText.Text);
            SetStatus($"EDI is reachable and currently exposes {definitions.Count} definition(s).");
        }
        catch (Exception exception)
        {
            SetStatus($"EDI check failed: {exception.Message}", true);
        }
    }

    private async void TestPlay_Click(object sender, RoutedEventArgs e)
    {
        if (ActionList.SelectedItem is not AuthoredAction action)
        {
            SetStatus("Select a saved scene to test.", true);
            return;
        }
        try
        {
            var definitions = await _ediApi.GetDefinitionsAsync(EdiBaseUrlText.Text);
            if (!definitions.Any(definition => definition.Name.Equals(action.Name, StringComparison.OrdinalIgnoreCase)))
            {
                SetStatus($"EDI does not currently expose '{action.Name}'. Export/reload the gallery before testing.", true);
                return;
            }
            await _ediApi.PlayAsync(EdiBaseUrlText.Text, action.Name);
            SetStatus($"EDI play sent for '{action.Name}'.");
        }
        catch (Exception exception)
        {
            SetStatus($"EDI play failed: {exception.Message}", true);
        }
    }

    private async void StopEdi_Click(object sender, RoutedEventArgs e) => await StopEdiAsync();

    private async Task StopEdiAsync()
    {
        try
        {
            await _ediApi.StopAsync(EdiBaseUrlText.Text);
            SetStatus("EDI stop sent.");
        }
        catch (Exception exception)
        {
            SetStatus($"EDI stop failed: {exception.Message}", true);
        }
    }

    private void BrowseGame_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select the Unity game executable", Filter = "Windows executable (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true)
        {
            GameExecutableText.Text = dialog.FileName;
            _inspection = null;
            UnityStatusText.Text = "Not analyzed";
        }
    }

    private void ModPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePresetDescription();
        if (!_updatingUi && ModPresetCombo.SelectedItem is UnityModPresetKind preset) _project.Game.ModPreset = preset;
    }

    private void UpdatePresetDescription()
    {
        ModPresetDescriptionText.Text = ModPresetCombo.SelectedItem switch
        {
            UnityModPresetKind.SceneNames => "Watch Unity scenes and automatically play an authored scene when the normalized names match.",
            UnityModPresetKind.AnimationNames => "Watch Animator, Legacy Animation, Timeline, and supported framework states and play an authored scene when normalized names match.",
            UnityModPresetKind.SceneAndAnimationNames => "Enable exact-name scene, animation, and FSM matching. Start with Discovery to check for false triggers first.",
            _ => "Safest preset: record scene, animation, and FSM changes without automatically triggering authored scenes."
        };
    }

    private void AnalyzeGame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var inspection = _gameInspector.Inspect(GameExecutableText.Text);
            ApplyUnityInspection(inspection);
            MessageBox.Show(this, string.Join(Environment.NewLine, inspection.Findings), "Unity analysis",
                MessageBoxButton.OK, inspection.IsUnity && inspection.IsBuildReady ? MessageBoxImage.Information : MessageBoxImage.Warning);
            SetStatus(inspection.IsBuildReady
                    ? $"Unity {inspection.Runtime} mod toolchain is ready."
                    : inspection.IsUnity
                        ? "Unity detected, but BepInEx or required generated references are not ready."
                        : "No supported Unity runtime markers found.",
                !inspection.IsUnity);
        }
        catch (Exception exception)
        {
            SetStatus($"Game analysis failed: {exception.Message}", true);
        }
    }

    private async void InstallBepInEx_Click(object sender, RoutedEventArgs e)
    {
        UnityInspectionResult inspection;
        try
        {
            inspection = _gameInspector.Inspect(GameExecutableText.Text);
            ApplyUnityInspection(inspection);
            if (!inspection.IsUnity)
            {
                SetStatus("A supported Unity Mono or IL2CPP runtime was not detected.", true);
                return;
            }
            if (!inspection.Architecture.Equals("Amd64", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus($"The packaged BepInEx builds are x64, but this game is {inspection.Architecture}.", true);
                return;
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Game analysis failed: {exception.Message}", true);
            return;
        }

        var package = ResolveBepInExPackage(inspection.Runtime);
        if (!File.Exists(package.Path))
        {
            SetStatus($"Packaged BepInEx archive is missing: {package.Path}", true);
            return;
        }
        var gameRoot = Path.GetDirectoryName(inspection.ExecutablePath)!;
        var staleScaffoldWarning = Directory.Exists(_project.Game.ModProjectPath) && inspection.BepInEx == BepInExFlavor.Missing
            ? $"{Environment.NewLine}{Environment.NewLine}A mod project was generated before BepInEx was installed. Generate a fresh scaffold afterward so Plugin.cs targets the detected loader."
            : string.Empty;
        var confirmation = MessageBox.Show(this,
            $"Install the recommended BepInEx 6 x64 {inspection.Runtime} nightly into:{Environment.NewLine}{gameRoot}" +
            $"{Environment.NewLine}{Environment.NewLine}Files supplied by the package will be replaced. Existing plugins, config files, and unrelated files are preserved." +
            $"{Environment.NewLine}{Environment.NewLine}The game will then start automatically. EDI Integration Studio will wait for BepInEx to finish, close the game normally, and force-close it only if needed." +
            staleScaffoldWarning,
            "Install BepInEx and initialize", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        await _unityOperationGate.WaitAsync();
        SetUnityToolsBusy(true);
        using var cancellation = new CancellationTokenSource();
        _unitySetupCancellation = cancellation;
        try
        {
            SetStatus($"Installing packaged BepInEx {inspection.Runtime} files...");
            var install = await Task.Run(() => _runtimeProvisioner.InstallBepInEx(
                inspection, package.Path, package.Sha256, cancellation.Token), cancellation.Token);
            var installedInspection = _gameInspector.Inspect(inspection.ExecutablePath);
            ApplyUnityInspection(installedInspection);
            var progress = new Progress<string>(message =>
            {
                UnityStatusText.Text = message;
                SetStatus(message);
            });
            var initialization = await _runtimeProvisioner.InitializeGameAsync(
                installedInspection, progress, cancellationToken: cancellation.Token);
            var finalInspection = _gameInspector.Inspect(inspection.ExecutablePath);
            ApplyUnityInspection(finalInspection);
            if (_projectDirectory is not null) await SaveProjectAsync();

            var closeMode = initialization.ForcedClose ? "The game required a forced close." : "The game closed normally.";
            var ready = finalInspection.IsBuildReady
                ? "The Unity mod toolchain is ready."
                : "BepInEx initialized, but analysis still reports missing build references.";
            MessageBox.Show(this,
                $"Installed {install.InstalledFileCount} BepInEx files ({install.ReplacedFileCount} replaced)." +
                $"{Environment.NewLine}Initialization time: {initialization.Elapsed:mm\\:ss}. {closeMode}" +
                $"{Environment.NewLine}{ready}",
                "BepInEx setup complete", MessageBoxButton.OK,
                finalInspection.IsBuildReady ? MessageBoxImage.Information : MessageBoxImage.Warning);
            SetStatus($"BepInEx {inspection.Runtime} setup completed. {ready}", !finalInspection.IsBuildReady);
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (OperationCanceledException)
        {
            SetStatus("BepInEx setup was cancelled.", true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "BepInEx setup failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus($"BepInEx setup failed: {exception.Message}", true);
        }
        finally
        {
            if (ReferenceEquals(_unitySetupCancellation, cancellation)) _unitySetupCancellation = null;
            SetUnityToolsBusy(false);
            _unityOperationGate.Release();
        }
    }

    private async void InstallEdi_Click(object sender, RoutedEventArgs e)
    {
        UnityInspectionResult inspection;
        try
        {
            inspection = _gameInspector.Inspect(GameExecutableText.Text);
            ApplyUnityInspection(inspection);
            if (!inspection.IsUnity)
            {
                SetStatus("Select and analyze a supported Unity game before installing EDI.", true);
                return;
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Game analysis failed: {exception.Message}", true);
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choose the fresh EDI folder containing Edi.exe" };
        if (dialog.ShowDialog(this) != true) return;
        var sourceDirectory = dialog.FolderName;
        var gameRoot = Path.GetDirectoryName(inspection.ExecutablePath)!;
        var confirmation = MessageBox.Show(this,
            $"Copy EDI from:{Environment.NewLine}{sourceDirectory}{Environment.NewLine}{Environment.NewLine}Into the game folder:{Environment.NewLine}{gameRoot}" +
            $"{Environment.NewLine}{Environment.NewLine}Existing destination files with the same names will be replaced. The source Gallery contents will not be copied, and the destination Gallery will not be changed.",
            "Install EDI", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes) return;

        await _unityOperationGate.WaitAsync();
        SetUnityToolsBusy(true);
        using var cancellation = new CancellationTokenSource();
        _unitySetupCancellation = cancellation;
        try
        {
            SetStatus("Installing EDI files while preserving Gallery contents...");
            var result = await Task.Run(() => _runtimeProvisioner.InstallEdi(
                inspection, sourceDirectory, cancellation.Token), cancellation.Token);
            MessageBox.Show(this,
                $"Installed {result.InstalledFileCount} EDI files ({result.ReplacedFileCount} replaced)." +
                $"{Environment.NewLine}Gallery preserved at:{Environment.NewLine}{result.GalleryPath}",
                "EDI installation complete", MessageBoxButton.OK, MessageBoxImage.Information);
            SetStatus($"Installed EDI into the game folder. Gallery contents were preserved.");
        }
        catch (OperationCanceledException) when (_closing)
        {
        }
        catch (OperationCanceledException)
        {
            SetStatus("EDI installation was cancelled.", true);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "EDI installation failed", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus($"EDI installation failed: {exception.Message}", true);
        }
        finally
        {
            if (ReferenceEquals(_unitySetupCancellation, cancellation)) _unitySetupCancellation = null;
            SetUnityToolsBusy(false);
            _unityOperationGate.Release();
        }
    }

    private async void GenerateMod_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _projectTransitionRunning) != 0)
        {
            SetStatus("Wait for the active project to finish loading before generating a mod.", true);
            return;
        }

        var requestedProjectGeneration = Volatile.Read(ref _projectGeneration);
        UnityInspectionResult inspection;
        try
        {
            inspection = _gameInspector.Inspect(GameExecutableText.Text);
            ApplyUnityInspection(inspection);
            if (!inspection.IsUnity)
            {
                SetStatus("A supported Unity Mono or IL2CPP runtime was not detected.", true);
                return;
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Game analysis failed: {exception.Message}", true);
            return;
        }
        var dialog = new OpenFolderDialog { Title = "Choose a folder for the generated BepInEx mod source" };
        if (dialog.ShowDialog(this) != true) return;
        var projectOperationStarted = false;
        await _unityOperationGate.WaitAsync();
        await _projectOperationGate.WaitAsync();
        try
        {
            if (requestedProjectGeneration != Volatile.Read(ref _projectGeneration))
            {
                SetStatus("Mod generation was cancelled because the active project changed.", true);
                return;
            }

            projectOperationStarted = true;
            Interlocked.Exchange(ref _projectTransitionRunning, 1);
            SetUnityToolsBusy(true);
            _project.Name = string.IsNullOrWhiteSpace(ProjectNameText.Text) ? _project.Name : ProjectNameText.Text.Trim();
            var preset = ModPresetCombo.SelectedItem is UnityModPresetKind selectedPreset
                ? selectedPreset
                : UnityModPresetKind.Discovery;
            var result = await _modScaffolder.GenerateAsync(_project, inspection, dialog.FolderName, preset, EdiBaseUrlText.Text);
            _project.Game.ModPreset = preset;
            _project.Game.ModProjectPath = result.ProjectDirectory;
            _project.Game.TelemetryPath = result.TelemetryFile;
            if (_projectDirectory is not null) await SaveProjectWhileLockedAsync();
            SetStatus($"Generated {inspection.Runtime} {preset} mod project. Use Build + install when BepInEx is ready.");
        }
        catch (Exception exception)
        {
            SetStatus($"Mod scaffold failed: {exception.Message}", true);
        }
        finally
        {
            if (projectOperationStarted)
            {
                Interlocked.Exchange(ref _projectTransitionRunning, 0);
                SetUnityToolsBusy(false);
            }
            _projectOperationGate.Release();
            _unityOperationGate.Release();
        }
    }

    private async void BuildInstallMod_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _projectTransitionRunning) != 0)
        {
            SetStatus("Wait for the active project to finish loading before building a mod.", true);
            return;
        }

        var requestedProjectGeneration = Volatile.Read(ref _projectGeneration);
        var projectPath = _project.Game.ModProjectPath;
        if (!Directory.Exists(projectPath))
        {
            var dialog = new OpenFolderDialog { Title = "Choose the generated mod folder containing IntegrationMod.csproj" };
            if (dialog.ShowDialog(this) != true) return;
            projectPath = dialog.FolderName;
        }

        await _unityOperationGate.WaitAsync();
        await _projectOperationGate.WaitAsync();
        var projectOperationStarted = false;
        try
        {
            if (requestedProjectGeneration != Volatile.Read(ref _projectGeneration))
            {
                SetStatus("Mod build was cancelled because the active project changed.", true);
                return;
            }

            projectOperationStarted = true;
            Interlocked.Exchange(ref _projectTransitionRunning, 1);
            SetUnityToolsBusy(true);
            var manifest = await UnityModDeployer.LoadManifestAsync(projectPath);
            var inspection = _gameInspector.Inspect(manifest.GameExecutable);
            ApplyUnityInspection(inspection);
            if (!inspection.IsBuildReady)
            {
                MessageBox.Show(this, string.Join(Environment.NewLine, inspection.Findings), "Mod toolchain is not ready",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                SetStatus("Install the matching BepInEx build and run the game once before building.", true);
                return;
            }
            await _modScaffolder.RepairProjectFileAsync(inspection, projectPath);
            manifest = await UnityModDeployer.LoadManifestAsync(projectPath);

            var preset = ModPresetCombo.SelectedItem is UnityModPresetKind selectedPreset
                ? selectedPreset
                : UnityModPresetKind.Discovery;
            _project.Game.ModPreset = preset;
            await _modScaffolder.UpdatePresetAsync(_project, projectPath, preset);
            SetStatus("Building the generated Unity plugin...");
            var build = await _modDeployer.BuildAsync(projectPath);
            if (!build.Success)
            {
                var output = build.Output.Length > 14000 ? build.Output[^14000..] : build.Output;
                MessageBox.Show(this, output, $"Mod build failed (exit {build.ExitCode})",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus("Mod build failed. The compiler output is shown in the build dialog.", true);
                return;
            }

            var install = _modDeployer.Install(build);
            _project.Game.ModProjectPath = projectPath;
            _project.Game.TelemetryPath = build.Manifest.TelemetryFile;
            _project.Game.InstalledPluginPath = install.PluginPath;
            if (_projectDirectory is not null) await SaveProjectWhileLockedAsync();
            SetStatus($"Built and installed {Path.GetFileName(install.PluginPath)}. Launch the game, then Watch discovery.");
        }
        catch (Exception exception)
        {
            SetStatus($"Mod build/install failed: {exception.Message}", true);
        }
        finally
        {
            if (projectOperationStarted)
            {
                Interlocked.Exchange(ref _projectTransitionRunning, 0);
                SetUnityToolsBusy(false);
            }
            _projectOperationGate.Release();
            _unityOperationGate.Release();
        }
    }

    private async void WatchTelemetry_Click(object sender, RoutedEventArgs e)
    {
        if (_telemetryTimer.IsEnabled)
        {
            StopTelemetryWatch();
            SetStatus("Runtime discovery watch stopped. The current output has been preserved.");
            return;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(_project.Game.TelemetryPath) && Directory.Exists(_project.Game.ModProjectPath))
            {
                var manifest = await UnityModDeployer.LoadManifestAsync(_project.Game.ModProjectPath);
                _project.Game.TelemetryPath = manifest.TelemetryFile;
            }
            if (string.IsNullOrWhiteSpace(_project.Game.TelemetryPath))
            {
                SetStatus("Generate the mod project before watching runtime discovery.", true);
                return;
            }

            var path = Path.GetFullPath(_project.Game.TelemetryPath);
            if (!path.Equals(_watchedTelemetryPath, StringComparison.OrdinalIgnoreCase))
            {
                ResetTelemetryOutput(clearSuppressions: true);
                _watchedTelemetryPath = path;
            }
            _telemetryOutputPaused = false;
            _telemetryTimer.Start();
            WatchTelemetryButton.Content = "Stop watching";
            PauseTelemetryButton.Content = "Pause output";
            PauseTelemetryButton.IsEnabled = true;
            RefreshUnityTelemetry();
            SetStatus(File.Exists(path)
                ? "Watching live Unity scene and detected-framework runtime discovery."
                : "Discovery watch started. Launch the game to create telemetry.");
            UpdateTelemetryOutputStatus();
        }
        catch (Exception exception)
        {
            SetStatus($"Could not start discovery watch: {exception.Message}", true);
        }
    }

    private void RefreshUnityTelemetry(bool force = false)
    {
        if (_telemetryOutputPaused && !force) return;
        var path = string.IsNullOrWhiteSpace(_watchedTelemetryPath)
            ? _project.Game.TelemetryPath
            : _watchedTelemetryPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            UpdateTelemetryOutputStatus();
            return;
        }
        try
        {
            var events = UnityTelemetryLog.Read(path);
            var followTail = _telemetryFollowTail;
            if (events.Count < _telemetryLineCount)
            {
                _telemetryLineCount = 0;
                _telemetryEntries.Clear();
                _telemetryFollowTail = true;
                followTail = true;
            }
            var added = false;
            var startIndex = _telemetryLineCount;
            if (startIndex == 0 && _telemetryEntries.Count == 0)
                startIndex = Math.Max(0, events.Count - MaximumFollowedTelemetryEntries * 5);
            for (var index = startIndex; index < events.Count; index++)
            {
                var item = events[index];
                var time = item.Timestamp.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                var candidate = item.Candidate;
                if (string.IsNullOrWhiteSpace(candidate) && item.Kind is "SCENE" or "ACTIVE_SCENE") candidate = item.Scene;
                if (IsTelemetrySuppressed(item.Kind, candidate, item.ObjectPath)) continue;
                var location = string.IsNullOrWhiteSpace(item.ObjectPath) ? item.Scene : item.ObjectPath;
                var timingText = UnityTelemetryLog.TryGetPlaybackTiming(item, out var timing)
                    ? $"  |  {timing.CycleDuration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s " +
                      $"{(timing.IsLooping ? "loop" : "one-shot")} phase {timing.Phase.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s"
                    : string.Empty;
                _telemetryEntries.Add(new UnityTelemetryEntry(item.Timestamp, item.Kind, item.Scene, item.ObjectPath, candidate,
                    item.Details, $"{time} [{item.Kind}] {candidate}  |  {location}{timingText}"));
                added = true;
            }
            _telemetryLineCount = events.Count;
            if (followTail)
            {
                while (_telemetryEntries.Count > MaximumFollowedTelemetryEntries) _telemetryEntries.RemoveAt(0);
            }
            if (added && followTail && _telemetryEntries.Count > 0)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    if (_telemetryFollowTail && _telemetryEntries.Count > 0)
                        UnityTelemetryList.ScrollIntoView(_telemetryEntries[^1]);
                });
            }
            if (_workingClip is not null) UpdateRuntimeCorrelation(autoApplyName: false);
            UpdateTelemetryOutputStatus();
        }
        catch (IOException exception)
        {
            UnityStatusText.Text = $"Telemetry read delayed: {exception.Message}";
        }
    }

    private void UnityTelemetryList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (Math.Abs(e.VerticalChange) < 0.01) return;
        _telemetryFollowTail = e.VerticalOffset >= Math.Max(0, e.ExtentHeight - e.ViewportHeight - 2);
    }

    private void PauseTelemetry_Click(object sender, RoutedEventArgs e)
    {
        if (!_telemetryTimer.IsEnabled) return;
        _telemetryOutputPaused = !_telemetryOutputPaused;
        PauseTelemetryButton.Content = _telemetryOutputPaused ? "Resume output" : "Pause output";
        UpdateTelemetryOutputStatus();
        if (_telemetryOutputPaused)
        {
            SetStatus("Telemetry output paused. The game log is still accumulating and will catch up when resumed.");
            return;
        }
        RefreshUnityTelemetry();
        SetStatus("Telemetry output resumed.");
    }

    private void ClearTelemetryOutput_Click(object sender, RoutedEventArgs e)
    {
        _telemetryEntries.Clear();
        _telemetryFollowTail = true;
        var path = string.IsNullOrWhiteSpace(_watchedTelemetryPath)
            ? _project.Game.TelemetryPath
            : _watchedTelemetryPath;
        try
        {
            _telemetryLineCount = string.IsNullOrWhiteSpace(path) || !File.Exists(path)
                ? 0
                : UnityTelemetryLog.Read(path).Count;
        }
        catch (IOException exception)
        {
            UnityStatusText.Text = $"Telemetry read delayed: {exception.Message}";
        }
        UpdateTelemetryOutputStatus();
        SetStatus("Telemetry output cleared. The telemetry file was left intact.");
    }

    private void SuppressTelemetrySelected_Click(object sender, RoutedEventArgs e)
    {
        if (UnityTelemetryList.SelectedItem is not UnityTelemetryEntry entry)
        {
            SetStatus("Select a telemetry event to hide first.", true);
            return;
        }
        _suppressedTelemetryStreams.Add(TelemetryStreamKey(entry.Kind, entry.Candidate, entry.ObjectPath));
        RemoveTelemetryEntries(item => item.Kind.Equals(entry.Kind, StringComparison.OrdinalIgnoreCase) &&
                                       item.Candidate.Equals(entry.Candidate, StringComparison.OrdinalIgnoreCase) &&
                                       item.ObjectPath.Equals(entry.ObjectPath, StringComparison.OrdinalIgnoreCase));
        UpdateTelemetryFilterStatus();
        UpdateTelemetryOutputStatus();
        SetStatus($"Hidden {entry.Kind} events for '{entry.Candidate}' at '{entry.ObjectPath}'.");
    }

    private void SuppressTelemetryKind_Click(object sender, RoutedEventArgs e)
    {
        if (UnityTelemetryList.SelectedItem is not UnityTelemetryEntry entry)
        {
            SetStatus("Select a telemetry event kind to hide first.", true);
            return;
        }
        _suppressedTelemetryKinds.Add(entry.Kind);
        RemoveTelemetryEntries(item => item.Kind.Equals(entry.Kind, StringComparison.OrdinalIgnoreCase));
        UpdateTelemetryFilterStatus();
        UpdateTelemetryOutputStatus();
        SetStatus($"Hidden all {entry.Kind} telemetry events.");
    }

    private void ClearTelemetrySuppressions_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressedTelemetryKinds.Count == 0 && _suppressedTelemetryStreams.Count == 0) return;
        _suppressedTelemetryKinds.Clear();
        _suppressedTelemetryStreams.Clear();
        UpdateTelemetryFilterStatus();
        _telemetryEntries.Clear();
        _telemetryLineCount = 0;
        _telemetryFollowTail = true;
        RefreshUnityTelemetry(force: true);
        SetStatus("Telemetry filters reset and recent history reloaded.");
    }

    private bool IsTelemetrySuppressed(string kind, string candidate, string objectPath) =>
        _suppressedTelemetryKinds.Contains(kind) ||
        _suppressedTelemetryStreams.Contains(TelemetryStreamKey(kind, candidate, objectPath));

    private static string TelemetryStreamKey(string kind, string candidate, string objectPath) =>
        string.Join('\u001f', kind, candidate, objectPath);

    private void RemoveTelemetryEntries(Func<UnityTelemetryEntry, bool> predicate)
    {
        for (var index = _telemetryEntries.Count - 1; index >= 0; index--)
        {
            if (predicate(_telemetryEntries[index])) _telemetryEntries.RemoveAt(index);
        }
    }

    private void UpdateTelemetryFilterStatus()
    {
        var count = _suppressedTelemetryKinds.Count + _suppressedTelemetryStreams.Count;
        TelemetryFilterStatusText.Text = count == 0 ? "No hidden events" : $"{count} filter(s) active";
    }

    private void UpdateTelemetryOutputStatus()
    {
        var state = _telemetryTimer.IsEnabled
            ? _telemetryOutputPaused ? "Paused" : "Live"
            : "Stopped";
        TelemetryOutputStatusText.Text = $"{state} | {_telemetryEntries.Count} shown";
    }

    private void TelemetryResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        UnityTelemetryList.Height = Math.Clamp(UnityTelemetryList.ActualHeight + e.VerticalChange, 120, 520);
    }

    private async void MapTelemetry_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _projectTransitionRunning) != 0) return;
        if (UnityTelemetryList.SelectedItem is not UnityTelemetryEntry entry ||
            TelemetryActionCombo.SelectedItem is not AuthoredAction action)
        {
            SetStatus("Select a discovered scene, animation, or FSM state and a saved authored scene first.", true);
            return;
        }
        var kind = entry.Kind switch
        {
            "SCENE" or "ACTIVE_SCENE" => UnityTriggerKind.Scene,
            _ when UnityTelemetryLog.IsRuntimeCandidateEvent(entry.Kind) => UnityTriggerKind.AnimationClip,
            _ => (UnityTriggerKind?)null
        };
        if (kind is null || string.IsNullOrWhiteSpace(entry.Candidate))
        {
            SetStatus("Only scene, animation, and FSM state discovery entries can be mapped.", true);
            return;
        }

        var telemetryEvent = new UnityTelemetryEvent(entry.Timestamp, entry.Kind, entry.Scene, entry.ObjectPath,
            entry.Candidate, entry.Details);
        var cycleDurationMilliseconds = UnityTelemetryLog.TryGetPlaybackTiming(telemetryEvent, out var timing)
            ? Math.Max(1, (int)Math.Round(timing.CycleDuration.TotalMilliseconds))
            : (int?)null;
        var objectPath = kind == UnityTriggerKind.AnimationClip ? entry.ObjectPath : string.Empty;
        _project.Game.SetTriggerMapping(kind.Value, entry.Candidate, action.Name, objectPath, cycleDurationMilliseconds);
        UpdateTriggerMappingStatus();
        if (_projectDirectory is null || await SaveProjectAsync())
        {
            var timingText = cycleDurationMilliseconds is { } duration ? $" at {duration} ms" : string.Empty;
            SetStatus($"Mapped {kind} '{entry.Candidate}'{timingText} to '{action.Name}'. Use Build + install to apply it.");
        }
    }

    private async void ClearTriggerMappings_Click(object sender, RoutedEventArgs e)
    {
        if (Volatile.Read(ref _projectTransitionRunning) != 0) return;
        if (_project.Game.TriggerMappings.Count == 0) return;
        if (MessageBox.Show(this, "Clear all discovered Unity trigger mappings for this project?", "Clear mappings",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _project.Game.TriggerMappings.Clear();
        UpdateTriggerMappingStatus();
        if (_projectDirectory is null || await SaveProjectAsync())
            SetStatus("Unity trigger mappings cleared. Use Build + install to apply the change.");
    }

    private void UpdateTriggerMappingStatus()
    {
        var pending = _pendingCapturedTriggerMapping is null ? string.Empty : " | pending save: 1";
        TriggerMappingStatusText.Text = $"Project-wide mappings: {_project.Game.TriggerMappings.Count}{pending}";
    }

    private void StopTelemetryWatch(bool clearOutput = false)
    {
        _telemetryTimer.Stop();
        _telemetryOutputPaused = false;
        WatchTelemetryButton.Content = "Watch discovery";
        PauseTelemetryButton.Content = "Pause output";
        PauseTelemetryButton.IsEnabled = false;
        if (clearOutput) ResetTelemetryOutput(clearSuppressions: true);
        UpdateTelemetryOutputStatus();
    }

    private void ResetTelemetryOutput(bool clearSuppressions)
    {
        _telemetryLineCount = 0;
        _telemetryEntries.Clear();
        _telemetryFollowTail = true;
        _watchedTelemetryPath = string.Empty;
        if (clearSuppressions)
        {
            _suppressedTelemetryKinds.Clear();
            _suppressedTelemetryStreams.Clear();
            UpdateTelemetryFilterStatus();
        }
    }

    private void ApplyUnityInspection(UnityInspectionResult inspection)
    {
        _inspection = inspection;
        GameExecutableText.Text = inspection.ExecutablePath;
        _project.Game.ExecutablePath = inspection.ExecutablePath;
        _project.Game.ProcessName = Path.GetFileNameWithoutExtension(inspection.ExecutablePath);
        _project.Game.Runtime = inspection.Runtime;
        _project.Game.Architecture = inspection.Architecture;
        _project.Game.UnityVersion = inspection.UnityVersion;
        _project.Game.TargetFramework = inspection.RecommendedTargetFramework;
        _project.Game.BepInExFlavor = inspection.BepInEx.ToString();
        UnityStatusText.Text = inspection.IsUnity
            ? $"Unity {inspection.Runtime} / {inspection.Architecture} / {inspection.RecommendedTargetFramework} / {inspection.BepInEx}" +
              (inspection.IsBuildReady ? " / ready" : " / setup required")
            : "Unity runtime not detected";
    }

    private static BepInExPackage ResolveBepInExPackage(UnityRuntimeKind runtime)
    {
        var (fileName, hash) = runtime == UnityRuntimeKind.Il2Cpp
            ? (Il2CppBepInExPackage, Il2CppBepInExSha256)
            : (MonoBepInExPackage, MonoBepInExSha256);
        return new(Path.Combine(AppContext.BaseDirectory, "RuntimePackages", fileName), hash);
    }

    private void SetUnityToolsBusy(bool busy)
    {
        NewProjectButton.IsEnabled = !busy;
        OpenProjectButton.IsEnabled = !busy;
        RecentProjectsCombo.IsEnabled = !busy && _recentProjectItems.Count > 1;
        RefreshWindowsButton.IsEnabled = !busy;
        WindowCombo.IsEnabled = !busy;
        GameBrowseButton.IsEnabled = !busy;
        GameExecutableText.IsEnabled = !busy;
        ModPresetCombo.IsEnabled = !busy;
        AnalyzeGameButton.IsEnabled = !busy;
        InstallBepInExButton.IsEnabled = !busy;
        InstallEdiButton.IsEnabled = !busy;
        GenerateModButton.IsEnabled = !busy;
        BuildInstallButton.IsEnabled = !busy;
        WatchTelemetryButton.IsEnabled = !busy;
    }

    private void TopmostCheck_Changed(object sender, RoutedEventArgs e) => Topmost = TopmostCheck.IsChecked == true;

    private string ResolveProjectAsset(string relativePath)
    {
        if (_projectDirectory is null) throw new InvalidOperationException("No project directory is open.");
        var root = Path.GetFullPath(_projectDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Project asset path escapes the project directory.");
        }
        return fullPath;
    }

    private static BitmapSource CreateBitmap(byte[] jpeg)
    {
        using var memory = new MemoryStream(jpeg, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = memory;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static double ParseSeconds(string text, double fallback, double minimum, double maximum) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;

    private static string FormatTime(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds).ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture);

    private static string SafeDirectoryName(string value)
    {
        var result = new string(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray())
            .Trim().TrimEnd('.', ' ');
        var reserved = new HashSet<string>(
            ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
             "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
            StringComparer.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(result) || result is "." or ".." || reserved.Contains(result)
            ? "EDI Integration"
            : result;
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(isError ? "DangerTextBrush" : "MutedTextBrush");
    }

    private void RegisterGlobalHotkeys()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        source.AddHook(WindowProcedure);
        var modifiers = 0x0002u | 0x0004u;
        if (!RegisterHotKey(source.Handle, CaptureHotkeyId, modifiers, 0x77))
        {
            SetStatus("Could not register Ctrl+Shift+F8 capture hotkey.", true);
        }
        if (!RegisterHotKey(source.Handle, StopHotkeyId, modifiers, 0x7B))
        {
            SetStatus("Could not register Ctrl+Shift+F12 EDI stop hotkey.", true);
        }
    }

    private nint WindowProcedure(nint handle, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != 0x0312) return nint.Zero;
        if (wParam == CaptureHotkeyId)
        {
            handled = true;
            _ = StartOrCaptureFromHotkeyAsync();
        }
        else if (wParam == StopHotkeyId)
        {
            handled = true;
            _ = StopEdiAsync();
        }
        return nint.Zero;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        _closing = true;
        IsEnabled = false;
        _unitySetupCancellation?.Cancel();
        _clipTimer.Stop();
        _telemetryTimer.Stop();
        await StopCaptureAsync();
        await _projectOperationGate.WaitAsync();
        _projectOperationGate.Release();
        await _projectSwitchGate.WaitAsync();
        _projectSwitchGate.Release();
        await _unityOperationGate.WaitAsync();
        _unityOperationGate.Release();
        _ediApi.Dispose();
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            UnregisterHotKey(source.Handle, CaptureHotkeyId);
            UnregisterHotKey(source.Handle, StopHotkeyId);
        }
        Closing -= Window_Closing;
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);

    private sealed record UnityTelemetryEntry(
        DateTimeOffset Timestamp,
        string Kind,
        string Scene,
        string ObjectPath,
        string Candidate,
        string Details,
        string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record BepInExPackage(string Path, string Sha256);
    private sealed record RecentProjectItem(string Display, string Directory)
    {
        public override string ToString() => Display;
    }
}
