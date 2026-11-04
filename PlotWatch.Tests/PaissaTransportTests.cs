using System.Net;
using System.Net.Http;
using PlotWatch.Paissa;

internal static class PaissaTransportTests
{
    public static readonly (string Name, Action Test)[] Cases =
    [
        ("truncated world response retries once through shared pacing", () => TruncatedRetry().GetAwaiter().GetResult()),
        ("truncated world response has a strict two-attempt budget", () => RetryBound().GetAwaiter().GetResult()),
        ("short body with declared length is rejected before parsing", () => ShortBody().GetAwaiter().GetResult()),
        ("truncated stream IO is classified without matching error text", () => StreamFailure().GetAwaiter().GetResult()),
        ("world retry cancellation prevents a second HTTP request", () => Cancellation().GetAwaiter().GetResult()),
        ("world retry honors shared Retry-After cooldown", () => RateLimit().GetAwaiter().GetResult()),
        ("world retry does not retry parse limits HTTP errors or unrelated IO", () => NoBroadRetry().GetAwaiter().GetResult()),
    ];
    private const string WorldJson = "{\"id\":57,\"name\":\"Siren\",\"num_open_plots\":0,\"districts\":[]}";
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
    }
    private static HttpResponseMessage Good() => new(HttpStatusCode.OK) { Content = new StringContent(WorldJson) };

    private static async Task TruncatedRetry()
    {
        var handler = new Handler(number => number == 1 ? throw new HttpRequestException(HttpRequestError.ResponseEnded, "transport failure") : Good());
        using var client = new PaissaApiClient(handler, TimeSpan.FromMilliseconds(30));
        var notices = 0;
        var response = await client.GetWorldWithRetryAsync(57, retrying: (_, failed) => { notices++; Equal(true, failed.TruncatedResponse); });
        Equal(true, response.IsSuccess);
        Equal(2, handler.Calls);
        Equal(1, notices);
        Equal(true, handler.Starts[1] - handler.Starts[0] >= TimeSpan.FromMilliseconds(25));
    }

    private static async Task RetryBound()
    {
        var handler = new Handler(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "ended"));
        using var client = new PaissaApiClient(handler, TimeSpan.Zero);
        var response = await client.GetWorldWithRetryAsync(57);
        Equal(false, response.IsSuccess);
        Equal(true, response.TruncatedResponse);
        Equal(2, handler.Calls);
    }

    private static async Task ShortBody()
    {
        var handler = new Handler(number =>
        {
            var response = Good();
            if (number == 1) response.Content.Headers.ContentLength = WorldJson.Length + 100;
            return response;
        });
        using var client = new PaissaApiClient(handler, TimeSpan.Zero);
        var response = await client.GetWorldWithRetryAsync(57);
        Equal(true, response.IsSuccess);
        Equal(2, handler.Calls);
    }

    private static async Task StreamFailure()
    {
        var handler = new Handler(number => number == 1
            ? new(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) } : Good());
        using var client = new PaissaApiClient(handler, TimeSpan.Zero);
        Equal(true, (await client.GetWorldWithRetryAsync(57)).IsSuccess);
        Equal(2, handler.Calls);
    }

    private static async Task Cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler(_ => throw new HttpRequestException(HttpRequestError.ResponseEnded, "ended"));
        using var client = new PaissaApiClient(handler, TimeSpan.Zero);
        var response = await client.GetWorldWithRetryAsync(57, cancellation.Token, (_, _) => cancellation.Cancel());
        Equal(false, response.IsSuccess);
        Equal("Request cancelled.", response.Error);
        Equal(1, handler.Calls);
    }

    private static async Task RateLimit()
    {
        var handler = new Handler(number =>
        {
            if (number != 1) return Good();
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("slow down") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(60));
            return response;
        });
        using var client = new PaissaApiClient(handler, TimeSpan.Zero);
        Equal(true, (await client.GetWorldWithRetryAsync(57)).IsSuccess);
        Equal(2, handler.Calls);
        Equal(true, handler.Starts[1] - handler.Starts[0] >= TimeSpan.FromMilliseconds(50));
    }

    private static async Task NoBroadRetry()
    {
        foreach (var kind in new[] { "parse", "limit", "HTTP", "IO" })
        {
            var handler = new Handler(_ => kind switch
            {
                "parse" => new(HttpStatusCode.OK) { Content = new StringContent("{") },
                "limit" => new(HttpStatusCode.OK) { Content = new StringContent(new string('x', 2 * 1024 * 1024 + 1)) },
                "HTTP" => new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("offline") },
                _ => throw new IOException("unrelated IO failure"),
            });
            using var client = new PaissaApiClient(handler, TimeSpan.Zero);
            var response = await client.GetWorldWithRetryAsync(57);
            Equal(false, response.IsSuccess);
            Equal(false, response.TruncatedResponse);
            Equal(1, handler.Calls);
        }
    }

    private sealed class Handler(Func<int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        public List<DateTimeOffset> Starts = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Starts.Add(DateTimeOffset.UtcNow);
            return Task.FromResult(respond(++Calls));
        }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "read failure"));
    }
}
