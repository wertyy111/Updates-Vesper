using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace VesperNet.Service;

internal sealed class VesperNetControlService(ILogger<VesperNetControlService> logger) : BackgroundService
{
    internal const string ServiceName = "VesperNetService";
    internal const string BaseUrl = "http://127.0.0.1:37851";
    internal const string HealthUrl = $"{BaseUrl}/health";
    internal const string HostAttachUrl = $"{BaseUrl}/overlay/host/attach";
    internal const string GuestConnectUrl = $"{BaseUrl}/overlay/guest/connect";
    internal const string ClearUrl = $"{BaseUrl}/overlay/clear";

    private const string AdapterName = "VesperNet";
    private const string AdapterTunnelType = "Wintun";
    private const string HostVirtualIp = "100.96.0.1";
    private const string OverlaySubnetMask = "255.255.0.0";
    private const string OverlayTransportPrefix = "cfws-overlay";
    private const int PreferredIpv4Metric = 35;
    private const uint SessionRingCapacity = 0x400000;
    private const int MaxHttpHeaderBytes = 32 * 1024;
    private const int MaxPacketSize = 65_535;
    private const int ErrorNoMoreItems = 259;
    private static readonly Guid AdapterGuid = new("f3b770ec-8f4f-4d22-9ff8-3c1bfda9c0cf");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<VesperNetControlService> _logger = logger;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private readonly object _stateGate = new();
    private readonly object _overlayGate = new();
    private readonly object _sendGate = new();
    private readonly string _serviceLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vesper Launcher",
        "vespernet-service.log");

    private TcpListener? _listener;
    private WintunNative? _wintun;
    private nint _adapterHandle;
    private nint _sessionHandle;
    private EventWaitHandle? _readWaitHandle;
    private Task? _packetPumpTask;
    private bool _adapterInstalled;
    private bool _overlayConnected;
    private string _transportMode = "none";
    private string? _virtualIp;
    private string _statusNote = "Phase 2 overlay scaffold active";
    private string? _driverVersion;
    private string _currentLocalVirtualIp = HostVirtualIp;
    private string _overlayRole = "idle";
    private readonly Dictionary<string, OverlayPeerSession> _overlayPeersByConnectionId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, OverlayPeerSession> _overlayPeersByVirtualIp = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VesperNet control service started.");
        TryWriteServiceDiagnosticLog("Service started.");

        _listener = new TcpListener(IPAddress.Loopback, 37851);
        _listener.Start();
        _ = Task.Run(() => InitializeAdapterAsync(stoppingToken), stoppingToken);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        finally
        {
            try
            {
                _listener?.Stop();
            }
            catch
            {
                // Ignore shutdown issues.
            }

            await ClearOverlayPeerSessionsAsync(resetToHostIp: false, CancellationToken.None);
            DisposeAdapter();
            _logger.LogInformation("VesperNet control service stopped.");
            TryWriteServiceDiagnosticLog("Service stopped.");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Ignore shutdown issues.
        }

        await ClearOverlayPeerSessionsAsync(resetToHostIp: false, CancellationToken.None);
        DisposeAdapter();
        await base.StopAsync(cancellationToken);
    }

    private async Task InitializeAdapterAsync(CancellationToken cancellationToken)
    {
        try
        {
            var wintunPath = ResolveWintunDllPath();
            if (string.IsNullOrWhiteSpace(wintunPath) || !File.Exists(wintunPath))
            {
                UpdateState(false, false, "none", null, null, "Wintun DLL not found. Overlay mode is unavailable.");
                return;
            }

            _wintun = WintunNative.Load(wintunPath);
            var driverVersion = FormatDriverVersion(_wintun.GetRunningDriverVersion());

            var adapterHandle = _wintun.OpenAdapter(AdapterName);
            var openError = adapterHandle == nint.Zero ? Marshal.GetLastWin32Error() : 0;
            if (adapterHandle == nint.Zero)
            {
                adapterHandle = _wintun.CreateAdapter(AdapterName, AdapterTunnelType, AdapterGuid);
            }

            if (adapterHandle == nint.Zero)
            {
                var createError = Marshal.GetLastWin32Error();
                UpdateState(
                    false,
                    false,
                    "none",
                    null,
                    driverVersion,
                    $"Could not open or create the Wintun adapter. OpenError={openError}; CreateError={createError}");
                return;
            }

            _adapterHandle = adapterHandle;
            _sessionHandle = _wintun.StartSession(adapterHandle, SessionRingCapacity);
            if (_sessionHandle == nint.Zero)
            {
                UpdateState(true, false, "none", null, driverVersion, "Wintun adapter created, but the tunnel session failed to start.");
                return;
            }

            var readWaitEvent = _wintun.GetReadWaitEvent(_sessionHandle);
            if (readWaitEvent == nint.Zero)
            {
                UpdateState(true, false, "none", null, driverVersion, "Wintun session started, but the packet wait event was not created.");
                return;
            }

            _readWaitHandle = CreateReadWaitHandle(readWaitEvent);
            _packetPumpTask = Task.Run(() => RunPacketPumpAsync(cancellationToken), cancellationToken);

            await Task.Delay(1200, cancellationToken);
            var ipConfigured = await EnsureAdapterIpv4Async(HostVirtualIp, cancellationToken);
            var networkPreferencesConfigured = ipConfigured && await EnsureAdapterNetworkPreferencesAsync(cancellationToken);

            _currentLocalVirtualIp = ipConfigured ? HostVirtualIp : string.Empty;
            _overlayRole = "idle";
            TryWriteServiceDiagnosticLog(
                $"Adapter ready. localIp={_currentLocalVirtualIp}, ipConfigured={ipConfigured}, networkPreferencesConfigured={networkPreferencesConfigured}");

            UpdateState(
                adapterInstalled: true,
                overlayConnected: false,
                transportMode: ipConfigured ? "adapter-only" : "none",
                virtualIp: ipConfigured ? HostVirtualIp : null,
                driverVersion: driverVersion,
                statusNote: ipConfigured
                    ? networkPreferencesConfigured
                        ? $"Wintun adapter is ready. IPv4 metric pinned to {PreferredIpv4Metric}, profile is Private, overlay is waiting for peers."
                        : "Wintun adapter is ready, but network preferences could not be fully configured."
                    : "Wintun adapter exists, but the virtual IP was not assigned.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "VesperNet adapter init failed.");
            UpdateState(false, false, "none", null, null, $"Wintun init failed: {ex.Message}");
        }
    }

    private async Task RunPacketPumpAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_wintun is null || _sessionHandle == nint.Zero || _readWaitHandle is null)
            {
                await Task.Delay(250, cancellationToken);
                continue;
            }

            var hadPackets = false;
            while (TryReceivePacket(out var packet))
            {
                hadPackets = true;
                await RouteAdapterPacketAsync(packet, cancellationToken);
            }

            if (hadPackets)
            {
                continue;
            }

            var waitIndex = WaitHandle.WaitAny([_readWaitHandle, cancellationToken.WaitHandle], 500);
            if (waitIndex == 1)
            {
                break;
            }
        }
    }

    private async Task RouteAdapterPacketAsync(byte[] packet, CancellationToken cancellationToken)
    {
        if (!TryParseIpv4Header(packet, out _, out var destinationIp))
        {
            return;
        }

        OverlayPeerSession? peerSession;
        lock (_overlayGate)
        {
            _overlayPeersByVirtualIp.TryGetValue(destinationIp, out peerSession);
        }

        if (peerSession is null)
        {
            return;
        }

        try
        {
            await peerSession.SendLock.WaitAsync(cancellationToken);
            try
            {
                if (peerSession.WebSocket.State == WebSocketState.Open)
                {
                    await peerSession.WebSocket.SendAsync(
                        new ArraySegment<byte>(packet),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        cancellationToken);
                }
            }
            finally
            {
                peerSession.SendLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to forward adapter packet to peer {ConnectionId}.", peerSession.ConnectionId);
            await RemoveOverlayPeerSessionAsync(peerSession, resetToHostIpIfGuest: false);
        }
    }

    private bool TryReceivePacket(out byte[] packet)
    {
        packet = [];
        if (_wintun is null || _sessionHandle == nint.Zero)
        {
            return false;
        }

        var packetPointer = _wintun.ReceivePacket(_sessionHandle, out var packetSize);
        if (packetPointer == nint.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 0 && error != ErrorNoMoreItems)
            {
                _logger.LogDebug("WintunReceivePacket returned no packet. Error={Error}.", error);
            }

            return false;
        }

        try
        {
            if (packetSize == 0 || packetSize > MaxPacketSize)
            {
                return false;
            }

            packet = new byte[packetSize];
            Marshal.Copy(packetPointer, packet, 0, packet.Length);
            return true;
        }
        finally
        {
            _wintun.ReleaseReceivePacket(_sessionHandle, packetPointer);
        }
    }

    private bool TryInjectPacketIntoAdapter(byte[] packet)
    {
        if (_wintun is null || _sessionHandle == nint.Zero || packet.Length == 0 || packet.Length > MaxPacketSize)
        {
            return false;
        }

        lock (_sendGate)
        {
            var sendPointer = _wintun.AllocateSendPacket(_sessionHandle, (uint)packet.Length);
            if (sendPointer == nint.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogDebug("WintunAllocateSendPacket failed. Error={Error}.", error);
                return false;
            }

            Marshal.Copy(packet, 0, sendPointer, packet.Length);
            _wintun.SendPacket(_sessionHandle, sendPointer);
            return true;
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken stoppingToken)
    {
        using var _ = client;
        using var stream = client.GetStream();

        HttpRequestData? request;
        try
        {
            request = await ReadHttpRequestAsync(stream, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse VesperNet control request.");
            return;
        }

        if (request is null)
        {
            return;
        }

        HttpResponseData response;
        try
        {
            response = await HandleRequestAsync(request, stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled VesperNet control request failure for {Method} {Path}.", request.Method, request.Path);
            response = JsonResponse(
                statusCode: 500,
                new ErrorResponse(false, "internal-error", ex.Message));
        }

        await WriteHttpResponseAsync(stream, response, stoppingToken);
    }

    private async Task<HttpResponseData> HandleRequestAsync(HttpRequestData request, CancellationToken cancellationToken)
    {
        TryWriteServiceDiagnosticLog($"HTTP {request.Method} {request.Path}");

        if (string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.Path, "/health", StringComparison.OrdinalIgnoreCase))
        {
            VesperNetHealthResponse response;
            lock (_stateGate)
            {
                response = new VesperNetHealthResponse(
                    Ok: true,
                    ServiceName: ServiceName,
                    Version: ResolveVersion(),
                    StartedAtUtc: _startedAtUtc.UtcDateTime.ToString("O"),
                    AdapterInstalled: _adapterInstalled,
                    OverlayConnected: _overlayConnected,
                    TransportMode: _transportMode,
                    VirtualIp: _virtualIp,
                    DriverVersion: _driverVersion,
                    Note: _statusNote);
            }

            return JsonResponse(200, response);
        }

        if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.Path, "/overlay/host/attach", StringComparison.OrdinalIgnoreCase))
        {
            var payload = DeserializeJson<OverlayAttachRequest>(request.Body);
            if (!ValidateOverlayRequest(payload, out var validationError))
            {
                return JsonResponse(400, new ErrorResponse(false, "bad-request", validationError));
            }

            if (_wintun is null || _sessionHandle == nint.Zero)
            {
                return JsonResponse(503, new ErrorResponse(false, "adapter-unavailable", "Wintun adapter is not ready yet."));
            }

            await ConfigureOverlayRoleAsync(HostVirtualIp, "host", cancellationToken);
            var peerIp = DerivePeerVirtualIp(payload!.ConnectionId!);
            TryWriteServiceDiagnosticLog(
                $"Overlay host attach requested. connectionId={payload.ConnectionId}, peerIp={peerIp}");
            await StartOrReplaceOverlayPeerAsync(payload.ConnectionId!, peerIp, payload.AccessToken!, payload.WebSocketUrl!, cancellationToken);

            return JsonResponse(200, new OverlayConnectResponse(
                Ok: true,
                LocalIp: HostVirtualIp,
                PeerIp: peerIp,
                TransportMode: $"{OverlayTransportPrefix}-host",
                Error: null));
        }

        if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.Path, "/overlay/guest/connect", StringComparison.OrdinalIgnoreCase))
        {
            var payload = DeserializeJson<OverlayAttachRequest>(request.Body);
            if (!ValidateOverlayRequest(payload, out var validationError))
            {
                return JsonResponse(400, new ErrorResponse(false, "bad-request", validationError));
            }

            if (_wintun is null || _sessionHandle == nint.Zero)
            {
                return JsonResponse(503, new ErrorResponse(false, "adapter-unavailable", "Wintun adapter is not ready yet."));
            }

            var guestIp = DerivePeerVirtualIp(payload!.ConnectionId!);
            await ClearOverlayPeerSessionsAsync(resetToHostIp: false, cancellationToken);
            await ConfigureOverlayRoleAsync(guestIp, "guest", cancellationToken);
            TryWriteServiceDiagnosticLog(
                $"Overlay guest connect requested. connectionId={payload.ConnectionId}, localGuestIp={guestIp}, peerIp={HostVirtualIp}");
            await StartOrReplaceOverlayPeerAsync(payload.ConnectionId!, HostVirtualIp, payload.AccessToken!, payload.WebSocketUrl!, cancellationToken);

            return JsonResponse(200, new OverlayConnectResponse(
                Ok: true,
                LocalIp: guestIp,
                PeerIp: HostVirtualIp,
                TransportMode: $"{OverlayTransportPrefix}-guest",
                Error: null));
        }

        if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(request.Path, "/overlay/clear", StringComparison.OrdinalIgnoreCase))
        {
            var payload = request.Body.Length == 0
                ? new OverlayClearRequest()
                : DeserializeJson<OverlayClearRequest>(request.Body) ?? new OverlayClearRequest();
            TryWriteServiceDiagnosticLog($"Overlay clear requested. resetToHostIp={payload.ResetToHostIp}");
            await ClearOverlayPeerSessionsAsync(payload.ResetToHostIp, cancellationToken);
            return JsonResponse(200, new { ok = true, resetToHostIp = payload.ResetToHostIp });
        }

        return JsonResponse(404, new ErrorResponse(false, "not-found", "Unknown VesperNet control endpoint."));
    }

    private async Task ConfigureOverlayRoleAsync(string localIp, string overlayRole, CancellationToken cancellationToken)
    {
        var ipConfigured = await EnsureAdapterIpv4Async(localIp, cancellationToken);
        if (!ipConfigured)
        {
            throw new InvalidOperationException($"Could not assign VesperNet address {localIp}.");
        }

        await EnsureAdapterNetworkPreferencesAsync(cancellationToken);

        lock (_overlayGate)
        {
            _currentLocalVirtualIp = localIp;
            _overlayRole = overlayRole;
        }

        TryWriteServiceDiagnosticLog($"Overlay role configured. role={overlayRole}, localIp={localIp}");

        RefreshOverlayState();
    }

    private async Task StartOrReplaceOverlayPeerAsync(
        string connectionId,
        string peerVirtualIp,
        string accessToken,
        string webSocketUrl,
        CancellationToken cancellationToken)
    {
        OverlayPeerSession? existingSession = null;
        lock (_overlayGate)
        {
            _overlayPeersByConnectionId.TryGetValue(connectionId, out existingSession);
        }

        if (existingSession is not null)
        {
            await RemoveOverlayPeerSessionAsync(existingSession, resetToHostIpIfGuest: false);
        }

        var webSocket = new ClientWebSocket
        {
            Options =
            {
                KeepAliveInterval = TimeSpan.FromSeconds(20)
            }
        };
        webSocket.Options.SetRequestHeader("Authorization", $"Bearer {accessToken}");
        await webSocket.ConnectAsync(new Uri(webSocketUrl, UriKind.Absolute), cancellationToken);

        var peerSession = new OverlayPeerSession(connectionId, peerVirtualIp, webSocket);
        lock (_overlayGate)
        {
            _overlayPeersByConnectionId[connectionId] = peerSession;
            _overlayPeersByVirtualIp[peerVirtualIp] = peerSession;
        }

        TryWriteServiceDiagnosticLog(
            $"Overlay peer started. connectionId={connectionId}, peerIp={peerVirtualIp}, wsState={webSocket.State}");
        RefreshOverlayState();
        peerSession.ReceiveLoopTask = Task.Run(
            () => RunOverlayPeerReceiveLoopAsync(peerSession, peerSession.Cancellation.Token),
            peerSession.Cancellation.Token);
    }

    private async Task RunOverlayPeerReceiveLoopAsync(OverlayPeerSession peerSession, CancellationToken cancellationToken)
    {
        TryWriteServiceDiagnosticLog(
            $"Overlay receive loop started. connectionId={peerSession.ConnectionId}, peerIp={peerSession.PeerVirtualIp}");
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   (peerSession.WebSocket.State == WebSocketState.Open ||
                    peerSession.WebSocket.State == WebSocketState.CloseSent))
            {
                var packet = await ReceiveWebSocketMessageAsync(peerSession.WebSocket, cancellationToken);
                if (packet is null)
                {
                    break;
                }

                if (packet.Length == 0)
                {
                    continue;
                }

                if (!TryInjectPacketIntoAdapter(packet))
                {
                    _logger.LogDebug("Could not inject overlay packet from peer {ConnectionId} into adapter.", peerSession.ConnectionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal during shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Overlay receive loop failed for peer {ConnectionId}.", peerSession.ConnectionId);
        }
        finally
        {
            TryWriteServiceDiagnosticLog(
                $"Overlay receive loop finished. connectionId={peerSession.ConnectionId}, wsState={peerSession.WebSocket.State}");
            await RemoveOverlayPeerSessionAsync(peerSession, resetToHostIpIfGuest: false);
        }
    }

    private async Task<byte[]?> ReceiveWebSocketMessageAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var payload = new MemoryStream();

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.Count > 0)
            {
                payload.Write(buffer, 0, result.Count);
            }

            if (result.EndOfMessage)
            {
                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    return [];
                }

                return payload.ToArray();
            }
        }

        return null;
    }

    private async Task RemoveOverlayPeerSessionAsync(OverlayPeerSession peerSession, bool resetToHostIpIfGuest)
    {
        var removedAny = false;
        lock (_overlayGate)
        {
            removedAny = _overlayPeersByConnectionId.Remove(peerSession.ConnectionId);
            _overlayPeersByVirtualIp.Remove(peerSession.PeerVirtualIp);
        }

        if (!removedAny)
        {
            return;
        }

        TryWriteServiceDiagnosticLog(
            $"Overlay peer removed. connectionId={peerSession.ConnectionId}, peerIp={peerSession.PeerVirtualIp}, resetGuest={resetToHostIpIfGuest}");

        try
        {
            peerSession.Cancellation.Cancel();
        }
        catch
        {
            // Ignore cancellation races.
        }

        try
        {
            if (peerSession.WebSocket.State == WebSocketState.Open || peerSession.WebSocket.State == WebSocketState.CloseReceived)
            {
                await peerSession.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None);
            }
        }
        catch
        {
            // Ignore close handshake issues.
        }

        peerSession.WebSocket.Dispose();
        peerSession.SendLock.Dispose();
        peerSession.Cancellation.Dispose();

        lock (_overlayGate)
        {
            if (_overlayPeersByConnectionId.Count == 0 && resetToHostIpIfGuest && string.Equals(_overlayRole, "guest", StringComparison.Ordinal))
            {
                _overlayRole = "idle";
                _currentLocalVirtualIp = HostVirtualIp;
            }
        }

        RefreshOverlayState();
    }

    private async Task ClearOverlayPeerSessionsAsync(bool resetToHostIp, CancellationToken cancellationToken)
    {
        OverlayPeerSession[] sessions;
        lock (_overlayGate)
        {
            sessions = _overlayPeersByConnectionId.Values.ToArray();
            _overlayPeersByConnectionId.Clear();
            _overlayPeersByVirtualIp.Clear();
            _overlayRole = resetToHostIp ? "idle" : _overlayRole;
        }

        TryWriteServiceDiagnosticLog(
            $"Overlay clear begin. sessionCount={sessions.Length}, resetToHostIp={resetToHostIp}");

        foreach (var session in sessions)
        {
            try
            {
                session.Cancellation.Cancel();
            }
            catch
            {
                // Ignore cancellation races.
            }

            try
            {
                if (session.WebSocket.State == WebSocketState.Open || session.WebSocket.State == WebSocketState.CloseReceived)
                {
                    await session.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "cleared", CancellationToken.None);
                }
            }
            catch
            {
                // Ignore close handshake issues.
            }

            session.WebSocket.Dispose();
            session.SendLock.Dispose();
            session.Cancellation.Dispose();
        }

        if (resetToHostIp && _wintun is not null && _sessionHandle != nint.Zero)
        {
            try
            {
                await EnsureAdapterIpv4Async(HostVirtualIp, cancellationToken);
                lock (_overlayGate)
                {
                    _currentLocalVirtualIp = HostVirtualIp;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reset VesperNet adapter back to the host address.");
            }
        }

        TryWriteServiceDiagnosticLog(
            $"Overlay clear done. currentLocalIp={_currentLocalVirtualIp}, role={_overlayRole}");
        RefreshOverlayState();
    }

    private void RefreshOverlayState(string? noteOverride = null)
    {
        bool adapterInstalled;
        string? driverVersion;
        lock (_stateGate)
        {
            adapterInstalled = _adapterInstalled;
            driverVersion = _driverVersion;
        }

        bool overlayConnected;
        string localVirtualIp;
        string overlayRole;
        int peerCount;
        lock (_overlayGate)
        {
            overlayConnected = _overlayPeersByConnectionId.Count > 0;
            localVirtualIp = _currentLocalVirtualIp;
            overlayRole = _overlayRole;
            peerCount = _overlayPeersByConnectionId.Count;
        }

        var transportMode = overlayConnected
            ? $"{OverlayTransportPrefix}-{overlayRole}"
            : adapterInstalled
                ? "adapter-only"
                : "none";
        var note = noteOverride ?? (overlayConnected
            ? $"Overlay role: {overlayRole}. Peers connected: {peerCount}. Local IP: {localVirtualIp}."
            : adapterInstalled
                ? $"Wintun adapter ready. Overlay is idle. Local IP: {localVirtualIp}."
                : "VesperNet adapter is not ready.");

        UpdateState(
            adapterInstalled,
            overlayConnected,
            transportMode,
            string.IsNullOrWhiteSpace(localVirtualIp) ? null : localVirtualIp,
            driverVersion,
            note);
    }

    private async Task<bool> EnsureAdapterIpv4Async(string ipv4Address, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"interface ip set address name=\"{AdapterName}\" static {ipv4Address} {OverlaySubnetMask}",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return false;
        }

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var outputText = (await process.StandardOutput.ReadToEndAsync()).Trim();
            var errorText = (await process.StandardError.ReadToEndAsync()).Trim();
            _logger.LogWarning(
                "Failed to configure VesperNet adapter IP {Ipv4Address}. ExitCode={ExitCode}; Output={Output}; Error={Error}",
                ipv4Address,
                process.ExitCode,
                outputText,
                errorText);
            return false;
        }

        return true;
    }

    private async Task<bool> EnsureAdapterNetworkPreferencesAsync(CancellationToken cancellationToken)
    {
        var metricConfigured = await RunPowerShellCommandAsync(
            "$ErrorActionPreference = 'Stop'; " +
            $"Set-NetIPInterface -InterfaceAlias '{AdapterName}' -AddressFamily IPv4 -AutomaticMetric Disabled -InterfaceMetric {PreferredIpv4Metric};",
            "Failed to pin VesperNet IPv4 metric",
            cancellationToken);

        var privateProfileConfigured = await RunPowerShellCommandAsync(
            "$ErrorActionPreference = 'Stop'; " +
            $"$profile = Get-NetConnectionProfile -InterfaceAlias '{AdapterName}' -ErrorAction SilentlyContinue; " +
            "if ($null -ne $profile) { " +
            $"Set-NetConnectionProfile -InterfaceAlias '{AdapterName}' -NetworkCategory Private; " +
            "}",
            "Failed to set VesperNet network profile to Private",
            cancellationToken);

        return metricConfigured && privateProfileConfigured;
    }

    private async Task<bool> RunPowerShellCommandAsync(
        string script,
        string warningMessage,
        CancellationToken cancellationToken)
    {
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            _logger.LogWarning("{WarningMessage}: process did not start.", warningMessage);
            return false;
        }

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode == 0)
        {
            return true;
        }

        var outputText = (await process.StandardOutput.ReadToEndAsync()).Trim();
        var errorText = (await process.StandardError.ReadToEndAsync()).Trim();
        _logger.LogWarning(
            "{WarningMessage}. ExitCode={ExitCode}; Output={Output}; Error={Error}",
            warningMessage,
            process.ExitCode,
            outputText,
            errorText);
        return false;
    }

    private void DisposeAdapter()
    {
        try
        {
            _readWaitHandle?.Dispose();
            _readWaitHandle = null;
        }
        catch
        {
            // Ignore read wait handle shutdown issues.
        }

        try
        {
            if (_wintun is not null && _sessionHandle != nint.Zero)
            {
                _wintun.EndSession(_sessionHandle);
                _sessionHandle = nint.Zero;
            }

            if (_wintun is not null && _adapterHandle != nint.Zero)
            {
                _wintun.CloseAdapter(_adapterHandle);
                _adapterHandle = nint.Zero;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to close Wintun adapter cleanly.");
        }
        finally
        {
            _wintun?.Dispose();
            _wintun = null;
        }
    }

    private void UpdateState(
        bool adapterInstalled,
        bool overlayConnected,
        string transportMode,
        string? virtualIp,
        string? driverVersion,
        string statusNote)
    {
        lock (_stateGate)
        {
            _adapterInstalled = adapterInstalled;
            _overlayConnected = overlayConnected;
            _transportMode = transportMode;
            _virtualIp = virtualIp;
            _driverVersion = driverVersion;
            _statusNote = statusNote;
        }
    }

    private static HttpResponseData JsonResponse(int statusCode, object payload)
    {
        var body = JsonSerializer.Serialize(payload);
        return new HttpResponseData(statusCode, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body));
    }

    private static async Task<HttpRequestData?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var requestData = new MemoryStream();
        var headerEndIndex = -1;

        while (headerEndIndex < 0)
        {
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead <= 0)
            {
                return null;
            }

            requestData.Write(buffer, 0, bytesRead);
            if (requestData.Length > MaxHttpHeaderBytes)
            {
                throw new InvalidOperationException("HTTP header is too large.");
            }

            headerEndIndex = IndexOfHeaderTerminator(requestData.GetBuffer(), (int)requestData.Length);
        }

        var rawData = requestData.ToArray();
        var headerBytes = rawData[..headerEndIndex];
        var initialBodyBytes = rawData[(headerEndIndex + 4)..];
        var headerText = Encoding.ASCII.GetString(headerBytes);
        var headerLines = headerText.Split(["\r\n"], StringSplitOptions.None);
        if (headerLines.Length == 0)
        {
            return null;
        }

        var requestLineParts = headerLines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLineParts.Length < 2)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var headerLine in headerLines.Skip(1))
        {
            var separatorIndex = headerLine.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var headerName = headerLine[..separatorIndex].Trim();
            var headerValue = headerLine[(separatorIndex + 1)..].Trim();
            headers[headerName] = headerValue;
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var contentLengthValue))
        {
            _ = int.TryParse(contentLengthValue, out contentLength);
        }

        var bodyBytes = new byte[contentLength];
        if (contentLength > 0)
        {
            var copied = Math.Min(contentLength, initialBodyBytes.Length);
            if (copied > 0)
            {
                Buffer.BlockCopy(initialBodyBytes, 0, bodyBytes, 0, copied);
            }

            var totalRead = copied;
            while (totalRead < contentLength)
            {
                var bytesRead = await stream.ReadAsync(bodyBytes.AsMemory(totalRead, contentLength - totalRead), cancellationToken);
                if (bytesRead <= 0)
                {
                    break;
                }

                totalRead += bytesRead;
            }
        }

        var path = requestLineParts[1];
        var querySeparatorIndex = path.IndexOf('?');
        if (querySeparatorIndex >= 0)
        {
            path = path[..querySeparatorIndex];
        }

        return new HttpRequestData(requestLineParts[0], path, headers, bodyBytes);
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, HttpResponseData response, CancellationToken cancellationToken)
    {
        var statusText = response.StatusCode switch
        {
            200 => "OK",
            400 => "Bad Request",
            404 => "Not Found",
            500 => "Internal Server Error",
            503 => "Service Unavailable",
            _ => "OK"
        };

        var headers = new StringBuilder()
            .Append($"HTTP/1.1 {response.StatusCode} {statusText}\r\n")
            .Append($"Content-Type: {response.ContentType}\r\n")
            .Append($"Content-Length: {response.Body.Length}\r\n")
            .Append("Connection: close\r\n")
            .Append("\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(headers.ToString());
        await stream.WriteAsync(headerBytes, cancellationToken);
        await stream.WriteAsync(response.Body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static int IndexOfHeaderTerminator(byte[] buffer, int length)
    {
        for (var index = 0; index <= length - 4; index++)
        {
            if (buffer[index] == '\r' &&
                buffer[index + 1] == '\n' &&
                buffer[index + 2] == '\r' &&
                buffer[index + 3] == '\n')
            {
                return index;
            }
        }

        return -1;
    }

    private static T? DeserializeJson<T>(byte[] body)
    {
        if (body.Length == 0)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    private static bool ValidateOverlayRequest(OverlayAttachRequest? payload, out string errorMessage)
    {
        if (payload is null)
        {
            errorMessage = "Missing JSON request body.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.AccessToken))
        {
            errorMessage = "Missing access token.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.WebSocketUrl))
        {
            errorMessage = "Missing websocket URL.";
            return false;
        }

        if (!Uri.TryCreate(payload.WebSocketUrl, UriKind.Absolute, out var webSocketUri) ||
            (webSocketUri.Scheme != "ws" && webSocketUri.Scheme != "wss"))
        {
            errorMessage = "Invalid websocket URL.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(payload.ConnectionId))
        {
            errorMessage = "Missing connection id.";
            return false;
        }

        errorMessage = string.Empty;
        return true;
    }

    private static string DerivePeerVirtualIp(string connectionId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(connectionId.Trim()));
        var thirdOctet = hash[0];
        var fourthOctet = hash[1];

        if (thirdOctet == 0)
        {
            thirdOctet = 1;
        }

        if (fourthOctet <= 1)
        {
            fourthOctet = 2;
        }

        if (thirdOctet == 0 && fourthOctet == 1)
        {
            fourthOctet = 2;
        }

        return $"100.96.{thirdOctet}.{fourthOctet}";
    }

    private static bool TryParseIpv4Header(byte[] packet, out string sourceIp, out string destinationIp)
    {
        sourceIp = string.Empty;
        destinationIp = string.Empty;

        if (packet.Length < 20)
        {
            return false;
        }

        var version = packet[0] >> 4;
        if (version != 4)
        {
            return false;
        }

        var headerLength = (packet[0] & 0x0F) * 4;
        if (headerLength < 20 || packet.Length < headerLength)
        {
            return false;
        }

        sourceIp = new IPAddress(packet.AsSpan(12, 4)).ToString();
        destinationIp = new IPAddress(packet.AsSpan(16, 4)).ToString();
        return true;
    }

    private static EventWaitHandle CreateReadWaitHandle(nint readWaitEventHandle)
    {
        var waitHandle = new EventWaitHandle(false, EventResetMode.AutoReset);
        waitHandle.SafeWaitHandle = new SafeWaitHandle(readWaitEventHandle, ownsHandle: false);
        return waitHandle;
    }

    private static string? ResolveWintunDllPath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var architectureFolder = Environment.Is64BitProcess ? "amd64" : "x86";
        var candidatePaths = new[]
        {
            Path.Combine(baseDirectory, "Native", architectureFolder, "wintun.dll"),
            Path.Combine(baseDirectory, "wintun.dll")
        };

        return candidatePaths.FirstOrDefault(File.Exists);
    }

    private static string FormatDriverVersion(uint version)
    {
        if (version == 0)
        {
            return "0.0";
        }

        var major = version >> 16;
        var minor = version & 0xFFFF;
        return $"{major}.{minor}";
    }

    private static string ResolveVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
    }

    private void TryWriteServiceDiagnosticLog(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(_serviceLogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}{new string('-', 70)}{Environment.NewLine}";
            File.AppendAllText(_serviceLogPath, entry);
        }
        catch
        {
            // Ignore logging failures.
        }
    }

    private sealed class OverlayPeerSession(string connectionId, string peerVirtualIp, ClientWebSocket webSocket)
    {
        public string ConnectionId { get; } = connectionId;
        public string PeerVirtualIp { get; } = peerVirtualIp;
        public ClientWebSocket WebSocket { get; } = webSocket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? ReceiveLoopTask { get; set; }
    }

    private sealed record HttpRequestData(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);

    private sealed record HttpResponseData(int StatusCode, string ContentType, byte[] Body);

    private sealed record ErrorResponse(bool Ok, string Error, string? Details);

    private sealed class OverlayAttachRequest
    {
        public string? AccessToken { get; init; }
        public string? WebSocketUrl { get; init; }
        public string? ConnectionId { get; init; }
    }

    private sealed class OverlayClearRequest
    {
        public bool ResetToHostIp { get; init; } = true;
    }

    private sealed record OverlayConnectResponse(
        bool Ok,
        string LocalIp,
        string PeerIp,
        string TransportMode,
        string? Error);

    private sealed record VesperNetHealthResponse(
        bool Ok,
        string ServiceName,
        string Version,
        string StartedAtUtc,
        bool AdapterInstalled,
        bool OverlayConnected,
        string TransportMode,
        string? VirtualIp,
        string? DriverVersion,
        string Note);
}
