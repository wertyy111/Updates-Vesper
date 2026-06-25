using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VesperLauncher.PhotinoHost;

internal sealed class LocalStaticFileServer : IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".mjs"] = "text/javascript; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".ico"] = "image/x-icon",
            [".svg"] = "image/svg+xml",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".gif"] = "image/gif",
            [".txt"] = "text/plain; charset=utf-8",
            [".wav"] = "audio/wav",
            [".mp3"] = "audio/mpeg"
        };

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _frontendRoot;
    private readonly string _indexPath;
    private readonly string _assetsRoot;
    private readonly Task _serverTask;

    private LocalStaticFileServer(string frontendRoot, string assetsRoot, int port)
    {
        _frontendRoot = frontendRoot;
        _indexPath = Path.Combine(frontendRoot, "index.html");
        _assetsRoot = assetsRoot;
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _serverTask = Task.Run(() => ListenLoopAsync(_shutdown.Token));
    }

    public string BaseUrl { get; }

    public Func<string, Task<string>>? BridgeMessageHandler { get; set; }

    public static LocalStaticFileServer Start()
    {
        var frontendRoot = ResolveFrontendRoot();
        var assetsRoot = ResolveAssetsRoot();
        var port = GetFreePort();
        return new LocalStaticFileServer(frontendRoot, assetsRoot, port);
    }

    public void Dispose()
    {
        try
        {
            _shutdown.Cancel();
        }
        catch
        {
            // Ignore shutdown races.
        }

        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // Ignore shutdown races.
        }

        try
        {
            _serverTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown races.
        }
    }

    public static string BuildLauncherFileUrl(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(absolutePath);
        var token = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "/launcher-file/" + token;
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
                _ = Task.Run(() => HandleRequestAsync(context, cancellationToken), cancellationToken);
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                context?.Response.Close();
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var requestPath = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/");
            if (requestPath.Equals("/bridge-message", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await HandleBridgeMessageAsync(context, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (requestPath.StartsWith("/launcher-assets/", StringComparison.OrdinalIgnoreCase))
            {
                var assetRelativePath = requestPath["/launcher-assets/".Length..];
                var assetPath = Path.Combine(_assetsRoot, assetRelativePath.Replace('/', Path.DirectorySeparatorChar));
                await ServeFileAsync(context, NormalizeSafePath(_assetsRoot, assetPath), cancellationToken).ConfigureAwait(false);
                return;
            }

            if (requestPath.StartsWith("/launcher-file/", StringComparison.OrdinalIgnoreCase))
            {
                var token = requestPath["/launcher-file/".Length..];
                var decodedPath = DecodeLauncherFileToken(token);
                await ServeFileAsync(context, decodedPath, cancellationToken).ConfigureAwait(false);
                return;
            }

            var relativePath = requestPath.TrimStart('/');
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                relativePath = "index.html";
            }

            var staticFilePath = NormalizeSafePath(_frontendRoot, Path.Combine(_frontendRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!string.IsNullOrWhiteSpace(staticFilePath) && File.Exists(staticFilePath))
            {
                await ServeFileAsync(context, staticFilePath, cancellationToken).ConfigureAwait(false);
                return;
            }

            await ServeFileAsync(context, _indexPath, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (!context.Response.OutputStream.CanWrite)
            {
                return;
            }

            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            var buffer = Encoding.UTF8.GetBytes("Internal server error.");
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.ContentLength64 = buffer.LongLength;
            await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    private async Task HandleBridgeMessageAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var handler = BridgeMessageHandler;
        if (handler is null)
        {
            context.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
            context.Response.Close();
            return;
        }

        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
        var requestBody = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var responseBody = await handler(requestBody).ConfigureAwait(false);
        var buffer = Encoding.UTF8.GetBytes(responseBody);

        context.Response.StatusCode = (int)HttpStatusCode.OK;
        ApplyCommonResponseHeaders(context.Response);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = buffer.LongLength;
        await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
        context.Response.Close();
    }

    private static string ResolveFrontendRoot()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "UserInterface", "dist"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "UserInterface", "dist"))
        };

        var resolved = candidates.FirstOrDefault(Directory.Exists);
        if (resolved is null)
        {
            throw new DirectoryNotFoundException("Photino frontend build output was not found. Run the React build first.");
        }

        return resolved;
    }

    private static string ResolveAssetsRoot()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "Assets"))
        };

        var resolved = candidates.FirstOrDefault(Directory.Exists);
        if (resolved is null)
        {
            throw new DirectoryNotFoundException("Launcher assets directory was not found.");
        }

        return resolved;
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string? DecodeLauncherFileToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var normalized = token.Replace('-', '+').Replace('_', '/');
        var remainder = normalized.Length % 4;
        if (remainder > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');
        }

        try
        {
            var bytes = Convert.FromBase64String(normalized);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private static string? NormalizeSafePath(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : null;
    }

    private static async Task ServeFileAsync(HttpListenerContext context, string? absolutePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !File.Exists(absolutePath))
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            ApplyCommonResponseHeaders(context.Response);
            context.Response.Close();
            return;
        }

        var extension = Path.GetExtension(absolutePath);
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        ApplyCommonResponseHeaders(context.Response);
        context.Response.ContentType = ContentTypes.TryGetValue(extension, out var contentType)
            ? contentType
            : "application/octet-stream";

        using var stream = File.OpenRead(absolutePath);
        context.Response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(context.Response.OutputStream, 81920, cancellationToken).ConfigureAwait(false);
        context.Response.Close();
    }

    private static void ApplyCommonResponseHeaders(HttpListenerResponse response)
    {
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
        response.Headers["Cache-Control"] = "no-store";
    }
}

