using System.Net.WebSockets;
using System.Text;

namespace PlotWatch.Paissa;

public sealed record PaissaWebSocketActivity(int MessagesReceived, int PingsReceived, int DeltasReceived,
    int InvalidMessages, int UnknownMessages, DateTimeOffset? LastMessageAtUtc,
    DateTimeOffset? LastPingAtUtc, DateTimeOffset? LastDeltaAtUtc);

/// <summary>Long-lived read-only PaissaDB delta stream. Never falls back to REST polling.</summary>
public sealed class PaissaWebSocketClient(Action<PaissaDelta> onDelta, Action<string> onStatus,
    Action<PaissaWebSocketActivity> onActivity) : IDisposable
{
    public const string WebSocketUrl = "wss://paissadb.zhu.codes/ws";
    public const int MaximumMessageBytes = 64 * 1024;
    private static readonly TimeSpan MaximumReconnectDelay = TimeSpan.FromMinutes(5);
    private readonly Action<PaissaDelta> onDelta = onDelta;
    private readonly Action<string> onStatus = onStatus;
    private readonly Action<PaissaWebSocketActivity> onActivity = onActivity;
    private readonly CancellationTokenSource cancellation = new();
    private readonly object gate = new();
    private ClientWebSocket? activeSocket;
    private Task? runTask;
    private bool disposed;
    private int messagesReceived;
    private int pingsReceived;
    private int deltasReceived;
    private int invalidMessages;
    private int unknownMessages;
    private DateTimeOffset? lastMessageAtUtc;
    private DateTimeOffset? lastPingAtUtc;
    private DateTimeOffset? lastDeltaAtUtc;

    public void Start()
    {
        lock (gate)
        {
            if (disposed || runTask is not null) return;
            runTask = Task.Run(() => RunAsync(cancellation.Token));
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        var reconnectAttempt = 0;
        while (!token.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            lock (gate) activeSocket = socket;
            try
            {
                ReportStatus("Connecting to PaissaDB live stream...");
                await socket.ConnectAsync(new Uri(WebSocketUrl), token).ConfigureAwait(false);
                reconnectAttempt = 0;
                ReportStatus("Connected to PaissaDB live stream.");
                await ReceiveLoopAsync(socket, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                    ReportStatus("PaissaDB live stream disconnected; reconnecting.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is WebSocketException or IOException or InvalidOperationException)
            {
                ReportStatus($"PaissaDB live stream unavailable ({error.GetType().Name}); reconnecting.");
            }
            finally
            {
                lock (gate)
                    if (ReferenceEquals(activeSocket, socket)) activeSocket = null;
                try { socket.Abort(); } catch (ObjectDisposedException) { }
            }

            if (token.IsCancellationRequested) break;
            reconnectAttempt = Math.Min(reconnectAttempt + 1, 10);
            var baseSeconds = Math.Min(1 << Math.Min(reconnectAttempt, 8), (int)MaximumReconnectDelay.TotalSeconds);
            var delay = TimeSpan.FromSeconds(Math.Min(baseSeconds + Random.Shared.NextDouble() * Math.Min(baseSeconds * 0.25, 15),
                MaximumReconnectDelay.TotalSeconds));
            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
        ReportStatus("PaissaDB live stream stopped.");
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (!token.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text)
            {
                message.SetLength(0);
                continue;
            }
            if (message.Length + result.Count > MaximumMessageBytes)
            {
                message.SetLength(0);
                ReportStatus("PaissaDB sent an oversized live event; connection will be restarted.");
                throw new WebSocketException("Live event exceeded the size limit.");
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var json = Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
            message.SetLength(0);
            var now = DateTimeOffset.UtcNow;
            messagesReceived++;
            lastMessageAtUtc = now;
            var isPing = IsPingMessage(json);
            if (!PaissaDeltaParser.TryParse(json, out var delta, out var error))
            {
                invalidMessages++;
                ReportActivity();
                ReportStatus($"Ignored invalid PaissaDB live event: {error}");
                continue;
            }
            if (delta is null)
            {
                if (isPing)
                {
                    pingsReceived++;
                    lastPingAtUtc = now;
                }
                else
                    unknownMessages++;
                ReportActivity();
                continue;
            }
            deltasReceived++;
            lastDeltaAtUtc = now;
            ReportActivity();
            try { onDelta(delta); }
            catch (Exception) { ReportStatus("Failed to queue a PaissaDB live event; stream remains connected."); }
        }
    }

    private static bool IsPingMessage(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.String &&
                type.GetString() == "ping";
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private void ReportActivity()
    {
        try
        {
            onActivity(new(messagesReceived, pingsReceived, deltasReceived, invalidMessages, unknownMessages,
                lastMessageAtUtc, lastPingAtUtc, lastDeltaAtUtc));
        }
        catch (Exception) { ReportStatus("Failed to report PaissaDB stream activity."); }
    }

    private void ReportStatus(string status)
    {
        try { onStatus(status); }
        catch (Exception) { /* UI status reporting must not break socket lifecycle. */ }
    }

    public void Dispose()
    {
        ClientWebSocket? socket;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            socket = activeSocket;
        }
        cancellation.Cancel();
        try { socket?.Abort(); } catch (ObjectDisposedException) { }
        cancellation.Dispose();
    }
}
