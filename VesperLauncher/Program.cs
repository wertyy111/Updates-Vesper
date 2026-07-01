using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Photino.NET;
using Velopack;
using VesperLauncher.Core;
using VesperLauncher.PhotinoHost;
using VesperLauncher.PhotinoShell;
using VesperLauncher.Platform;

namespace VesperLauncher;

internal static class Program
{
	private sealed class SnapshotTransportState
	{
		private int _clientReady;

		private int _uiReady;

		public string? LastSnapshotJson { get; set; }

		public bool IsClientReady => Volatile.Read(in _clientReady) == 1;

		public bool IsUiReady => Volatile.Read(in _uiReady) == 1;

		public void MarkClientReady()
		{
			Volatile.Write(ref _clientReady, 1);
		}

		public void MarkUiReady()
		{
			Volatile.Write(ref _uiReady, 1);
		}
	}

	private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
	{
		WriteIndented = false
	};

	private static readonly Logger HostLogger = new Logger("photino-shell", null, 2097152L);

	private static readonly IPlatformService PlatformService = PlatformServiceFactory.CreateCurrent();

	private static readonly IPhotinoWindowChrome WindowChrome = PhotinoWindowChromeFactory.Create(PlatformService);

	[STAThread]
	private static void Main(string[] args)
	{
		EnableWindowsDpiAwareness();
		if (PlatformService.Features.SupportsVelopackAutoUpdate)
		{
			VelopackApp.Build().Run();
		}
		AppDomain.CurrentDomain.UnhandledException += delegate(object _, UnhandledExceptionEventArgs eventArgs)
		{
			if (eventArgs.ExceptionObject is Exception value)
			{
				TryWriteLog($"[{DateTime.Now:O}] FATAL{Environment.NewLine}{value}{Environment.NewLine}");
			}
		};
		HostLogger.Info(AppDiagnostics.Capture().ToLogText());
		ILauncherBackendHost backendHost = LauncherBackendHostFactory.CreateCurrent();
		try
		{
			CancellationTokenSource shutdown = new CancellationTokenSource();
			try
			{
				SemaphoreSlim bridgeLock = new SemaphoreSlim(1, 1);
				SnapshotTransportState snapshotTransport = new SnapshotTransportState();
				backendHost.Start();
				using LocalStaticFileServer localStaticFileServer = LocalStaticFileServer.Start();
				PhotinoWindow window = CreateWindow(localStaticFileServer.BaseUrl, startOffscreen: false);
				localStaticFileServer.BridgeMessageHandler = (string rawMessage) => HandleHttpBridgeMessageAsync(window, rawMessage, backendHost, bridgeLock, snapshotTransport, shutdown.Token);
				window.RegisterWebMessageReceivedHandler(delegate(object? sender, string message)
				{
					if (sender is PhotinoWindow window2)
					{
						snapshotTransport.MarkClientReady();
						HandleIncomingMessageAsync(window2, message, backendHost, bridgeLock, snapshotTransport, shutdown.Token);
					}
				});
				Task snapshotLoop = Task.CompletedTask;
				TaskCompletionSource windowCreated = new TaskCompletionSource();
				try
				{
					window.RegisterWindowCreatedHandler(delegate(object? sender, EventArgs e)
					{
						WindowChrome.ApplySplashWindowBounds(window);
						WindowChrome.ApplyWindowBackdrop(window, 3);
						windowCreated.TrySetResult();
					});
					string text = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
					localStaticFileServer.SplashHtml = BuildSplashHtml(localStaticFileServer.BaseUrl + "?v=" + text);
					window.Load(localStaticFileServer.SplashUrl + "?v=" + text);
					
					Task.Run(async delegate
					{
						try
						{
							await windowCreated.Task.ConfigureAwait(false);
							
							var readyTask = backendHost.WaitForLauncherReadyAsync();
							var delayTask = Task.Delay(4000);
							await Task.WhenAll(readyTask, delayTask).ConfigureAwait(false);
							
							if (readyTask.Result)
							{
								WindowChrome.SetWindowVisibility(window, false);
								WindowChrome.TryApplyLauncherWindowBounds(window, updatePosition: true);
								window.Load(localStaticFileServer.BaseUrl);
								snapshotLoop = RunSnapshotLoopAsync(window, backendHost, bridgeLock, snapshotTransport, shutdown.Token);
								await WaitForLauncherUiReadyAsync(snapshotTransport, shutdown.Token).ConfigureAwait(continueOnCapturedContext: false);
								WindowChrome.TryApplyLauncherWindowBounds(window, updatePosition: true);
								WindowChrome.ApplyWindowBackdrop(window, 0);
								WindowChrome.SetWindowVisibility(window, true);
								WindowChrome.ScheduleLauncherWindowBounds(window, shutdown.Token);
								WindowChrome.ScheduleRestoreBoundsGuard(window, shutdown.Token);
							}
						}
						catch (Exception value)
						{
							TryWriteLog($"[{DateTime.Now:O}] PRELOAD ERROR{Environment.NewLine}{value}{Environment.NewLine}");
						}
					});
					window.WaitForClose();
				}
				finally
				{
					shutdown.Cancel();
					TryWriteLog($"[{DateTime.Now:O}] Photino shell closed.{Environment.NewLine}");
					backendHost.Dispose();
					localStaticFileServer.Dispose();
					try
					{
						snapshotLoop.GetAwaiter().GetResult();
					}
					catch
					{
					}
				}
			}
			finally
			{
				if (shutdown != null)
				{
					((IDisposable)shutdown).Dispose();
				}
			}
		}
		finally
		{
			if (backendHost != null)
			{
				backendHost.Dispose();
			}
		}
	}

	private static string BuildSplashHtml(string launcherUrl)
	{
		var escapedLauncherUrl = JsonSerializer.Serialize(launcherUrl);
		return """
			<!DOCTYPE html>
			<html lang="ru">
			<head>
			<meta charset="UTF-8">
			<meta name="viewport" content="width=device-width, initial-scale=1">
			<title>Vesper Launcher</title>
			<style>
			:root {
				color-scheme: dark;
				--radius: 36px;
				--accent: #0a84ff;
				--text: rgba(255, 255, 255, 0.95);
				--muted: rgba(255, 255, 255, 0.65);
				--dim: rgba(255, 255, 255, 0.45);
				--track: rgba(255, 255, 255, 0.15);
				--glass-bg: rgba(26, 26, 26, 0.7);
				--glass-border: rgba(255, 255, 255, 0.15);
			}
			@media (prefers-color-scheme: light) {
				:root {
					color-scheme: light;
					--text: rgba(0, 0, 0, 0.95);
					--muted: rgba(0, 0, 0, 0.65);
					--dim: rgba(0, 0, 0, 0.45);
					--track: rgba(0, 0, 0, 0.15);
					--glass-bg: rgba(255, 255, 255, 0.7);
					--glass-border: rgba(0, 0, 0, 0.15);
				}
			}
			* {
				box-sizing: border-box;
			}
			html,
			body {
				width: 100%;
				height: 100%;
				background: transparent;
			}
			body {
				margin: 0;
				padding: 0;
				color: var(--text);
				font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
				overflow: hidden;
				-webkit-user-select: none;
				user-select: none;
				-webkit-app-region: drag;
			}
			.screen {
				width: 100vw;
				height: 100vh;
				display: grid;
				place-items: center;
				padding: 0;
				background: transparent;
			}
			.container {
				width: calc(100% - 16px);
				height: calc(100% - 16px);
				padding: 24px 28px;
				display: flex;
				flex-direction: column;
				justify-content: center;
				text-align: left;
				border-radius: var(--radius);
				background: var(--glass-bg);
				border: 1px solid var(--glass-border);
				box-shadow: none;
				position: relative;
			}
			.close-button {
				position: absolute;
				top: 16px;
				right: 16px;
				width: 28px;
				height: 28px;
				border: none;
				background: transparent;
				color: var(--text);
				font-size: 14px;
				line-height: 1;
				font-weight: 500;
				cursor: default;
				display: flex;
				align-items: center;
				justify-content: center;
				-webkit-app-region: no-drag;
				transition: background 150ms ease, opacity 150ms ease;
				opacity: 0.75;
			}
			.close-button:hover {
				opacity: 1;
				background: rgba(255, 255, 255, 0.1);
				border-radius: 50%;
			}
			.title {
				margin: 0;
				font-size: 22px;
				line-height: 1.2;
				font-weight: 600;
				letter-spacing: -0.5px;
			}
			.subtitle {
				margin: 4px 0 0;
				font-size: 13px;
				color: var(--muted);
				font-weight: 400;
			}
			.status {
				margin: 14px 0 0;
				font-size: 14px;
				font-weight: 500;
			}
			.detail {
				margin: 4px 0 0;
				font-size: 12px;
				color: var(--muted);
			}
			.progress-container {
				margin-top: 18px;
				width: 100%;
				height: 6px;
				min-height: 6px;
				background: var(--track);
				border-radius: 999px;
				overflow: hidden;
				position: relative;
			}
			.progress-bar {
				position: absolute;
				top: 0;
				bottom: 0;
				left: 0;
				height: 100%;
				background: #0a84ff;
				border-radius: 999px;
				width: 0%;
				transition: width 0.2s ease;
			}
			.progress-bar.indeterminate {
				width: 30%;
				animation: indeterminate 1.5s infinite linear;
			}
			.progress-text {
				display: none;
			}
			@keyframes indeterminate {
				0% { left: -30%; }
				100% { left: 100%; }
			}
			</style>
			</head>
			<body>
			<main class="screen">
				<div class="container">
					<button class="close-button" id="close-button" type="button" aria-label="Закрыть">✕</button>
					<div class="title">Vesper Launcher</div>
					<div class="subtitle">Проверка обновлений и загрузка интерфейса</div>
					<div class="status" id="status-text">Проверяем обновления...</div>
					<div class="detail" id="detail-text">Подключаемся к серверу обновлений...</div>
					<div class="progress-container">
						<div class="progress-bar indeterminate" id="progress-bar"></div>
					</div>
					<div class="progress-text" id="progress-text">Подготовка...</div>
				</div>
			</main>
			<script>
			const launcherUrl = __LAUNCHER_URL__;
			const minimumSplashMs = 1400;
			const startedAt = Date.now();
			let isNavigating = false;
			const statusText = document.getElementById('status-text');
			const detailText = document.getElementById('detail-text');
			const progressText = document.getElementById('progress-text');
			const progressBar = document.getElementById('progress-bar');
			const closeButton = document.getElementById('close-button');

			function sendNativeCommand(command, payload = {}) {
				const message = JSON.stringify({ type: 'command', command, payload });
				if (window.external?.sendMessage) {
					window.external.sendMessage(message);
					return true;
				}

				fetch('/bridge-message', {
					method: 'POST',
					headers: { 'Content-Type': 'application/json' },
					body: message
				}).catch(() => {});
				return false;
			}

			closeButton?.addEventListener('pointerdown', (event) => {
				event.preventDefault();
				event.stopPropagation();
				sendNativeCommand('host.close');
			});

			document.addEventListener('pointerdown', (event) => {
				if (event.button !== 0 || event.target?.closest?.('button,input,select,textarea,a')) {
					return;
				}

				sendNativeCommand('host.startDrag');
			});

			function openLauncherWhenVisible() {
				if (isNavigating) {
					return;
				}

				isNavigating = true;
				statusText.textContent = 'Готово!';
			}

			async function pollStartupState() {
				try {
					const response = await fetch('/bridge-message', {
						method: 'POST',
						headers: { 'Content-Type': 'application/json' },
						body: JSON.stringify({ type: 'command', command: 'bridge.requestSnapshot', payload: {} })
					});
					const envelope = await response.json();
					const update = envelope?.data?.update ?? {};
					const phase = envelope?.data?.phase ?? 'startup';

					statusText.textContent = update.message || update.Message || 'Проверяем обновления...';
					detailText.textContent = update.detailMessage || update.DetailMessage || 'Загружаем интерфейс лаунчера...';
					progressText.textContent = update.progressText || update.ProgressText || 'Ожидание...';

					const isIndeterminate = update.isIndeterminate === true || update.IsIndeterminate === true;
					const rawPercent = update.progressPercent !== undefined ? update.progressPercent : update.ProgressPercent;

					if (isIndeterminate) {
						progressBar.classList.add('indeterminate');
					} else {
						progressBar.classList.remove('indeterminate');
						if (Number.isFinite(rawPercent)) {
							progressBar.style.width = `${Math.max(0, Math.min(100, rawPercent))}%`;
						} else {
							progressBar.style.width = '0%';
						}
					}

					if (phase === 'ready') {
						openLauncherWhenVisible();
						return;
					}
				} catch {
					detailText.textContent = 'Ждем ответ локального backend...';
				}

				window.setTimeout(pollStartupState, 250);
			}

			pollStartupState();
			</script>
			</body>
			</html>
			""".Replace("__LAUNCHER_URL__", escapedLauncherUrl, StringComparison.Ordinal);
	}

	private static async Task WaitForLauncherUiReadyAsync(SnapshotTransportState snapshotTransport, CancellationToken cancellationToken)
	{
		DateTimeOffset timeoutAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(25L);
		while (!snapshotTransport.IsUiReady && !cancellationToken.IsCancellationRequested)
		{
			if (DateTimeOffset.UtcNow >= timeoutAt)
			{
				TryWriteLog($"[{DateTime.Now:O}] WARNING: UI preload timed out. Showing launcher after fallback delay.{Environment.NewLine}");
				break;
			}
			await Task.Delay(50, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private static PhotinoWindow CreateWindow(string startUrl, bool startOffscreen)
	{
		string text = ResolveIconPath();
		var windowBounds = LauncherWindowState.Load();
		PhotinoWindow photinoWindow = new PhotinoWindow
		{
			Title = "Vesper Launcher",
			Chromeless = true,
			Resizable = true,
			ContextMenuEnabled = false,
			DevToolsEnabled = false,
			Centered = !startOffscreen,
			UseOsDefaultLocation = false,
			UseOsDefaultSize = false,
			Width = windowBounds.Width,
			Height = windowBounds.Height,
			MinWidth = LauncherWindowState.MinWidth,
			MinHeight = LauncherWindowState.MinHeight,
			Transparent = true
		};
		if (startOffscreen && WindowChrome.SupportsNativeWindowShaping)
		{
			photinoWindow.Left = -32000;
			photinoWindow.Top = -32000;
		}
		if (!string.IsNullOrWhiteSpace(text))
		{
			photinoWindow.IconFile = text;
		}
		return photinoWindow;
	}

	private static void ApplyInitialWindowBounds(PhotinoWindow window)
	{
		var windowBounds = LauncherWindowState.Load();
		window.MinWidth = LauncherWindowState.MinWidth;
		window.MinHeight = LauncherWindowState.MinHeight;
		if (!WindowChrome.SupportsNativeWindowShaping)
		{
			window.SetSize(windowBounds.Width, windowBounds.Height);
			window.Center();
		}
		else if (!WindowChrome.TryApplyLauncherWindowBounds(window, updatePosition: true))
		{
			window.SetSize(windowBounds.Width, windowBounds.Height);
			window.Center();
		}
	}

	private static void EnableWindowsDpiAwareness()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		try
		{
			if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
			{
				return;
			}
		}
		catch
		{
		}

		try
		{
			_ = SetProcessDpiAwareness(2);
		}
		catch
		{
		}
	}

	private static async Task HandleIncomingMessageAsync(PhotinoWindow window, string rawMessage, ILauncherBackendHost backendHost, SemaphoreSlim bridgeLock, SnapshotTransportState snapshotTransport, CancellationToken cancellationToken)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(rawMessage);
			JsonElement rootElement = document.RootElement;
			JsonElement value;
			JsonElement value2;
			string command = ((rootElement.TryGetProperty("command", out value) && value.ValueKind == JsonValueKind.String) ? value.GetString() : ((rootElement.TryGetProperty("type", out value2) && value2.ValueKind == JsonValueKind.String) ? value2.GetString() : null));
			JsonElement value3;
			JsonElement payload = (rootElement.TryGetProperty("payload", out value3) ? value3.Clone() : default(JsonElement));
			if (string.IsNullOrWhiteSpace(command))
			{
				return;
			}
			string normalizedCommand = command.Trim().ToLowerInvariant();
			if (normalizedCommand == "host.startdrag")
			{
				window.Invoke(delegate
				{
					WindowChrome.StartWindowDrag(window);
				});
				return;
			}
			if (normalizedCommand == "host.startresize")
			{
				JsonElement value4;
				string resizeDirection = ((payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("direction", out value4) && value4.ValueKind == JsonValueKind.String) ? value4.GetString() : null);
				window.Invoke(delegate
				{
					WindowChrome.StartWindowResize(window, resizeDirection ?? string.Empty);
				});
				return;
			}
			if (normalizedCommand == "host.windowsize")
			{
				if (payload.ValueKind == JsonValueKind.Object &&
					payload.TryGetProperty("width", out var widthElement) &&
					payload.TryGetProperty("height", out var heightElement) &&
					widthElement.TryGetInt32(out var width) &&
					heightElement.TryGetInt32(out var height))
				{
					LauncherWindowState.Save(width, height);
				}
				return;
			}
			await bridgeLock.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			try
			{
				bool shouldPublishSnapshot = true;
				switch (normalizedCommand)
				{
				case "client.uiready":
					snapshotTransport.MarkUiReady();
					break;
				case "host.minimize":
					window.Invoke(delegate
					{
						WindowChrome.MinimizeWindow(window);
					});
					break;
				case "host.togglemaximize":
					window.Invoke(delegate
					{
						window.Maximized = !window.Maximized;
					});
					break;
				case "host.close":
					shouldPublishSnapshot = false;
					window.Invoke(delegate
					{
						window.Close();
					});
					break;
				case "host.requestsnapshot":
				case "bridge.requestsnapshot":
					break;
				default:
					await backendHost.ExecuteCommandAsync(command, payload).ConfigureAwait(continueOnCapturedContext: false);
					break;
				}
				if (shouldPublishSnapshot)
				{
					bool forcePublish = command.Equals("host.requestSnapshot", StringComparison.OrdinalIgnoreCase) || command.Equals("bridge.requestSnapshot", StringComparison.OrdinalIgnoreCase);
					await PublishSnapshotCoreAsync(window, backendHost, snapshotTransport, cancellationToken, forcePublish).ConfigureAwait(continueOnCapturedContext: false);
				}
			}
			finally
			{
				bridgeLock.Release();
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			TryWriteLog($"[{DateTime.Now:O}] MESSAGE ERROR{Environment.NewLine}{ex2}{Environment.NewLine}");
			await SendTransportMessageAsync(window, new
			{
				type = "error",
				message = ex2.Message
			}, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private static async Task<string> HandleHttpBridgeMessageAsync(PhotinoWindow window, string rawMessage, ILauncherBackendHost backendHost, SemaphoreSlim bridgeLock, SnapshotTransportState snapshotTransport, CancellationToken cancellationToken)
	{
		try
		{
			using JsonDocument document = JsonDocument.Parse(rawMessage);
			JsonElement rootElement = document.RootElement;
			JsonElement value;
			JsonElement value2;
			string command = ((rootElement.TryGetProperty("command", out value) && value.ValueKind == JsonValueKind.String) ? value.GetString() : ((rootElement.TryGetProperty("type", out value2) && value2.ValueKind == JsonValueKind.String) ? value2.GetString() : null));
			JsonElement value3;
			JsonElement payload = (rootElement.TryGetProperty("payload", out value3) ? value3.Clone() : default(JsonElement));
			snapshotTransport.MarkClientReady();
			if (!string.IsNullOrWhiteSpace(command))
			{
				string normalizedCommand = command.Trim().ToLowerInvariant();
				if (normalizedCommand == "host.startdrag")
				{
					window.Invoke(delegate
					{
						WindowChrome.StartWindowDrag(window);
					});
					return await BuildSnapshotEnvelopeJsonAsync(backendHost).ConfigureAwait(continueOnCapturedContext: false);
				}
				if (normalizedCommand == "host.startresize")
				{
					JsonElement value4;
					string resizeDirection = ((payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("direction", out value4) && value4.ValueKind == JsonValueKind.String) ? value4.GetString() : null);
					window.Invoke(delegate
					{
						WindowChrome.StartWindowResize(window, resizeDirection ?? string.Empty);
					});
					return await BuildSnapshotEnvelopeJsonAsync(backendHost).ConfigureAwait(continueOnCapturedContext: false);
				}
				await bridgeLock.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				try
				{
					switch (normalizedCommand)
					{
					case "client.uiready":
						snapshotTransport.MarkUiReady();
						break;
					case "host.minimize":
						window.Invoke(delegate
						{
							WindowChrome.MinimizeWindow(window);
						});
						break;
					case "host.togglemaximize":
						window.Invoke(delegate
						{
							window.Maximized = !window.Maximized;
						});
						break;
					case "host.close":
						window.Invoke(delegate
						{
							window.Close();
						});
						break;
					default:
						await backendHost.ExecuteCommandAsync(command, payload).ConfigureAwait(continueOnCapturedContext: false);
						break;
					case "bridge.requestsnapshot":
						break;
					}
				}
				finally
				{
					bridgeLock.Release();
				}
			}
			return await BuildSnapshotEnvelopeJsonAsync(backendHost).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (Exception ex) when (!(ex is OperationCanceledException))
		{
			TryWriteLog($"[{DateTime.Now:O}] HTTP BRIDGE ERROR{Environment.NewLine}{ex}{Environment.NewLine}");
			return JsonSerializer.Serialize(new
			{
				type = "error",
				message = ex.Message
			}, JsonOptions);
		}
	}

	private static async Task RunSnapshotLoopAsync(PhotinoWindow window, ILauncherBackendHost backendHost, SemaphoreSlim bridgeLock, SnapshotTransportState snapshotTransport, CancellationToken cancellationToken)
	{
		await WaitForClientReadyAsync(snapshotTransport, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		await TrySynchronizeBoundsAsync(window, backendHost, bridgeLock, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		await PublishSnapshotAsync(window, backendHost, bridgeLock, snapshotTransport, cancellationToken, requireLock: false, forcePublish: true).ConfigureAwait(continueOnCapturedContext: false);
		using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(900L));
		while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
		{
			await TrySynchronizeBoundsAsync(window, backendHost, bridgeLock, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			await PublishSnapshotAsync(window, backendHost, bridgeLock, snapshotTransport, cancellationToken, requireLock: false, forcePublish: false).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private static async Task PublishSnapshotAsync(PhotinoWindow window, ILauncherBackendHost backendHost, SemaphoreSlim bridgeLock, SnapshotTransportState snapshotTransport, CancellationToken cancellationToken, bool requireLock, bool forcePublish)
	{
		if (!snapshotTransport.IsClientReady)
		{
			return;
		}
		bool lockTaken = false;
		try
		{
			if (requireLock)
			{
				await bridgeLock.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				lockTaken = true;
			}
			else
			{
				lockTaken = await bridgeLock.WaitAsync(0, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
				if (!lockTaken)
				{
					return;
				}
			}
			await PublishSnapshotCoreAsync(window, backendHost, snapshotTransport, cancellationToken, forcePublish).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			if (lockTaken)
			{
				bridgeLock.Release();
			}
		}
	}

	private static async Task PublishSnapshotCoreAsync(PhotinoWindow window, ILauncherBackendHost backendHost, SnapshotTransportState snapshotTransport, CancellationToken cancellationToken, bool forcePublish)
	{
		if (snapshotTransport.IsClientReady)
		{
			string json = await BuildSnapshotEnvelopeJsonAsync(backendHost).ConfigureAwait(continueOnCapturedContext: false);
			if ((forcePublish || !string.Equals(snapshotTransport.LastSnapshotJson, json, StringComparison.Ordinal)) && await SendTransportJsonAsync(window, json, cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
			{
				snapshotTransport.LastSnapshotJson = json;
			}
		}
	}

	private static async Task<string> BuildSnapshotEnvelopeJsonAsync(ILauncherBackendHost backendHost)
	{
		return JsonSerializer.Serialize(new
		{
			type = "snapshot",
			data = await backendHost.GetSnapshotAsync().ConfigureAwait(continueOnCapturedContext: false)
		}, JsonOptions);
	}

	private static async Task WaitForClientReadyAsync(SnapshotTransportState snapshotTransport, CancellationToken cancellationToken)
	{
		while (!snapshotTransport.IsClientReady)
		{
			await Task.Delay(100, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
		}
	}

	private static async Task TrySynchronizeBoundsAsync(PhotinoWindow window, ILauncherBackendHost backendHost, SemaphoreSlim bridgeLock, CancellationToken cancellationToken)
	{
		bool lockTaken = false;
		try
		{
			lockTaken = await bridgeLock.WaitAsync(0, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (lockTaken)
			{
				string json = JsonSerializer.Serialize(new
				{
					left = window.Left,
					top = window.Top,
					width = window.Width,
					height = window.Height,
					maximized = window.Maximized
				}, JsonOptions);
				using JsonDocument payloadDocument = JsonDocument.Parse(json);
				await backendHost.ExecuteCommandAsync("host.syncBounds", payloadDocument.RootElement.Clone()).ConfigureAwait(continueOnCapturedContext: false);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception value)
		{
			TryWriteLog($"[{DateTime.Now:O}] BOUNDS ERROR{Environment.NewLine}{value}{Environment.NewLine}");
		}
		finally
		{
			if (lockTaken)
			{
				bridgeLock.Release();
			}
		}
	}

	private static async Task SendTransportMessageAsync(PhotinoWindow window, object payload, CancellationToken cancellationToken)
	{
		string json = JsonSerializer.Serialize(payload, JsonOptions);
		await SendTransportJsonAsync(window, json, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
	}

	private static async Task<bool> SendTransportJsonAsync(PhotinoWindow window, string json, CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();
			TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			window.Invoke(delegate
			{
				try
				{
					if (cancellationToken.IsCancellationRequested)
					{
						completion.TrySetCanceled(cancellationToken);
					}
					else
					{
						window.SendWebMessage(json);
						completion.TrySetResult(result: true);
					}
				}
				catch (Exception value2)
				{
					TryWriteLog($"[{DateTime.Now:O}] SEND ERROR{Environment.NewLine}{value2}{Environment.NewLine}");
					completion.TrySetResult(result: false);
				}
			});
			return await completion.Task.ConfigureAwait(continueOnCapturedContext: false);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception value)
		{
			TryWriteLog($"[{DateTime.Now:O}] SEND ERROR{Environment.NewLine}{value}{Environment.NewLine}");
			return false;
		}
	}

	private static string? ResolveIconPath()
	{
		string[] array = new string[2];
		InlineArray6<string> buffer = default(InlineArray6<string>);
		buffer[0] = AppContext.BaseDirectory;
		buffer[1] = "..";
		buffer[2] = "..";
		buffer[3] = "..";
		buffer[4] = "Assets";
		buffer[5] = "vesper-app.ico";
		array[0] = Path.GetFullPath(Path.Combine(buffer));
		array[1] = Path.Combine(AppContext.BaseDirectory, "Assets", "vesper-app.ico");
		return array.FirstOrDefault(File.Exists);
	}

	private static void TryWriteLog(string entry)
	{
		HostLogger.WriteRaw(entry);
	}

	[DllImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

	[DllImport("shcore.dll")]
	private static extern int SetProcessDpiAwareness(int awareness);
}

