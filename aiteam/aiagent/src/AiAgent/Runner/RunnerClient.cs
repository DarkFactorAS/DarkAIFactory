using System.Net;
using System.Net.Http.Json;

namespace AiAgent.Runner;

/// <summary>Orchestrator-side client for the runner container.</summary>
public sealed class RunnerClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public RunnerClient(Uri? runnerUrl = null, HttpMessageHandler? handler = null)
    {
        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.BaseAddress = runnerUrl ?? new Uri(
            Environment.GetEnvironmentVariable("AIAGENT_RUNNER_URL") ?? "http://aiagent-runner:8080");
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<RunnerResponse> RunAsync(RunnerRequest request, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync("/run", request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new RunnerBusyException("The runner has no free job slot; retry in the next cycle.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"The runner returned status {(int)response.StatusCode}. " +
                (body.Length <= 2_000 ? body : body[..2_000] + "..."));
        }

        return await response.Content.ReadFromJsonAsync<RunnerResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The runner returned no response.");
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
