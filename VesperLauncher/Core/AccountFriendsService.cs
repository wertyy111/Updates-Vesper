using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using VesperLauncher.Launcher;
using VesperLauncher.Platform;

namespace VesperLauncher.Core;

internal sealed class AccountFriendsService
{
    private const int MaxRecentUsernames = 8;
    private static readonly Regex UsernameRegex = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly IPlatformService _platform;
    private readonly object _sync = new();
    private readonly string _statePath;
    private readonly AccountSyncConfig _syncConfig;
    private AccountFriendsState _state;

    private readonly CancellationTokenSource _cts = new();
    private Task? _syncTask;

    private string _avatarUrl = string.Empty;
    private List<VesperFriendInfo> _friends = [];
    private List<VesperIncomingFriendRequest> _incomingRequests = [];
    private int _outgoingRequestCount = 0;

    public AccountFriendsService(IPlatformService platform)
    {
        _platform = platform;
        _statePath = Path.Combine(platform.Paths.GetLauncherDataDirectory(), "account-friends-state.json");
        _syncConfig = LoadSyncConfig();
        _state = LoadState();
        if (_state.HasAuthenticatedSession)
        {
            VesperSkinRegistry.SetAccessToken(_state.SessionToken);
        }
        StartSyncLoop();
    }

    public void StartSyncLoop()
    {
        _syncTask = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                string token;
                bool hasSession;
                lock (_sync)
                {
                    token = _state.SessionToken;
                    hasSession = _state.HasAuthenticatedSession;
                }

                if (hasSession && !string.IsNullOrWhiteSpace(token))
                {
                    await RefreshProfileAsync(token, _cts.Token).ConfigureAwait(false);
                    await RefreshFriendsAsync(token, _cts.Token).ConfigureAwait(false);
                }

                try
                {
                    await Task.Delay(10000, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    public void StopSyncLoop()
    {
        _cts.Cancel();
    }

    public string CurrentNickname
    {
        get
        {
            lock (_sync)
            {
                return NormalizeNickname(_state.CurrentNickname);
            }
        }
    }

    public string FriendNicknameInput
    {
        get
        {
            lock (_sync)
            {
                return _state.FriendNicknameInput;
            }
        }
    }

    public async Task<AccountSubmitResult> SubmitAccountAsync(
        string? mode,
        string? username,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var normalizedMode = NormalizeMode(mode);
        if (normalizedMode == "guest")
        {
            return new AccountSubmitResult(false, "Гостевой режим отключен. Войдите или зарегистрируйтесь.");
        }

        var normalizedUsername = NormalizeNickname(username);
        if (!UsernameRegex.IsMatch(normalizedUsername))
        {
            return new AccountSubmitResult(false, "Ник должен быть 3-16 символов: латиница, цифры или подчёркивание.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return new AccountSubmitResult(false, "Введите пароль аккаунта Vesper.");
        }

        var authResult = await TrySubmitCloudAccountAsync(
            normalizedMode,
            normalizedUsername,
            password,
            cancellationToken).ConfigureAwait(false);

        if (!authResult.Success)
        {
            return authResult;
        }

        var token = authResult.Token ?? string.Empty;
        VesperSkinRegistry.SetAccessToken(token);
        _ = RefreshProfileAsync(token, CancellationToken.None);
        _ = RefreshFriendsAsync(token, CancellationToken.None);

        lock (_sync)
        {
            _state.CurrentNickname = normalizedUsername;
            _state.AccountMode = normalizedMode;
            _state.HasAuthenticatedSession = true;
            _state.SessionToken = authResult.Token ?? string.Empty;
            TouchRecentUsername(normalizedUsername);
            SaveState();
        }

        var action = normalizedMode == "register" ? "Аккаунт создан" : "Вход выполнен";
        return new AccountSubmitResult(true, $"{action}: {normalizedUsername}.", authResult.Token);
    }

    public async Task<AccountSubmitResult> LogoutAsync(CancellationToken cancellationToken = default)
    {
        string token;
        lock (_sync)
        {
            token = _state.SessionToken;
        }

        if (!string.IsNullOrWhiteSpace(token) &&
            Uri.TryCreate(_syncConfig.LogoutUrl, UriKind.Absolute, out var uri))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, uri);
                AddConfiguredAuthorizationHeaders(request);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                using var _ = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Local logout must still clear the session when the cloud endpoint is unavailable.
            }
        }

        lock (_sync)
        {
            _state.HasAuthenticatedSession = false;
            _state.SessionToken = string.Empty;
            _state.AccountMode = "login";
            SaveState();
        }

        VesperSkinRegistry.SetAccessToken(null);

        return new AccountSubmitResult(true, "Вы вышли из аккаунта Vesper.");
    }

    public void SetAccountMode(string? mode)
    {
        lock (_sync)
        {
            var normalizedMode = NormalizeMode(mode);
            _state.AccountMode = normalizedMode == "guest" ? "login" : normalizedMode;
            SaveState();
        }
    }

    public void SelectRecentUsername(string? username)
    {
        lock (_sync)
        {
            var normalizedUsername = NormalizeNickname(username);
            if (!UsernameRegex.IsMatch(normalizedUsername))
            {
                return;
            }

            _state.CurrentNickname = normalizedUsername;
            TouchRecentUsername(normalizedUsername);
            SaveState();
        }
    }

    public void SetFriendNickname(string? value)
    {
        lock (_sync)
        {
            _state.FriendNicknameInput = NormalizeNickname(value);
            SaveState();
        }
    }

    public AccountSnapshot CreateAccountSnapshot()
    {
        lock (_sync)
        {
            var nickname = NormalizeNickname(_state.CurrentNickname);
            return new AccountSnapshot(
                Mode: _state.HasAuthenticatedSession ? "summary" : NormalizeMode(_state.AccountMode),
                HasAuthenticatedSession: _state.HasAuthenticatedSession,
                HasStoredProfile: _state.RecentUsernames.Count > 0,
                HasGuestIdentity: false,
                IsEditingGuest: false,
                AccountStateText: _state.HasAuthenticatedSession
                    ? "Сессия активна."
                    : "Сессия не активна. Войдите или зарегистрируйтесь.",
                NicknameInput: nickname,
                CurrentNickname: nickname,
                AvatarUrl: _avatarUrl,
                AvatarPlaceholder: BuildAvatarPlaceholder(nickname),
                CanLogout: _state.HasAuthenticatedSession,
                CanChangeAvatar: _state.HasAuthenticatedSession,
                CanUseGuest: false,
                RecentUsernames: _state.RecentUsernames.Take(MaxRecentUsernames).ToArray(),
                HasEarlyPlayersAchievement: false);
        }
    }

    public FriendsSnapshot CreateFriendsSnapshot()
    {
        lock (_sync)
        {
            var nickname = NormalizeNickname(_state.CurrentNickname);
            return new FriendsSnapshot(
                ProfileNickname: nickname,
                ProfileType: _state.HasAuthenticatedSession ? "Тип входа: Vesper" : "Тип входа: требуется вход",
                CloudStatus: _state.HasAuthenticatedSession
                    ? "Аккаунт Vesper подключен."
                    : "Войдите в аккаунт Vesper, чтобы загрузить облачных друзей.",
                VesperNetStatus: _platform.Features.SupportsVesperNetService
                    ? "VesperNet доступен на Windows. Для Linux/macOS функция будет отключена до отдельного сетевого слоя."
                    : "VesperNet недоступен на этой платформе.",
                ProfileAvatarUrl: _avatarUrl,
                ProfileAvatarPlaceholder: BuildAvatarPlaceholder(nickname),
                FriendNicknameInput: _state.FriendNicknameInput,
                CanManage: _state.HasAuthenticatedSession,
                CanAccess: _state.HasAuthenticatedSession,
                OutgoingRequestCount: _outgoingRequestCount,
                SelectedRequestId: null,
                Friends: _friends,
                IncomingRequests: _incomingRequests);
        }
    }

    private AccountFriendsState LoadState()
    {
        try
        {
            if (!File.Exists(_statePath))
            {
                return CreateDefaultState();
            }

            var loaded = JsonSerializer.Deserialize<AccountFriendsState>(File.ReadAllText(_statePath));
            if (loaded is null)
            {
                return CreateDefaultState();
            }

            loaded.CurrentNickname = NormalizeNickname(loaded.CurrentNickname);
            if (IsImplicitWindowsNickname(loaded.CurrentNickname))
            {
                loaded.CurrentNickname = string.Empty;
                loaded.HasAuthenticatedSession = false;
                loaded.SessionToken = string.Empty;
            }

            loaded.AccountMode = NormalizeMode(loaded.AccountMode);
            loaded.FriendNicknameInput = NormalizeNickname(loaded.FriendNicknameInput);
            loaded.RecentUsernames = loaded.RecentUsernames
                .Select(NormalizeNickname)
                .Where(username => !IsImplicitWindowsNickname(username))
                .Where(username => UsernameRegex.IsMatch(username))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecentUsernames)
                .ToList();
            return loaded;
        }
        catch
        {
            return CreateDefaultState();
        }
    }

    private static AccountFriendsState CreateDefaultState()
    {
        return new AccountFriendsState
        {
            CurrentNickname = string.Empty,
            RecentUsernames = []
        };
    }

    private void SaveState()
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_statePath, json);
        }
        catch
        {
            // The launcher can still run with in-memory account state.
        }
    }

    private void TouchRecentUsername(string username)
    {
        _state.RecentUsernames.RemoveAll(existing => string.Equals(existing, username, StringComparison.OrdinalIgnoreCase));
        _state.RecentUsernames.Insert(0, username);
        if (_state.RecentUsernames.Count > MaxRecentUsernames)
        {
            _state.RecentUsernames.RemoveRange(MaxRecentUsernames, _state.RecentUsernames.Count - MaxRecentUsernames);
        }
    }

    private async Task<AccountSubmitResult> TrySubmitCloudAccountAsync(
        string mode,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var endpoint = mode == "register" ? _syncConfig.RegisterUrl : _syncConfig.LoginUrl;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return new AccountSubmitResult(false, "URL аккаунтов Vesper не настроен.");
        }

        try
        {
            string passwordHashHex;
            string? passwordSaltHex = null;
            int passwordIterations = 120000;

            if (mode == "login")
            {
                var infoUrl = _syncConfig.CredentialInfoUrl;
                if (string.IsNullOrWhiteSpace(infoUrl))
                {
                    var baseUri = new Uri(endpoint);
                    infoUrl = new Uri(baseUri, "/api/v1/auth/credential-info").ToString();
                }

                var infoUri = new Uri($"{infoUrl}?username={Uri.EscapeDataString(username)}");
                using var infoRequest = new HttpRequestMessage(HttpMethod.Get, infoUri);
                AddConfiguredAuthorizationHeaders(infoRequest);

                using var infoResponse = await Http.SendAsync(infoRequest, cancellationToken).ConfigureAwait(false);
                if (!infoResponse.IsSuccessStatusCode)
                {
                    var errJson = await infoResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return new AccountSubmitResult(
                        false,
                        ExtractErrorMessage(errJson) ?? $"Ошибка получения данных аккаунта Vesper: {(int)infoResponse.StatusCode}.");
                }

                var infoJson = await infoResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var infoDoc = JsonDocument.Parse(infoJson);
                var infoRoot = infoDoc.RootElement;
                if (!infoRoot.TryGetProperty("passwordSalt", out var saltElem) || saltElem.ValueKind != JsonValueKind.String)
                {
                    return new AccountSubmitResult(false, "Неверный ответ сервера авторизации (отсутствует salt).");
                }
                passwordSaltHex = saltElem.GetString()!;
                
                if (infoRoot.TryGetProperty("passwordIterations", out var iterElem) && iterElem.TryGetInt32(out var iterVal))
                {
                    passwordIterations = iterVal;
                }
                
                var saltBytes = Convert.FromHexString(passwordSaltHex);
                var hashBytes = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, passwordIterations, HashAlgorithmName.SHA256, 32);
                passwordHashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
            else
            {
                var saltBytes = new byte[16];
                RandomNumberGenerator.Fill(saltBytes);
                passwordSaltHex = Convert.ToHexString(saltBytes).ToLowerInvariant();
                passwordIterations = 120000;

                var hashBytes = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, passwordIterations, HashAlgorithmName.SHA256, 32);
                passwordHashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
            }

            object payload = mode == "login"
                ? new { username, passwordHash = passwordHashHex }
                : new { username, passwordHash = passwordHashHex, passwordSalt = passwordSaltHex, passwordIterations };

            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = JsonContent.Create(payload)
            };

            AddConfiguredAuthorizationHeaders(request);

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new AccountSubmitResult(true, string.Empty, ExtractToken(json));
            }

            return new AccountSubmitResult(
                false,
                ExtractErrorMessage(json) ?? $"Сервер Vesper отклонил запрос: {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AccountSubmitResult(false, "Не удалось подключиться к аккаунтам Vesper: " + ex.Message);
        }
    }

    private async Task RefreshProfileAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_syncConfig.MeUrl)) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _syncConfig.MeUrl);
            AddConfiguredAuthorizationHeaders(request);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("avatar", out var avatarElem) && avatarElem.ValueKind == JsonValueKind.Object)
                {
                    var url = ExtractAvatarUrl(avatarElem);
                    lock (_sync)
                    {
                        _avatarUrl = url;
                    }
                }
            }
        }
        catch
        {
        }
    }

    private async Task RefreshFriendsAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_syncConfig.MeUrl)) return;
        try
        {
            var baseUri = new Uri(_syncConfig.MeUrl);
            var friendsUrl = new Uri(baseUri, "/api/v1/friends").ToString();

            using var request = new HttpRequestMessage(HttpMethod.Get, friendsUrl);
            AddConfiguredAuthorizationHeaders(request);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var mappedFriends = new List<VesperFriendInfo>();
                var mappedIncoming = new List<VesperIncomingFriendRequest>();
                int outgoingCount = 0;

                if (root.TryGetProperty("friends", out var friendsElem) && friendsElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in friendsElem.EnumerateArray())
                    {
                        var username = item.TryGetProperty("username", out var u) ? u.GetString() ?? "Unknown" : "Unknown";
                        var isOnline = item.TryGetProperty("isOnline", out var o) && o.GetBoolean();
                        
                        string avatarUrl = string.Empty;
                        string avatarPlaceholder = BuildAvatarPlaceholder(username);
                        if (item.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.Object)
                        {
                            avatarUrl = ExtractAvatarUrl(av);
                            avatarPlaceholder = av.TryGetProperty("placeholder", out var uPl) ? uPl.GetString() ?? avatarPlaceholder : avatarPlaceholder;
                        }

                        var presenceText = isOnline ? "В сети" : "Офлайн";
                        var activityText = string.Empty;
                        var versionText = string.Empty;
                        var joinAddressText = string.Empty;
                        bool canConnect = false;

                        if (isOnline)
                        {
                            var activityKind = item.TryGetProperty("activityKind", out var ak) ? ak.GetString() : null;
                            var activityName = item.TryGetProperty("activityName", out var an) ? an.GetString() : null;
                            var versionId = item.TryGetProperty("versionId", out var vi) ? vi.GetString() : null;
                            var isJoinable = item.TryGetProperty("isJoinable", out var ij) && ij.GetBoolean();

                            if (activityKind == "game")
                            {
                                presenceText = "В игре";
                                activityText = !string.IsNullOrWhiteSpace(activityName) ? $"Играет в {activityName}" : "Играет в Minecraft";
                                versionText = !string.IsNullOrWhiteSpace(versionId) ? $"Версия: {versionId}" : string.Empty;

                                if (isJoinable)
                                {
                                    var joinHost = item.TryGetProperty("joinHost", out var jh) ? jh.GetString() : null;
                                    var joinPort = item.TryGetProperty("joinPort", out var jp) && jp.TryGetInt32(out var pVal) ? pVal : (int?)null;
                                    if (!string.IsNullOrWhiteSpace(joinHost))
                                    {
                                        joinAddressText = joinPort.HasValue ? $"{joinHost}:{joinPort}" : joinHost;
                                        canConnect = _platform.Features.SupportsVesperNetService;
                                    }
                                }
                            }
                        }

                        mappedFriends.Add(new VesperFriendInfo(
                            Username: username,
                            AvatarUrl: avatarUrl,
                            AvatarPlaceholder: avatarPlaceholder,
                            IsOnline: isOnline,
                            PresenceText: presenceText,
                            ActivityText: activityText,
                            VersionText: versionText,
                            JoinAddressText: joinAddressText,
                            CanConnect: canConnect,
                            RelayRoomId: item.TryGetProperty("relayRoomId", out var rrElem) ? rrElem.GetString() : null
                        ));
                    }
                }

                if (root.TryGetProperty("incomingRequests", out var incomingElem) && incomingElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in incomingElem.EnumerateArray())
                    {
                        var requestId = item.TryGetProperty("id", out var idElem) ? idElem.GetInt32().ToString() : "0";
                        var createdAt = item.TryGetProperty("createdAtUtc", out var caElem) ? caElem.GetString() ?? string.Empty : string.Empty;
                        
                        string username = "Unknown";
                        string avatarUrl = string.Empty;
                        string avatarPlaceholder = "AV";

                        if (item.TryGetProperty("user", out var userElem) && userElem.ValueKind == JsonValueKind.Object)
                        {
                            username = userElem.TryGetProperty("username", out var u) ? u.GetString() ?? "Unknown" : "Unknown";
                            avatarPlaceholder = BuildAvatarPlaceholder(username);

                            if (userElem.TryGetProperty("avatar", out var av) && av.ValueKind == JsonValueKind.Object)
                            {
                                avatarUrl = ExtractAvatarUrl(av);
                                avatarPlaceholder = av.TryGetProperty("placeholder", out var uPl) ? uPl.GetString() ?? avatarPlaceholder : avatarPlaceholder;
                            }
                        }

                        mappedIncoming.Add(new VesperIncomingFriendRequest(
                            RequestId: requestId,
                            Username: username,
                            AvatarUrl: avatarUrl,
                            AvatarPlaceholder: avatarPlaceholder,
                            SubtitleText: $"Отправил запрос: {createdAt}"
                        ));
                    }
                }

                if (root.TryGetProperty("outgoingRequests", out var outgoingElem) && outgoingElem.ValueKind == JsonValueKind.Array)
                {
                    outgoingCount = outgoingElem.GetArrayLength();
                }

                lock (_sync)
                {
                    _friends = mappedFriends;
                    _incomingRequests = mappedIncoming;
                    _outgoingRequestCount = outgoingCount;
                }
            }
        }
        catch
        {
        }
    }

    public async Task<AccountSubmitResult> AddFriendAsync(CancellationToken cancellationToken = default)
    {
        string token;
        string friendUsername;
        lock (_sync)
        {
            token = _state.SessionToken;
            friendUsername = _state.FriendNicknameInput;
        }

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(friendUsername))
        {
            return new AccountSubmitResult(false, "Не удалось добавить друга: отсутствует имя или сессия.");
        }

        if (string.IsNullOrWhiteSpace(_syncConfig.MeUrl))
        {
            return new AccountSubmitResult(false, "API URL не настроен.");
        }

        try
        {
            var baseUri = new Uri(_syncConfig.MeUrl);
            var url = new Uri(baseUri, "/api/v1/friends/request").ToString();

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { username = friendUsername })
            };
            AddConfiguredAuthorizationHeaders(request);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                lock (_sync)
                {
                    _state.FriendNicknameInput = string.Empty;
                    SaveState();
                }
                _ = RefreshFriendsAsync(token, cancellationToken);
                return new AccountSubmitResult(true, "Заявка в друзья успешно отправлена.");
            }

            return new AccountSubmitResult(false, ExtractErrorMessage(json) ?? $"Ошибка отправки заявки: {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new AccountSubmitResult(false, "Не удалось отправить заявку: " + ex.Message);
        }
    }

    public async Task<AccountSubmitResult> RespondFriendRequestAsync(string requestId, string action, CancellationToken cancellationToken = default)
    {
        string token;
        lock (_sync)
        {
            token = _state.SessionToken;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return new AccountSubmitResult(false, "Сессия не активна.");
        }

        if (string.IsNullOrWhiteSpace(_syncConfig.MeUrl))
        {
            return new AccountSubmitResult(false, "API URL не настроен.");
        }

        try
        {
            var baseUri = new Uri(_syncConfig.MeUrl);
            var url = new Uri(baseUri, "/api/v1/friends/respond").ToString();

            int reqIdVal = int.TryParse(requestId, out var parsed) ? parsed : 0;

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { requestId = reqIdVal, action })
            };
            AddConfiguredAuthorizationHeaders(request);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _ = RefreshFriendsAsync(token, cancellationToken);
                return new AccountSubmitResult(true, action == "accept" ? "Заявка принята." : "Заявка отклонена.");
            }

            return new AccountSubmitResult(false, ExtractErrorMessage(json) ?? $"Ошибка ответа на заявку: {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new AccountSubmitResult(false, "Не удалось ответить на заявку: " + ex.Message);
        }
    }

    public async Task<AccountSubmitResult> RemoveFriendAsync(string friendUsername, CancellationToken cancellationToken = default)
    {
        string token;
        lock (_sync)
        {
            token = _state.SessionToken;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return new AccountSubmitResult(false, "Сессия не активна.");
        }

        if (string.IsNullOrWhiteSpace(_syncConfig.MeUrl))
        {
            return new AccountSubmitResult(false, "API URL не настроен.");
        }

        try
        {
            var baseUri = new Uri(_syncConfig.MeUrl);
            var url = new Uri(baseUri, "/api/v1/friends/remove").ToString();

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(new { username = friendUsername })
            };
            AddConfiguredAuthorizationHeaders(request);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _ = RefreshFriendsAsync(token, cancellationToken);
                return new AccountSubmitResult(true, "Друг успешно удален.");
            }

            return new AccountSubmitResult(false, ExtractErrorMessage(json) ?? $"Ошибка удаления друга: {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return new AccountSubmitResult(false, "Не удалось удалить друга: " + ex.Message);
        }
    }

    private AccountSyncConfig LoadSyncConfig()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "account-sync.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "account-sync.json"))
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                var loaded = JsonSerializer.Deserialize<AccountSyncConfig>(File.ReadAllText(candidate));
                if (loaded is not null)
                {
                    return loaded;
                }
            }
            catch
            {
            }
        }

        return new AccountSyncConfig();
    }

    private static string NormalizeMode(string? mode)
    {
        return string.Equals(mode, "register", StringComparison.OrdinalIgnoreCase) ? "register" : "login";
    }

    private static string NormalizeNickname(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return string.Empty;
        }

        var filtered = new string(username.Trim().Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? string.Empty : filtered[..Math.Min(filtered.Length, 16)];
    }

    private static string BuildAvatarPlaceholder(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "AV" : value.Trim();
        return text.Length <= 2 ? text.ToUpperInvariant() : text[..2].ToUpperInvariant();
    }

    private static string ExtractAvatarUrl(JsonElement avatarElement)
    {
        foreach (var propertyName in new[] { "url", "imageUrl" })
        {
            if (avatarElement.TryGetProperty(propertyName, out var urlElement) &&
                urlElement.ValueKind == JsonValueKind.String)
            {
                var url = urlElement.GetString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url.Trim();
                }
            }
        }

        if (avatarElement.TryGetProperty("imageBase64", out var base64Element) &&
            base64Element.ValueKind == JsonValueKind.String)
        {
            var base64 = base64Element.GetString();
            if (!string.IsNullOrWhiteSpace(base64))
            {
                var contentType = "image/png";
                if (avatarElement.TryGetProperty("contentType", out var contentTypeElement) &&
                    contentTypeElement.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(contentTypeElement.GetString()))
                {
                    contentType = contentTypeElement.GetString()!.Trim();
                }

                return $"data:{contentType};base64,{base64.Trim()}";
            }
        }

        return string.Empty;
    }

    private static string? ExtractToken(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            foreach (var property in new[] { "token", "accessToken", "access_token", "sessionToken" })
            {
                if (root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
                {
                    return element.GetString();
                }
            }

            foreach (var container in new[] { "session", "data", "auth", "result" })
            {
                if (!root.TryGetProperty(container, out var nested) || nested.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var property in new[] { "token", "accessToken", "access_token", "sessionToken" })
                {
                    if (nested.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
                    {
                        return element.GetString();
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string? ExtractErrorMessage(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            foreach (var property in new[] { "message", "error", "detail" })
            {
                if (root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String)
                {
                    return element.GetString();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool IsImplicitWindowsNickname(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
            string.Equals(NormalizeNickname(value), NormalizeNickname(Environment.UserName), StringComparison.OrdinalIgnoreCase);
    }

    private void AddConfiguredAuthorizationHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_syncConfig.AuthorizationHeaderName) &&
            !string.IsNullOrWhiteSpace(_syncConfig.AuthorizationHeaderValue))
        {
            var headerValue = _syncConfig.AuthorizationHeaderValue;
            if (headerValue.StartsWith("Bearer B64:", StringComparison.Ordinal))
            {
                var base64Part = headerValue.Substring("Bearer B64:".Length).Trim();
                try
                {
                    var decodedBytes = Convert.FromBase64String(base64Part);
                    var decodedValue = System.Text.Encoding.UTF8.GetString(decodedBytes);
                    headerValue = "Bearer " + decodedValue;
                }
                catch
                {
                    // Fallback to raw value
                }
            }

            request.Headers.TryAddWithoutValidation(
                _syncConfig.AuthorizationHeaderName,
                headerValue);
        }
    }

    private sealed class AccountFriendsState
    {
        public string AccountMode { get; set; } = "login";
        public string CurrentNickname { get; set; } = string.Empty;
        public string FriendNicknameInput { get; set; } = string.Empty;
        public List<string> RecentUsernames { get; set; } = [];
        public bool HasAuthenticatedSession { get; set; }
        public string SessionToken { get; set; } = string.Empty;
    }

    private sealed class AccountSyncConfig
    {
        public string RegisterUrl { get; set; } = string.Empty;
        public string LoginUrl { get; set; } = string.Empty;
        public string CredentialInfoUrl { get; set; } = string.Empty;
        public string MeUrl { get; set; } = string.Empty;
        public string LogoutUrl { get; set; } = string.Empty;
        public string AuthorizationHeaderName { get; set; } = string.Empty;
        public string AuthorizationHeaderValue { get; set; } = string.Empty;
    }

    public string? TryGetFriendRelayRoomId(string friendUsername)
    {
        lock (_sync)
        {
            var friend = _friends.FirstOrDefault(f => string.Equals(f.Username, friendUsername, StringComparison.OrdinalIgnoreCase));
            return friend?.RelayRoomId;
        }
    }

    public async Task<VesperGuestRelayTunnel?> CreateFriendTunnelAsync(string roomId, CancellationToken cancellationToken = default)
    {
        string token;
        lock (_sync)
        {
            token = _state.SessionToken;
        }

        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(_syncConfig.MeUrl))
        {
            return null;
        }

        var baseUri = new Uri(_syncConfig.MeUrl);
        var connectUrl = new Uri(baseUri, "/api/v1/relay/connect").ToString();

        try
        {
            return await VesperFriendRelay.CreateGuestTunnelAsync(Http, connectUrl, token, roomId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}

public sealed record VesperFriendInfo(
    string Username,
    string AvatarUrl,
    string AvatarPlaceholder,
    bool IsOnline,
    string PresenceText,
    string ActivityText,
    string VersionText,
    string JoinAddressText,
    bool CanConnect,
    string? RelayRoomId);

public sealed record VesperIncomingFriendRequest(
    string RequestId,
    string Username,
    string AvatarUrl,
    string AvatarPlaceholder,
    string SubtitleText);

internal sealed record AccountSubmitResult(bool Success, string Message, string? Token = null);

internal sealed record AccountSnapshot(
    string Mode,
    bool HasAuthenticatedSession,
    bool HasStoredProfile,
    bool HasGuestIdentity,
    bool IsEditingGuest,
    string AccountStateText,
    string NicknameInput,
    string CurrentNickname,
    string AvatarUrl,
    string AvatarPlaceholder,
    bool CanLogout,
    bool CanChangeAvatar,
    bool CanUseGuest,
    IReadOnlyList<string> RecentUsernames,
    bool HasEarlyPlayersAchievement);

internal sealed record FriendsSnapshot(
    string ProfileNickname,
    string ProfileType,
    string CloudStatus,
    string VesperNetStatus,
    string ProfileAvatarUrl,
    string ProfileAvatarPlaceholder,
    string FriendNicknameInput,
    bool CanManage,
    bool CanAccess,
    int OutgoingRequestCount,
    string? SelectedRequestId,
    IReadOnlyList<object> Friends,
    IReadOnlyList<object> IncomingRequests);
