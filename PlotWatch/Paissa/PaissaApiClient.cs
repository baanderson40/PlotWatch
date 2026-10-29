using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;

namespace PlotWatch.Paissa;

public sealed record PaissaApiResponse<T>(
    string Url,
    HttpStatusCode? StatusCode,
    TimeSpan Duration,
    DateTimeOffset FetchedAtUtc,
    T? Data,
    string? Error,
    bool IsSuccess,
    DateTimeOffset? RetryAfterUtc = null);

public sealed class PaissaApiClient : IDisposable
{
    public const string BaseUrl = "https://paissadb.zhu.codes";
    public static readonly TimeSpan DefaultMinimumRequestInterval = TimeSpan.FromSeconds(10);
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient httpClient;
    private readonly SemaphoreSlim requestGate = new(2, 2);
    private readonly SemaphoreSlim startGate = new(1, 1);
    private readonly TimeSpan minimumRequestInterval;
    private DateTimeOffset nextRequestAtUtc;
    private long cooldownUntilUnixMilliseconds;

    public DateTimeOffset CooldownUntilUtc
    {
        get
        {
            var milliseconds = Interlocked.Read(ref cooldownUntilUnixMilliseconds);
            return milliseconds == 0 ? default : DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
    }
    public event Action<string, TimeSpan>? RequestStarted;

    public PaissaApiClient(HttpMessageHandler? handler = null, TimeSpan? minimumRequestInterval = null)
    {
        this.minimumRequestInterval = minimumRequestInterval ?? DefaultMinimumRequestInterval;
        if (this.minimumRequestInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumRequestInterval));
        httpClient = handler is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(15) }
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("PlotWatch-Dalamud");
    }

    public Task<PaissaApiResponse<IReadOnlyList<PaissaWorld>>> GetWorldsAsync(CancellationToken cancellationToken = default)
        => GetAsync("/worlds", PaissaApiParser.ParseWorlds, cancellationToken);

    public Task<PaissaApiResponse<PaissaWorldInspection>> GetWorldAsync(int worldId, CancellationToken cancellationToken = default)
        => GetAsync<PaissaWorldInspection>($"/worlds/{worldId}", json => PaissaApiParser.InspectWorld(json), cancellationToken);

    public Task<PaissaApiResponse<string>> GetOpenApiAsync(CancellationToken cancellationToken = default)
        => GetAsync<string>("/openapi.json", json => json, cancellationToken);

    private async Task<PaissaApiResponse<T>> GetAsync<T>(
        string path,
        Func<string, T> parse,
        CancellationToken cancellationToken)
    {
        var url = BaseUrl + path;
        var fetchedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = new Stopwatch();
        HttpStatusCode? statusCode = null;
        var enteredGate = false;
        HttpResponseMessage? response = null;
        try
        {
            await requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            var pacingWait = await WaitForRequestSlotAsync(cancellationToken).ConfigureAwait(false);
            fetchedAtUtc = DateTimeOffset.UtcNow;
            stopwatch.Start();
            RequestStarted?.Invoke(url, pacingWait);
            response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            statusCode = response.StatusCode;
            var retryAfterUtc = GetRetryAfterUtc(response);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && retryAfterUtc is null)
                retryAfterUtc = DateTimeOffset.UtcNow.AddMinutes(1);
            if (retryAfterUtc is { } retry)
                ExtendCooldown(retry);
            if (response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                stopwatch.Stop();
                return new(url, statusCode, stopwatch.Elapsed, fetchedAtUtc, default, $"Response exceeded {MaxResponseBytes} byte limit.", false, retryAfterUtc);
            }
            var body = await ReadBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            fetchedAtUtc = DateTimeOffset.UtcNow;
            if (!response.IsSuccessStatusCode)
                return new(url, statusCode, stopwatch.Elapsed, fetchedAtUtc, default, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", false, retryAfterUtc);

            try
            {
                return new(url, statusCode, stopwatch.Elapsed, fetchedAtUtc, parse(body), null, true, retryAfterUtc);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException or InvalidOperationException)
            {
                return new(url, statusCode, stopwatch.Elapsed, fetchedAtUtc, default, $"Response parse failed: {ex.Message}", false, retryAfterUtc);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new(url, statusCode, stopwatch.Elapsed, fetchedAtUtc, default, "Request cancelled.", false);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new(url, statusCode, stopwatch.Elapsed, fetchedAtUtc, default, "Request timed out after 15 seconds.", false);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new(url, statusCode, stopwatch.Elapsed, fetchedAtUtc, default, ex.Message, false);
        }
        finally
        {
            response?.Dispose();
            if (enteredGate)
                requestGate.Release();
        }
    }

    private async Task<TimeSpan> WaitForRequestSlotAsync(CancellationToken cancellationToken)
    {
        await startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var start = DateTimeOffset.UtcNow;
            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                var cooldown = CooldownUntilUtc;
                var readyAt = nextRequestAtUtc > cooldown ? nextRequestAtUtc : cooldown;
                if (readyAt <= now)
                    break;
                await Task.Delay(readyAt - now, cancellationToken).ConfigureAwait(false);
                // Cooldown can be extended by another in-flight response while this request waits.
            }
            nextRequestAtUtc = DateTimeOffset.UtcNow + minimumRequestInterval;
            return DateTimeOffset.UtcNow - start;
        }
        finally
        {
            startGate.Release();
        }
    }

    private void ExtendCooldown(DateTimeOffset untilUtc)
    {
        var candidate = untilUtc.ToUnixTimeMilliseconds();
        while (true)
        {
            var current = Interlocked.Read(ref cooldownUntilUnixMilliseconds);
            if (current >= candidate)
                return;
            if (Interlocked.CompareExchange(ref cooldownUntilUnixMilliseconds, candidate, current) == current)
                return;
        }
    }

    private static DateTimeOffset? GetRetryAfterUtc(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Date is { } date)
            return date;
        if (retryAfter?.Delta is { } delta)
            return DateTimeOffset.UtcNow + delta;
        return null;
    }

    private static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            if (destination.Length + read > MaxResponseBytes)
                throw new HttpRequestException($"Response exceeded {MaxResponseBytes} byte limit.");
            destination.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(destination.GetBuffer(), 0, (int)destination.Length);
    }

    public void Dispose() => httpClient.Dispose();
}
