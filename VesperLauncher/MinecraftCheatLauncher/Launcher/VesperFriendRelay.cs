using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VesperLauncher.Launcher;

internal sealed record VesperRelaySessionInfo(string RoomId, string TransportMode);
internal sealed record VesperRelayGuestConnectionInfo(string ConnectionId, string RoomId, string TransportMode, string WebSocketUrl);

internal sealed record VesperRelayPendingConnection(
    string ConnectionId,
    string RoomId,
    string? GuestUsername,
    string WebSocketUrl);

internal sealed class VesperGuestRelayTunnel : IAsyncDisposable
{
    private static readonly TimeSpan LocalClientAcceptTimeout = TimeSpan.FromMinutes(2);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly Task _acceptLoopTask;

    private VesperGuestRelayTunnel(TcpListener listener, string accessToken, string webSocketUrl)
    {
        _listener = listener;
        _cts = new CancellationTokenSource();
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoopTask = RunAcceptLoopAsync(accessToken, webSocketUrl, _cts.Token);
    }

    public int LocalPort { get; }

    public Task Completion => _acceptLoopTask;

    public static VesperGuestRelayTunnel Start(string accessToken, string webSocketUrl)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(4);
        return new VesperGuestRelayTunnel(listener, accessToken, webSocketUrl);
    }

    private async Task RunAcceptLoopAsync(string accessToken, string webSocketUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var acceptTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            acceptTimeoutCts.CancelAfter(LocalClientAcceptTimeout);

            using var client = await _listener.AcceptTcpClientAsync(acceptTimeoutCts.Token);
            using var webSocket = await ConnectWebSocketAsync(accessToken, webSocketUrl, cancellationToken);
            await RelayTcpAndWebSocketAsync(client.GetStream(), webSocket, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Timeout/cancel means the tunnel was no longer needed.
        }
        finally
        {
            _listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Ignore disposal races.
        }

        _listener.Stop();

        try
        {
            await _acceptLoopTask.ConfigureAwait(false);
        }
        catch
        {
            // Background tunnel shutdown should not crash the launcher.
        }

        _cts.Dispose();
    }

    private static async Task<ClientWebSocket> ConnectWebSocketAsync(string accessToken, string webSocketUrl, CancellationToken cancellationToken)
    {
        var webSocket = new ClientWebSocket();
        webSocket.Options.KeepAliveInterval = VesperFriendRelay.RelayWebSocketKeepAliveInterval;
        webSocket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        await webSocket.ConnectAsync(new Uri(webSocketUrl, UriKind.Absolute), cancellationToken).ConfigureAwait(false);
        return webSocket;
    }

    private static async Task RelayTcpAndWebSocketAsync(Stream tcpStream, ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var relayToken = relayCts.Token;

        var tcpToWsTask = PumpTcpToWebSocketAsync(tcpStream, webSocket, relayToken);
        var wsToTcpTask = PumpWebSocketToTcpAsync(webSocket, tcpStream, relayToken);

        await Task.WhenAny(tcpToWsTask, wsToTcpTask).ConfigureAwait(false);
        relayCts.Cancel();

        try
        {
            await Task.WhenAll(tcpToWsTask, wsToTcpTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch
        {
            // Suppress transport teardown noise.
        }

        try
        {
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore close handshake issues.
        }
    }

    private static async Task PumpTcpToWebSocketAsync(Stream tcpStream, ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived))
            {
                var bytesRead = await tcpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                await webSocket.SendAsync(
                    new ArraySegment<byte>(buffer, 0, bytesRead),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task PumpWebSocketToTcpAsync(ClientWebSocket webSocket, Stream tcpStream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent))
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.Count > 0)
                {
                    await tcpStream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
                    await tcpStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal static class VesperFriendRelay
{
    internal static readonly TimeSpan RelayWebSocketKeepAliveInterval = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<VesperRelaySessionInfo?> EnsureHostSessionAsync(
        HttpClient httpClient,
        string requestUrl,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedJsonRequest(HttpMethod.Post, requestUrl, accessToken, "{}");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = JsonSerializer.Deserialize<EnsureSessionResponse>(body, JsonOptions);
        if (payload is null || !payload.Ok || string.IsNullOrWhiteSpace(payload.RoomId))
        {
            return null;
        }

        return new VesperRelaySessionInfo(payload.RoomId.Trim(), string.IsNullOrWhiteSpace(payload.TransportMode) ? "cfws" : payload.TransportMode.Trim());
    }

    public static async Task CloseHostSessionAsync(
        HttpClient httpClient,
        string requestUrl,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedJsonRequest(HttpMethod.Post, requestUrl, accessToken, "{}");
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        _ = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<VesperRelayPendingConnection>> GetPendingConnectionsAsync(
        HttpClient httpClient,
        string requestUrl,
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = JsonSerializer.Deserialize<PendingConnectionsResponse>(body, JsonOptions);
        if (payload?.Connections is null || payload.Connections.Count == 0)
        {
            return Array.Empty<VesperRelayPendingConnection>();
        }

        return payload.Connections
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ConnectionId) && !string.IsNullOrWhiteSpace(entry.WebSocketUrl))
            .Select(entry => new VesperRelayPendingConnection(
                entry.ConnectionId!.Trim(),
                string.IsNullOrWhiteSpace(entry.RoomId) ? string.Empty : entry.RoomId.Trim(),
                string.IsNullOrWhiteSpace(entry.GuestUsername) ? null : entry.GuestUsername.Trim(),
                entry.WebSocketUrl!.Trim()))
            .ToList();
    }

    public static async Task<VesperGuestRelayTunnel> CreateGuestTunnelAsync(
        HttpClient httpClient,
        string requestUrl,
        string accessToken,
        string roomId,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = JsonSerializer.Serialize(new { roomId });
        using var request = CreateAuthorizedJsonRequest(HttpMethod.Post, requestUrl, accessToken, payloadJson);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = JsonSerializer.Deserialize<ConnectRelayResponse>(body, JsonOptions);
        if (payload is null || !payload.Ok || string.IsNullOrWhiteSpace(payload.WebSocketUrl))
        {
            throw new InvalidOperationException("Relay guest websocket was not returned by the server.");
        }

        return VesperGuestRelayTunnel.Start(accessToken, payload.WebSocketUrl.Trim());
    }

    public static async Task<VesperRelayGuestConnectionInfo> CreateGuestConnectionAsync(
        HttpClient httpClient,
        string requestUrl,
        string accessToken,
        string roomId,
        CancellationToken cancellationToken = default)
    {
        var payloadJson = JsonSerializer.Serialize(new { roomId });
        using var request = CreateAuthorizedJsonRequest(HttpMethod.Post, requestUrl, accessToken, payloadJson);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var payload = JsonSerializer.Deserialize<ConnectRelayResponse>(body, JsonOptions);
        if (payload is null ||
            !payload.Ok ||
            string.IsNullOrWhiteSpace(payload.ConnectionId) ||
            string.IsNullOrWhiteSpace(payload.WebSocketUrl))
        {
            throw new InvalidOperationException("Relay guest connection data was not returned by the server.");
        }

        return new VesperRelayGuestConnectionInfo(
            payload.ConnectionId.Trim(),
            string.IsNullOrWhiteSpace(payload.RoomId) ? roomId : payload.RoomId.Trim(),
            string.IsNullOrWhiteSpace(payload.TransportMode) ? "cfws" : payload.TransportMode.Trim(),
            payload.WebSocketUrl.Trim());
    }

    public static async Task AttachHostConnectionAsync(
        string accessToken,
        string webSocketUrl,
        int targetPort,
        CancellationToken cancellationToken = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, targetPort, cancellationToken).ConfigureAwait(false);

        using var webSocket = new ClientWebSocket();
        webSocket.Options.KeepAliveInterval = RelayWebSocketKeepAliveInterval;
        webSocket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        await webSocket.ConnectAsync(new Uri(webSocketUrl, UriKind.Absolute), cancellationToken).ConfigureAwait(false);

        await RelayHostStreamAsync(client.GetStream(), webSocket, cancellationToken).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateAuthorizedJsonRequest(HttpMethod method, string requestUrl, string accessToken, string jsonPayload)
    {
        var request = new HttpRequestMessage(method, requestUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task RelayHostStreamAsync(NetworkStream tcpStream, ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var relayToken = relayCts.Token;

        var tcpToWsTask = PumpTcpToWebSocketAsync(tcpStream, webSocket, relayToken);
        var wsToTcpTask = PumpWebSocketToTcpAsync(webSocket, tcpStream, relayToken);

        await Task.WhenAny(tcpToWsTask, wsToTcpTask).ConfigureAwait(false);
        relayCts.Cancel();

        try
        {
            await Task.WhenAll(tcpToWsTask, wsToTcpTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during relay teardown.
        }
        catch
        {
            // Ignore socket races while the world closes.
        }

        try
        {
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore close handshake issues.
        }
    }

    private static async Task PumpTcpToWebSocketAsync(NetworkStream tcpStream, ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived))
            {
                var bytesRead = await tcpStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (bytesRead <= 0)
                {
                    break;
                }

                await webSocket.SendAsync(
                    new ArraySegment<byte>(buffer, 0, bytesRead),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task PumpWebSocketToTcpAsync(ClientWebSocket webSocket, NetworkStream tcpStream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseSent))
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.Count > 0)
                {
                    await tcpStream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken).ConfigureAwait(false);
                    await tcpStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class EnsureSessionResponse
    {
        public bool Ok { get; init; }
        public string? RoomId { get; init; }
        public string? TransportMode { get; init; }
    }

    private sealed class ConnectRelayResponse
    {
        public bool Ok { get; init; }
        public string? ConnectionId { get; init; }
        public string? RoomId { get; init; }
        public string? TransportMode { get; init; }
        public string? WebSocketUrl { get; init; }
    }

    private sealed class PendingConnectionsResponse
    {
        public bool Ok { get; init; }
        public List<PendingConnectionEntry>? Connections { get; init; }
    }

    private sealed class PendingConnectionEntry
    {
        public string? ConnectionId { get; init; }
        public string? RoomId { get; init; }
        public string? GuestUsername { get; init; }
        public string? WebSocketUrl { get; init; }
    }
}

