using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dupli.Agent.Configuration;
using Dupli.Agent.Core.Secrets;
using Dupli.Contracts;
using Dupli.Contracts.Agents;
using Dupli.Contracts.Jobs;
using Dupli.Contracts.Logs;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace Dupli.Agent.Server;

/// <summary>The server rejected a request for a reason retrying will not fix (4xx other than 401/408/429).</summary>
public sealed class ServerRejectedException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Typed client for the agent API. Handles the 15-minute JWT (fetched with AgentId+Secret, refreshed
/// before expiry and once on 401) and retries transient failures with exponential backoff.
/// </summary>
public sealed class ServerClient
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly ServerConfig _server;
    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly ILogger<ServerClient> _logger;
    private readonly ResiliencePipeline _retry;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AgentTokenResponse? _token;

    public ServerClient(HttpClient http, ServerConfig server, ISecretStore secrets, TimeProvider time, ILogger<ServerClient> logger)
    {
        _http = http;
        _http.BaseAddress ??= new Uri(server.Url.TrimEnd('/') + "/");
        _server = server;
        _secrets = secrets;
        _time = time;
        _logger = logger;
        _retry = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<HttpRequestException>().Handle<TimeoutException>()
                    .Handle<TaskCanceledException>(e => e.InnerException is TimeoutException),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    _logger.LogWarning("Server call failed ({Error}), retry {Attempt} in {Delay}",
                        args.Outcome.Exception?.Message, args.AttemptNumber + 1, args.RetryDelay);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();
    }

    public string AgentId => _server.AgentId;

    public Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest request, CancellationToken ct) =>
        SendAsync<HeartbeatResponse>(HttpMethod.Post, "api/agents/heartbeat", request, ct);

    public Task<List<AgentJobDto>> GetJobsAsync(CancellationToken ct) =>
        SendAsync<List<AgentJobDto>>(HttpMethod.Get, $"api/agents/{_server.AgentId}/jobs", null, ct);

    public Task<JobControlResponse> StartedAsync(string jobId, DateTimeOffset startedAt, CancellationToken ct) =>
        SendAsync<JobControlResponse>(HttpMethod.Post, $"api/jobs/{jobId}/started", new JobStartedRequest { StartedAt = startedAt }, ct);

    public Task<JobControlResponse> ProgressAsync(string jobId, string? message, CancellationToken ct) =>
        SendAsync<JobControlResponse>(HttpMethod.Post, $"api/jobs/{jobId}/progress", new JobProgressRequest { Message = message }, ct);

    public Task ReportAsync(string jobId, JobResultDto result, CancellationToken ct)
    {
        var endpoint = result.Outcome is JobOutcome.Succeeded or JobOutcome.SucceededWithWarnings ? "completed" : "failed";
        return SendAsync<object>(HttpMethod.Post, $"api/jobs/{jobId}/{endpoint}", result, ct);
    }

    public Task UploadLogsAsync(AgentLogBatchDto batch, CancellationToken ct) =>
        SendAsync<object>(HttpMethod.Post, "api/agents/logs", batch, ct);

    public Task<RotateSecretResponse> RotateSecretAsync(CancellationToken ct) =>
        SendAsync<RotateSecretResponse>(HttpMethod.Post, "api/agents/secret/rotate", null, ct);

    private Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
        _retry.ExecuteAsync(async token =>
        {
            using var response = await SendOnceAsync(method, path, body, forceRefresh: false, token);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                using var retried = await SendOnceAsync(method, path, body, forceRefresh: true, token);
                return await ReadAsync<T>(retried, token);
            }
            return await ReadAsync<T>(response, token);
        }, ct).AsTask();

    private async Task<HttpResponseMessage> SendOnceAsync(HttpMethod method, string path, object? body, bool forceRefresh, CancellationToken ct)
    {
        var token = await GetTokenAsync(forceRefresh, ct);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: DupliJson.Options);
        return await _http.SendAsync(request, ct);
    }

    private async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _token is { } cached && cached.ExpiresAt - RefreshMargin > _time.GetUtcNow())
                return cached.AccessToken;

            using var response = await _http.PostAsJsonAsync("api/agents/token", new AgentTokenRequest
            {
                AgentId = _server.AgentId,
                AgentSecret = _secrets.GetRequired(_server.AgentSecretName),
            }, DupliJson.Options, ct);
            _token = await ReadAsync<AgentTokenResponse>(response, ct);
            return _token.AccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
                return default!;
            return (await response.Content.ReadFromJsonAsync<T>(DupliJson.Options, ct))!;
        }

        var detail = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;
        if (status >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException($"Server returned {status}: {detail}", null, response.StatusCode);
        throw new ServerRejectedException(response.StatusCode, $"Server returned {status}: {detail}");
    }
}
