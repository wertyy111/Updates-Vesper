using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Linq;
using VesperLauncher.Platform;

namespace VesperLauncher.Launcher;

internal sealed class VesperAuthHttpServer : IDisposable
{
    private const string ApiBasePath = "/vesper";
    private static readonly TimeSpan JoinTtl = TimeSpan.FromMinutes(10);
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _listenTask;
    private readonly object _stateLock = new();
    private readonly RSA _profileKey = RSA.Create(2048);
    private readonly string _publicKeyPem;
    private readonly Dictionary<string, VesperProfile> _profilesByUuid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VesperProfile> _profilesByUsername = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VesperProfile> _profilesByAccessToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VesperJoinSession> _joinsByLookupKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowedSkinDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _diagnosticLogPath;

    public VesperAuthHttpServer()
    {
        Port = ReserveLoopbackPort();
        _publicKeyPem = _profileKey.ExportSubjectPublicKeyInfoPem();
        _diagnosticLogPath = Path.Combine(GetLauncherDiagnosticDirectory(), "vesper-auth.log");
        SeedAllowedSkinDomains();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Prefixes.Add($"http://localhost:{Port}/");
        _listener.Start();
        _listenTask = Task.Run(ListenLoopAsync);
        AppendDiagnosticLog($"server-start port={Port}");
    }

    public int Port { get; }

    public string ApiBaseUrl => $"http://127.0.0.1:{Port}{ApiBasePath}";

    public VesperAuthPreparedProfile RegisterProfile(
        string username,
        string uuid,
        string accessToken,
        string? skinUrl,
        bool isSlimModel)
    {
        var normalizedUuid = NormalizeUuid(uuid)
            ?? throw new InvalidOperationException("Некорректный UUID офлайн-сессии.");
        var compactUuid = normalizedUuid.Replace("-", string.Empty, StringComparison.Ordinal);

        string? textureValue = null;
        string? textureSignature = null;

        if (!string.IsNullOrWhiteSpace(skinUrl))
        {
            textureValue = BuildTextureValue(username, compactUuid, skinUrl!, isSlimModel);
            textureSignature = Convert.ToBase64String(
                _profileKey.SignData(
                    Encoding.UTF8.GetBytes(textureValue),
                    HashAlgorithmName.SHA1,
                    RSASignaturePadding.Pkcs1));

        }

        AppendDiagnosticLog($"register-profile username={username} uuid={normalizedUuid} skinUrl={skinUrl ?? "-"} slim={isSlimModel}");
        return RegisterProfileCore(username, normalizedUuid, compactUuid, accessToken, textureValue, textureSignature);
    }

    public VesperAuthPreparedProfile RegisterSignedProfile(
        string username,
        string uuid,
        string accessToken,
        string textureValue,
        string? textureSignature)
    {
        var normalizedUuid = NormalizeUuid(uuid)
            ?? throw new InvalidOperationException("РќРµРєРѕСЂСЂРµРєС‚РЅС‹Р№ UUID РѕС„Р»Р°Р№РЅ-СЃРµСЃСЃРёРё.");
        var compactUuid = normalizedUuid.Replace("-", string.Empty, StringComparison.Ordinal);
        return RegisterProfileCore(username, normalizedUuid, compactUuid, accessToken, textureValue, textureSignature);
    }

    private VesperAuthPreparedProfile RegisterProfileCore(
        string username,
        string normalizedUuid,
        string compactUuid,
        string accessToken,
        string? textureValue,
        string? textureSignature)
    {
        var userPropertiesJson = "{}";
        if (!string.IsNullOrWhiteSpace(textureValue))
        {
            userPropertiesJson = JsonSerializer.Serialize(new[]
            {
                BuildTextureProperty(textureValue!, textureSignature)
            });
        }

        lock (_stateLock)
        {
            if (_profilesByUsername.TryGetValue(username, out var previousProfile))
            {
                _profilesByUuid.Remove(previousProfile.Uuid);
                _profilesByUuid.Remove(previousProfile.CompactUuid);
                _profilesByAccessToken.Remove(previousProfile.AccessToken);
            }

            var profile = new VesperProfile(
                username,
                normalizedUuid,
                compactUuid,
                accessToken,
                textureValue,
                textureSignature,
                userPropertiesJson);

            _profilesByUsername[username] = profile;
            _profilesByUuid[profile.Uuid] = profile;
            _profilesByUuid[profile.CompactUuid] = profile;
            _profilesByAccessToken[profile.AccessToken] = profile;

            if (TryExtractSkinDescriptor(textureValue, out var textureUrl, out _))
            {
                AddAllowedSkinDomain(textureUrl);
            }
        }

        return new VesperAuthPreparedProfile(
            new VesperAuthSession(normalizedUuid, username, accessToken, ApiBaseUrl),
            userPropertiesJson);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
            // ignore
        }

        _profileKey.Dispose();
    }

    private static int ReserveLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleContextAsync(context));
            }
            catch (HttpListenerException) when (_cts.IsCancellationRequested)
            {
                context?.Response.Close();
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch
            {
                context?.Response.Close();
            }
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        try
        {
            var relativePath = GetRelativePath(context.Request.Url?.AbsolutePath);
            AppendDiagnosticLog($"request {context.Request.HttpMethod} {context.Request.Url}");
            if (relativePath is null)
            {
                await WriteStatusAsync(context.Response, 404).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                (relativePath.Length == 0 || relativePath.Equals("/", StringComparison.Ordinal)))
            {
                await WriteJsonAsync(
                    context.Response,
                    new Dictionary<string, object?>
                    {
                        ["meta"] = new Dictionary<string, object?>
                        {
                            ["serverName"] = "Vesper Auth",
                            ["implementationName"] = "Vesper Launcher",
                            ["implementationVersion"] = "1.0.0",
                            ["feature.non_email_login"] = true,
                            ["feature.legacy_skin_api"] = true,
                            ["feature.enable_mojang_anti_features"] = true,
                        ["feature.enable_profile_key"] = false,
                        ["feature.usernameCheck"] = false
                        },
                        ["skinDomains"] = GetAllowedSkinDomains(),
                        ["signaturePublickey"] = _publicKeyPem
                    }).ConfigureAwait(false);
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                TryHandleSessionProfileRequest(relativePath, context.Request, context.Response))
            {
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                await TryHandleHasJoinedRequestAsync(relativePath, context.Request, context.Response).ConfigureAwait(false))
            {
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                TryHandlePublicKeysRequest(relativePath, context.Response))
            {
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                await TryHandleElyProfileLookupRequestAsync(relativePath, context.Response).ConfigureAwait(false))
            {
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                await TryHandleMinecraftServicesProfileRequestAsync(relativePath, context.Request, context.Response).ConfigureAwait(false))
            {
                return;
            }

            if (context.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                TryHandleMinecraftServicesPrivilegesRequest(relativePath, context.Response))
            {
                return;
            }

            if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                await TryHandleJoinRequestAsync(relativePath, context.Request, context.Response).ConfigureAwait(false))
            {
                return;
            }

            if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                await TryHandleProfilesLookupAsync(relativePath, context.Request, context.Response).ConfigureAwait(false))
            {
                return;
            }

            if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                await TryHandleAuthRequestAsync(relativePath, context.Request, context.Response).ConfigureAwait(false))
            {
                return;
            }

            await WriteStatusAsync(context.Response, 404).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await WriteStatusAsync(context.Response, 500).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }
    }

    private bool TryHandleSessionProfileRequest(
        string relativePath,
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        const string prefix = "/sessionserver/session/minecraft/profile/";
        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rawUuid = Uri.UnescapeDataString(relativePath[prefix.Length..]);
        if (!TryGetProfileByUuid(rawUuid, out var profile))
        {
            AppendDiagnosticLog($"session-profile miss uuid={rawUuid}");
            _ = WriteStatusAsync(response, 404);
            return true;
        }

        var includeSignature = !string.Equals(
            request.QueryString["unsigned"],
            "true",
            StringComparison.OrdinalIgnoreCase);

        AppendDiagnosticLog($"session-profile hit uuid={rawUuid} username={profile.Username} signed={includeSignature}");
        _ = WriteJsonAsync(
            response,
            BuildProfileResponse(profile, includeSignature));
        return true;
    }

    private async Task<bool> TryHandleHasJoinedRequestAsync(
        string relativePath,
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        if (!relativePath.Equals("/sessionserver/session/minecraft/hasJoined", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var username = request.QueryString["username"];
        var serverId = request.QueryString["serverId"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(serverId))
        {
            await WriteStatusAsync(response, 400).ConfigureAwait(false);
            return true;
        }

        VesperProfile? profile = null;
        lock (_stateLock)
        {
            CleanupExpiredJoinsUtc(DateTimeOffset.UtcNow);
            var lookupKey = BuildJoinLookupKey(username!, serverId!);
            if (_joinsByLookupKey.TryGetValue(lookupKey, out var join) &&
                _profilesByAccessToken.TryGetValue(join.AccessToken, out var joinedProfile))
            {
                profile = joinedProfile;
            }
        }

        if (profile is null)
        {
            if (TryGetProfileByUsername(username!, out var registryProfile))
            {
                profile = registryProfile;
                AppendDiagnosticLog($"hasJoined fallback username={profile.Username} uuid={profile.Uuid} serverId={serverId}");
            }
            else
            {
                AppendDiagnosticLog($"hasJoined miss username={username} serverId={serverId}");
                await WriteStatusAsync(response, 204).ConfigureAwait(false);
                return true;
            }
        }

        AppendDiagnosticLog($"hasJoined hit username={profile.Username} uuid={profile.Uuid} serverId={serverId}");
        await WriteJsonAsync(response, BuildProfileResponse(profile, includeSignature: true)).ConfigureAwait(false);
        return true;
    }

    private bool TryHandlePublicKeysRequest(string relativePath, HttpListenerResponse response)
    {
        if (!relativePath.Equals("/minecraftservices/publickeys", StringComparison.OrdinalIgnoreCase) &&
            !relativePath.Equals("/publickeys", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _ = WriteJsonAsync(
            response,
            new Dictionary<string, object?>
            {
                ["profilePropertyKeys"] = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["expiresAt"] = DateTimeOffset.UtcNow.AddDays(30).ToString("O"),
                        ["publicKey"] = _publicKeyPem
                    }
                },
                ["playerCertificateKeys"] = Array.Empty<object>()
            });
        return true;
    }

    private async Task<bool> TryHandleElyProfileLookupRequestAsync(
        string relativePath,
        HttpListenerResponse response)
    {
        const string prefix = "/ely/profile/";
        if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var username = Uri.UnescapeDataString(relativePath[prefix.Length..]);
        if (string.IsNullOrWhiteSpace(username))
        {
            await WriteStatusAsync(response, 400).ConfigureAwait(false);
            return true;
        }

        if (!TryGetProfileByUsername(username, out var profile))
        {
            AppendDiagnosticLog($"ely-profile miss username={username}");
            await WriteStatusAsync(response, 404).ConfigureAwait(false);
            return true;
        }

        AppendDiagnosticLog($"ely-profile hit username={profile.Username} uuid={profile.Uuid}");
        await WriteJsonAsync(
            response,
            BuildMinecraftProfilePropertiesResponse(profile, includeSignature: true)).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryHandleMinecraftServicesProfileRequestAsync(
        string relativePath,
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        if (relativePath.Equals("/minecraftservices/minecraft/profile", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Equals("/minecraft/profile", StringComparison.OrdinalIgnoreCase))
        {
            var accessToken = TryExtractBearerToken(request);
            if (string.IsNullOrWhiteSpace(accessToken) ||
                !TryGetProfileByAccessToken(accessToken, out var profile))
            {
                AppendDiagnosticLog($"minecraft-profile unauthorized path={relativePath}");
                await WriteJsonAsync(
                    response,
                    new Dictionary<string, string>
                    {
                        ["path"] = "/minecraft/profile",
                        ["errorType"] = "UnauthorizedOperationException",
                        ["error"] = "UnauthorizedOperationException",
                        ["errorMessage"] = "Unauthorized",
                        ["developerMessage"] = "Unauthorized"
                    },
                    401).ConfigureAwait(false);
                return true;
            }

            AppendDiagnosticLog($"minecraft-profile self username={profile.Username} uuid={profile.Uuid} path={relativePath}");
            await WriteJsonAsync(response, BuildMinecraftServicesProfileResponse(profile)).ConfigureAwait(false);
            return true;
        }

        const string byNamePrefix = "/minecraft/profile/name/";
        const string byNamePrefixServices = "/minecraftservices/minecraft/profile/name/";
        string? username = null;

        if (relativePath.StartsWith(byNamePrefixServices, StringComparison.OrdinalIgnoreCase))
        {
            username = Uri.UnescapeDataString(relativePath[byNamePrefixServices.Length..]);
        }
        else if (relativePath.StartsWith(byNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            username = Uri.UnescapeDataString(relativePath[byNamePrefix.Length..]);
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        if (!TryGetProfileByUsername(username, out var byNameProfile))
        {
            AppendDiagnosticLog($"minecraft-profile miss-by-name username={username}");
            await WriteStatusAsync(response, 404).ConfigureAwait(false);
            return true;
        }

        AppendDiagnosticLog($"minecraft-profile hit-by-name username={byNameProfile.Username} uuid={byNameProfile.Uuid}");
        await WriteJsonAsync(response, BuildMinecraftServicesProfileResponse(byNameProfile)).ConfigureAwait(false);
        return true;
    }

    private bool TryHandleMinecraftServicesPrivilegesRequest(string relativePath, HttpListenerResponse response)
    {
        if (!relativePath.Equals("/minecraftservices/player/attributes", StringComparison.OrdinalIgnoreCase) &&
            !relativePath.Equals("/minecraftservices/privileges", StringComparison.OrdinalIgnoreCase) &&
            !relativePath.Equals("/privileges", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _ = WriteJsonAsync(
            response,
            new Dictionary<string, object?>
            {
                ["privileges"] = new Dictionary<string, object?>
                {
                    ["onlineChat"] = new Dictionary<string, object?> { ["enabled"] = true },
                    ["multiplayerServer"] = new Dictionary<string, object?> { ["enabled"] = true },
                    ["multiplayerRealms"] = new Dictionary<string, object?> { ["enabled"] = true },
                    ["telemetry"] = new Dictionary<string, object?> { ["enabled"] = false }
                },
                ["profanityFilterPreferences"] = new Dictionary<string, object?>
                {
                    ["profanityFilterOn"] = false
                }
            });
        return true;
    }

    private async Task<bool> TryHandleJoinRequestAsync(
        string relativePath,
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        if (!relativePath.Equals("/sessionserver/session/minecraft/join", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var body = await JsonDocument.ParseAsync(request.InputStream).ConfigureAwait(false);
        var accessToken = body.RootElement.TryGetProperty("accessToken", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
        var selectedProfile = body.RootElement.TryGetProperty("selectedProfile", out var selectedProfileElement)
            ? selectedProfileElement.GetString()
            : null;
        var serverId = body.RootElement.TryGetProperty("serverId", out var serverIdElement)
            ? serverIdElement.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(accessToken) ||
            string.IsNullOrWhiteSpace(selectedProfile) ||
            string.IsNullOrWhiteSpace(serverId))
        {
            await WriteStatusAsync(response, 400).ConfigureAwait(false);
            return true;
        }

        lock (_stateLock)
        {
            CleanupExpiredJoinsUtc(DateTimeOffset.UtcNow);
            if (!_profilesByAccessToken.TryGetValue(accessToken!, out var profile) ||
                !string.Equals(profile.CompactUuid, selectedProfile, StringComparison.OrdinalIgnoreCase))
            {
                // Try hyphenated UUID from older clients.
                if (!string.Equals(profile?.Uuid, NormalizeUuid(selectedProfile), StringComparison.OrdinalIgnoreCase))
                {
                    profile = null;
                }
            }

            if (profile is null)
            {
                AppendDiagnosticLog($"join deny selectedProfile={selectedProfile} serverId={serverId}");
                _ = WriteStatusAsync(response, 403);
                return true;
            }

            var lookupKey = BuildJoinLookupKey(profile.Username, serverId!);
            _joinsByLookupKey[lookupKey] = new VesperJoinSession(profile.Username, accessToken!, serverId!, DateTimeOffset.UtcNow);
            AppendDiagnosticLog($"join ok username={profile.Username} uuid={profile.Uuid} selectedProfile={selectedProfile} serverId={serverId}");
        }

        await WriteStatusAsync(response, 204).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryHandleProfilesLookupAsync(
        string relativePath,
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        if (!relativePath.Equals("/api/profiles/minecraft", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var body = await JsonDocument.ParseAsync(request.InputStream).ConfigureAwait(false);
        if (body.RootElement.ValueKind != JsonValueKind.Array)
        {
            await WriteStatusAsync(response, 400).ConfigureAwait(false);
            return true;
        }

        List<Dictionary<string, string>> results = [];
        foreach (var nameElement in body.RootElement.EnumerateArray())
        {
            if (nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var username = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(username))
            {
                continue;
            }

            if (TryGetProfileByUsername(username, out var profile))
            {
                results.Add(new Dictionary<string, string>
                {
                    ["id"] = profile.CompactUuid,
                    ["name"] = profile.Username
                });
            }
        }

        await WriteJsonAsync(response, results).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryHandleAuthRequestAsync(
        string relativePath,
        HttpListenerRequest request,
        HttpListenerResponse response)
    {
        if (relativePath.Equals("/authserver/validate", StringComparison.OrdinalIgnoreCase))
        {
            using var validateBody = await JsonDocument.ParseAsync(request.InputStream).ConfigureAwait(false);
            var validateAccessToken = validateBody.RootElement.TryGetProperty("accessToken", out var tokenElement)
                ? tokenElement.GetString()
                : null;
            var isValid = !string.IsNullOrWhiteSpace(validateAccessToken) &&
                          TryGetProfileByAccessToken(validateAccessToken!, out _);
            await WriteStatusAsync(response, isValid ? 204 : 403).ConfigureAwait(false);
            return true;
        }

        if (relativePath.Equals("/authserver/invalidate", StringComparison.OrdinalIgnoreCase) ||
            relativePath.Equals("/authserver/signout", StringComparison.OrdinalIgnoreCase))
        {
            await WriteStatusAsync(response, 204).ConfigureAwait(false);
            return true;
        }

        if (!relativePath.Equals("/authserver/authenticate", StringComparison.OrdinalIgnoreCase) &&
            !relativePath.Equals("/authserver/refresh", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        using var body = await JsonDocument.ParseAsync(request.InputStream).ConfigureAwait(false);
        var authAccessToken = body.RootElement.TryGetProperty("accessToken", out var accessTokenElement)
            ? accessTokenElement.GetString()
            : null;
        var username = body.RootElement.TryGetProperty("username", out var usernameElement)
            ? usernameElement.GetString()
            : null;
        var clientToken = body.RootElement.TryGetProperty("clientToken", out var clientTokenElement)
            ? clientTokenElement.GetString()
            : Guid.NewGuid().ToString("N");

        VesperProfile? profile = null;
        lock (_stateLock)
        {
            if (!string.IsNullOrWhiteSpace(authAccessToken))
            {
                _profilesByAccessToken.TryGetValue(authAccessToken!, out profile);
            }

            if (profile is null && !string.IsNullOrWhiteSpace(username))
            {
                _profilesByUsername.TryGetValue(username!, out profile);
            }
        }

        if (profile is null)
        {
            await WriteJsonAsync(
                response,
                new Dictionary<string, object?>
                {
                    ["error"] = "ForbiddenOperationException",
                    ["errorMessage"] = "Unknown Vesper profile."
                },
                403).ConfigureAwait(false);
            return true;
        }

        await WriteJsonAsync(
            response,
            new Dictionary<string, object?>
            {
                ["accessToken"] = profile.AccessToken,
                ["clientToken"] = string.IsNullOrWhiteSpace(clientToken) ? Guid.NewGuid().ToString("N") : clientToken,
                ["availableProfiles"] = new[]
                {
                    new Dictionary<string, string>
                    {
                        ["id"] = profile.CompactUuid,
                        ["name"] = profile.Username
                    }
                },
                ["selectedProfile"] = new Dictionary<string, string>
                {
                    ["id"] = profile.CompactUuid,
                    ["name"] = profile.Username
                }
            }).ConfigureAwait(false);
        return true;
    }

    private bool TryGetProfileByUuid(string rawUuid, out VesperProfile profile)
    {
        var normalizedUuid = NormalizeUuid(rawUuid);
        if (!string.IsNullOrWhiteSpace(normalizedUuid))
        {
            lock (_stateLock)
            {
                if (_profilesByUuid.TryGetValue(normalizedUuid, out var hit))
                {
                    profile = hit;
                    return true;
                }
            }
        }

        var compactUuid = rawUuid?.Replace("-", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(compactUuid))
        {
            profile = null!;
            return false;
        }

        lock (_stateLock)
        {
            if (_profilesByUuid.TryGetValue(compactUuid, out var hit))
            {
                profile = hit;
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(rawUuid) &&
            TryGetRegistryProfileByUuid(rawUuid, out profile))
        {
            return true;
        }

        profile = null!;
        return false;
    }

    private bool TryGetProfileByUsername(string username, out VesperProfile profile)
    {
        lock (_stateLock)
        {
            if (_profilesByUsername.TryGetValue(username, out var hit))
            {
                profile = hit;
                return true;
            }
        }

        if (TryGetRegistryProfileByUsername(username, out profile))
        {
            return true;
        }

        profile = null!;
        return false;
    }

    private bool TryGetProfileByAccessToken(string accessToken, out VesperProfile profile)
    {
        lock (_stateLock)
        {
            if (_profilesByAccessToken.TryGetValue(accessToken, out var hit))
            {
                profile = hit;
                return true;
            }

            profile = null!;
            return false;
        }
    }

    private bool TryGetRegistryProfileByUsername(string username, out VesperProfile profile)
    {
        var offlineUuid = string.IsNullOrWhiteSpace(username)
            ? null
            : BuildOfflineUuid(username);
        return TryCreateRegistryProfile(VesperSkinRegistry.TryGetByUsername(username), offlineUuid, out profile);
    }

    private bool TryGetRegistryProfileByUuid(string rawUuid, out VesperProfile profile)
    {
        return TryCreateRegistryProfile(VesperSkinRegistry.TryGetByUuid(rawUuid), rawUuid, out profile);
    }

    private bool TryCreateRegistryProfile(VesperSkinRegistryEntry? entry, string? requestedUuid, out VesperProfile profile)
    {
        if (entry is null ||
            string.IsNullOrWhiteSpace(entry.Username) ||
            string.IsNullOrWhiteSpace(entry.Uuid) ||
            string.IsNullOrWhiteSpace(entry.TextureValue))
        {
            profile = null!;
            return false;
        }

        var normalizedUuid = NormalizeUuid(entry.Uuid);
        if (string.IsNullOrWhiteSpace(normalizedUuid))
        {
            profile = null!;
            return false;
        }

        var effectiveUuid = normalizedUuid;
        var normalizedRequestedUuid = NormalizeUuid(requestedUuid);
        var normalizedPublishedUuid = NormalizeUuid(entry.PublishedUuid);
        var normalizedOfflineUuid = BuildOfflineUuid(entry.Username);
        if (!string.IsNullOrWhiteSpace(normalizedRequestedUuid) &&
            (string.Equals(normalizedRequestedUuid, normalizedUuid, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalizedRequestedUuid, normalizedPublishedUuid, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(normalizedRequestedUuid, normalizedOfflineUuid, StringComparison.OrdinalIgnoreCase)))
        {
            effectiveUuid = normalizedRequestedUuid;
        }

        var compactUuid = effectiveUuid.Replace("-", string.Empty, StringComparison.Ordinal);
        var textureValue = entry.TextureValue;
        var textureSignature = entry.TextureSignature;
        var textureUrl = !string.IsNullOrWhiteSpace(entry.TextureUrl)
            ? entry.TextureUrl
            : null;
        var isSlimModel = false;

        if (!TryExtractSkinDescriptor(entry.TextureValue, out var extractedSkinUrl, out isSlimModel))
        {
            extractedSkinUrl = null;
        }

        if (string.IsNullOrWhiteSpace(textureUrl))
        {
            textureUrl = extractedSkinUrl;
        }

        // Re-issue textures for the UUID/name that multiplayer actually sees.
        if (!string.IsNullOrWhiteSpace(textureUrl))
        {
            textureValue = BuildTextureValue(entry.Username, compactUuid, textureUrl!, isSlimModel);
            textureSignature = Convert.ToBase64String(
                _profileKey.SignData(
                    Encoding.UTF8.GetBytes(textureValue),
                    HashAlgorithmName.SHA1,
                    RSASignaturePadding.Pkcs1));
        }

        var userPropertiesJson = JsonSerializer.Serialize(new[]
        {
            BuildTextureProperty(textureValue, textureSignature)
        });

        AppendDiagnosticLog(
            $"registry-profile username={entry.Username} effectiveUuid={effectiveUuid} offlineUuid={entry.Uuid} publishedUuid={entry.PublishedUuid ?? "-"} rebuiltTexture={!string.IsNullOrWhiteSpace(textureUrl)}");

        profile = new VesperProfile(
            entry.Username,
            effectiveUuid,
            compactUuid,
            BuildDeterministicAccessToken(entry.Username),
            textureValue,
            textureSignature,
            userPropertiesJson);
        return true;
    }

    private static string BuildDeterministicAccessToken(string username)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"VesperAuth:{username}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildOfflineUuid(string username)
    {
        var input = Encoding.UTF8.GetBytes($"OfflinePlayer:{username}");
        var hash = MD5.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    private void CleanupExpiredJoinsUtc(DateTimeOffset nowUtc)
    {
        foreach (var expiredKey in _joinsByLookupKey
                     .Where(entry => nowUtc - entry.Value.CreatedAtUtc > JoinTtl)
                     .Select(entry => entry.Key)
                     .ToArray())
        {
            _joinsByLookupKey.Remove(expiredKey);
        }
    }

    private static string BuildJoinLookupKey(string username, string serverId)
    {
        return $"{username.Trim().ToLowerInvariant()}|{serverId.Trim()}";
    }

    private static string? GetRelativePath(string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) ||
            !absolutePath.StartsWith(ApiBasePath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = absolutePath[ApiBasePath.Length..];
        return relative.Length == 0 ? "/" : relative;
    }

    private static Dictionary<string, string> BuildTextureProperty(string textureValue, string? textureSignature)
    {
        var property = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = "textures",
            ["value"] = textureValue
        };

        if (!string.IsNullOrWhiteSpace(textureSignature))
        {
            property["signature"] = textureSignature;
        }

        return property;
    }

    private static string BuildTextureValue(string username, string compactUuid, string skinUrl, bool isSlimModel)
    {
        var skinTexture = new Dictionary<string, object?>
        {
            ["url"] = skinUrl
        };

        if (isSlimModel)
        {
            skinTexture["metadata"] = new Dictionary<string, string>
            {
                ["model"] = "slim"
            };
        }

        var texturePayload = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["profileId"] = compactUuid,
            ["profileName"] = username,
            ["signatureRequired"] = true,
            ["textures"] = new Dictionary<string, object?>
            {
                ["SKIN"] = skinTexture
            }
        };

        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(texturePayload)));
    }

    private static Dictionary<string, object?> BuildProfileResponse(VesperProfile profile, bool includeSignature)
    {
        List<Dictionary<string, string>> properties = [];
        if (!string.IsNullOrWhiteSpace(profile.TextureValue))
        {
            properties.Add(BuildTextureProperty(
                profile.TextureValue!,
                includeSignature ? profile.TextureSignature : null));
        }

        return new Dictionary<string, object?>
        {
            ["id"] = profile.CompactUuid,
            ["name"] = profile.Username,
            ["properties"] = properties
        };
    }

    private static Dictionary<string, object?> BuildMinecraftServicesProfileResponse(VesperProfile profile)
    {
        var skins = Array.Empty<object>();
        if (TryExtractSkinDescriptor(profile.TextureValue, out var skinUrl, out var isSlimModel) &&
            !string.IsNullOrWhiteSpace(skinUrl))
        {
            skins =
            [
                new Dictionary<string, object?>
                {
                    ["id"] = DeriveDeterministicGuid(skinUrl!).ToString(),
                    ["state"] = "ACTIVE",
                    ["url"] = skinUrl,
                    ["variant"] = isSlimModel ? "SLIM" : "CLASSIC",
                    ["alias"] = isSlimModel ? "ALEX" : "STEVE"
                }
            ];
        }

        return new Dictionary<string, object?>
        {
            ["id"] = profile.CompactUuid,
            ["name"] = profile.Username,
            ["skins"] = skins,
            ["capes"] = Array.Empty<object>(),
            ["profileActions"] = Array.Empty<object>()
        };
    }

    private static Dictionary<string, object?> BuildMinecraftProfilePropertiesResponse(
        VesperProfile profile,
        bool includeSignature)
    {
        List<Dictionary<string, string>> properties = [];
        if (!string.IsNullOrWhiteSpace(profile.TextureValue))
        {
            properties.Add(BuildTextureProperty(
                profile.TextureValue!,
                includeSignature ? profile.TextureSignature : null));
        }

        return new Dictionary<string, object?>
        {
            ["id"] = profile.Uuid,
            ["name"] = profile.Username,
            ["properties"] = properties,
            ["profileActions"] = Array.Empty<object>()
        };
    }

    private static bool TryExtractSkinDescriptor(string? textureValue, out string? skinUrl, out bool isSlimModel)
    {
        skinUrl = null;
        isSlimModel = false;

        if (string.IsNullOrWhiteSpace(textureValue))
        {
            return false;
        }

        try
        {
            var jsonBytes = Convert.FromBase64String(textureValue);
            using var document = JsonDocument.Parse(jsonBytes);
            if (!document.RootElement.TryGetProperty("textures", out var texturesElement) ||
                !texturesElement.TryGetProperty("SKIN", out var skinElement) ||
                !skinElement.TryGetProperty("url", out var urlElement))
            {
                return false;
            }

            skinUrl = urlElement.GetString();
            if (skinElement.TryGetProperty("metadata", out var metadataElement) &&
                metadataElement.ValueKind == JsonValueKind.Object &&
                metadataElement.TryGetProperty("model", out var modelElement) &&
                string.Equals(modelElement.GetString(), "slim", StringComparison.OrdinalIgnoreCase))
            {
                isSlimModel = true;
            }

            return !string.IsNullOrWhiteSpace(skinUrl);
        }
        catch
        {
            return false;
        }
    }

    private static Guid DeriveDeterministicGuid(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        Span<byte> guidBytes = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(guidBytes);
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x40);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private static string? TryExtractBearerToken(HttpListenerRequest request)
    {
        var authorization = request.Headers["Authorization"];
        if (string.IsNullOrWhiteSpace(authorization) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization["Bearer ".Length..].Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static string? NormalizeUuid(string? rawUuid)
    {
        if (string.IsNullOrWhiteSpace(rawUuid))
        {
            return null;
        }

        var compact = rawUuid.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact.Length != 32)
        {
            return null;
        }

        return $"{compact[..8]}-{compact[8..12]}-{compact[12..16]}-{compact[16..20]}-{compact[20..32]}";
    }

    private void SeedAllowedSkinDomains()
    {
        _allowedSkinDomains.Add("textures.minecraft.net");
        _allowedSkinDomains.Add("127.0.0.1");
        _allowedSkinDomains.Add("localhost");

        foreach (var host in TryLoadSkinSyncHosts())
        {
            _allowedSkinDomains.Add(host);
        }
    }

    private string[] GetAllowedSkinDomains()
    {
        lock (_stateLock)
        {
            return _allowedSkinDomains
                .Where(domain => !string.IsNullOrWhiteSpace(domain))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(domain => domain, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    private void AddAllowedSkinDomain(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) ||
            !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return;
        }

        lock (_stateLock)
        {
            _allowedSkinDomains.Add(uri.Host);
        }
    }

    private static IReadOnlyList<string> TryLoadSkinSyncHosts()
    {
        var hosts = new List<string>();
        var candidatePaths = LauncherDataPaths.GetDataFileCandidates(
            "skin-sync.json",
            includeBaseDirectoryFile: true);

        foreach (var path in candidatePaths)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var propertyName in new[] { "PublishUrl", "LookupByUsernameUrl", "LookupByUuidUrl" })
                {
                    if (document.RootElement.TryGetProperty(propertyName, out var valueElement) &&
                        valueElement.ValueKind == JsonValueKind.String)
                    {
                        var rawUrl = valueElement.GetString();
                        if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) &&
                            !string.IsNullOrWhiteSpace(uri.Host))
                        {
                            hosts.Add(uri.Host);
                        }
                    }
                }
            }
            catch
            {
                // Ignore malformed local sync config.
            }
        }

        return hosts;
    }

    private static string GetLauncherDiagnosticDirectory()
    {
        var baseDirectory = LauncherDataPaths.GetPreferredDataDirectory();
        Directory.CreateDirectory(baseDirectory);
        return baseDirectory;
    }

    private void AppendDiagnosticLog(string message)
    {
        try
        {
            File.AppendAllText(
                _diagnosticLogPath,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // ignore diagnostics failures
        }
    }

    private static Task WriteStatusAsync(HttpListenerResponse response, int statusCode)
    {
        response.StatusCode = statusCode;
        response.ContentLength64 = 0;
        response.Close();
        return Task.CompletedTask;
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object payload, int statusCode = 200)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        response.Close();
    }

    private sealed record VesperProfile(
        string Username,
        string Uuid,
        string CompactUuid,
        string AccessToken,
        string? TextureValue,
        string? TextureSignature,
        string UserPropertiesJson);

    private sealed record VesperJoinSession(
        string Username,
        string AccessToken,
        string ServerId,
        DateTimeOffset CreatedAtUtc);
}

