using System.IO;
using System.Text.Json;

namespace GTracker.App.Shell;

public sealed class EnginePreferenceStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _settingsPath;

    public EnginePreferenceStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EdiIntegrationStudio", "engine-preference.json");
    }

    public async Task<IntegrationEngine> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_settingsPath)) return IntegrationEngine.Unity;
            try
            {
                await using var stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var settings = await JsonSerializer.DeserializeAsync<EnginePreferenceSettings>(stream, JsonOptions, cancellationToken);
                return settings?.SchemaVersion == SchemaVersion &&
                       Enum.TryParse<IntegrationEngine>(settings.Engine, ignoreCase: true, out var engine) &&
                       Enum.IsDefined(engine)
                    ? engine
                    : IntegrationEngine.Unity;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                return IntegrationEngine.Unity;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(IntegrationEngine engine, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(_settingsPath))!;
            Directory.CreateDirectory(parent);
            var temporary = _settingsPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream,
                        new EnginePreferenceSettings { Engine = engine.ToString() }, JsonOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _settingsPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class EnginePreferenceSettings
    {
        public int SchemaVersion { get; set; } = EnginePreferenceStore.SchemaVersion;
        public string Engine { get; set; } = IntegrationEngine.Unity.ToString();
    }
}
