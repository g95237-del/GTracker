using System.Net.Http.Json;
using System.Net.Http;

namespace GTracker.App.Edi;

public sealed record EdiDefinition(string Name, string Type, string FileName, int StartTime, int EndTime, int Duration, bool Loop, string? Description);

public sealed class EdiApiClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public async Task<IReadOnlyList<EdiDefinition>> GetDefinitionsAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(Normalize(baseUrl) + "/Definitions", cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<EdiDefinition>>(cancellationToken: cancellationToken) ?? [];
    }

    public async Task PlayAsync(string baseUrl, string actionName, int seekMilliseconds = 0, CancellationToken cancellationToken = default)
    {
        var route = $"{Normalize(baseUrl)}/Play/{Uri.EscapeDataString(actionName)}?seek={Math.Max(0, seekMilliseconds)}";
        using var response = await _http.PostAsync(route, new StringContent(string.Empty), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task StopAsync(string baseUrl, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(Normalize(baseUrl) + "/Stop", new StringContent(string.Empty), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();

    private static string Normalize(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        return baseUrl.TrimEnd('/');
    }
}
