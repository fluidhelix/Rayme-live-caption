using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Rayme.Overlay;

public sealed class SessionClient : IAsyncDisposable {
    public const int MaxMessageBytes = 2 * 1024 * 1024;
    readonly Uri uri;
    readonly string origin;
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim sending = new(1);
    ClientWebSocket? socket;
    public SessionClient(string sessionUrl) {
        if (!Uri.TryCreate(sessionUrl, UriKind.Absolute, out var u) || u.Scheme != "http" || u.Host != "127.0.0.1" || u.UserInfo != "" || u.AbsolutePath != "/" || u.Fragment != "" || !System.Text.RegularExpressions.Regex.IsMatch(u.Query, "^\\?token=[a-f0-9]{48}$")) throw new ArgumentException("Invalid local caption session");
        origin = $"http://127.0.0.1:{u.Port}"; uri = new UriBuilder(u) { Scheme = "ws" }.Uri;
    }
    public async Task RunAsync(Func<string, Task> message, Func<string, Task> connection) {
        int attempts = 0;
        while (!stop.IsCancellationRequested) {
            using var ws = new ClientWebSocket(); socket = ws;
            ws.Options.SetRequestHeader("Origin", origin); ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try {
                await connection("Connecting…");
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) { connect.CancelAfter(TimeSpan.FromSeconds(8)); await ws.ConnectAsync(uri, connect.Token); }
                await connection("Connected"); attempts = 0;
                byte[] buffer = new byte[8192];
                while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open) {
                    using var data = new MemoryStream(); WebSocketReceiveResult result;
                    do {
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
                        if (result.MessageType == WebSocketMessageType.Close) throw new WebSocketException("Session closed");
                        if (result.MessageType != WebSocketMessageType.Text || data.Length + result.Count > MaxMessageBytes) throw new WebSocketException("Invalid caption message");
                        data.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                    // Await the dispatcher: at most one UI update in flight, never an unbounded queue.
                    await message(Encoding.UTF8.GetString(data.GetBuffer(), 0, (int)data.Length));
                }
            } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or IOException) { }
            finally { if (ReferenceEquals(socket, ws)) socket = null; }
            if (stop.IsCancellationRequested) break;
            await connection("Connection lost — reconnecting…");
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 << Math.Min(attempts++, 4))), stop.Token); } catch (OperationCanceledException) { break; }
        }
    }
    public async Task<bool> SendAsync(object command) {
        if (!await sending.WaitAsync(0)) return false;
        try {
            var ws = socket; if (ws?.State != WebSocketState.Open) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await ws.SendAsync(new ArraySegment<byte>(JsonSerializer.SerializeToUtf8Bytes(command)), WebSocketMessageType.Text, true, timeout.Token); return true;
        } catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException) { return false; }
        finally { sending.Release(); }
    }
    public void Reconnect() => socket?.Abort();
    public ValueTask DisposeAsync() { stop.Cancel(); socket?.Abort(); return ValueTask.CompletedTask; }
}
