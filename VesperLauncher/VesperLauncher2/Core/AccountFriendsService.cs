using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using VesperLauncher.Platform;

namespace VesperLauncher.Core;

internal sealed class AccountFriendsService
{
    private const int MaxRecentUsernames = 8;
    private static readonly Regex UsernameRegex = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);
    private readonly IPlatformService _platform;
    private readonly object _sync = new();
    private readonly string _statePath;
    private AccountFriendsState _state;

    public AccountFriendsService(IPlatformService platform)
    {
        _platform = platform;
        _statePath = Path.Combine(platform.Paths.GetLauncherDataDirectory(), "account-friends-state.json");
        _state = LoadState();
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

    public AccountSubmitResult SubmitAccount(string? mode, string? username, string? password)
    {
        lock (_sync)
        {
            var normalizedMode = NormalizeMode(mode);
            if (normalizedMode == "guest")
            {
                return new AccountSubmitResult(false, "Инкогнито-аккаунты отключены. Войдите или зарегистрируйтесь.");
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

            _state.CurrentNickname = normalizedUsername;
            _state.AccountMode = normalizedMode;
            TouchRecentUsername(normalizedUsername);
            SaveState();

            var action = normalizedMode == "register" ? "Регистрация подготовлена" : "Вход подготовлен";
            return new AccountSubmitResult(true, $"{action} для {normalizedUsername}. Облачный API будет подключён следующим шагом.");
        }
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
                Mode: NormalizeMode(_state.AccountMode),
                HasAuthenticatedSession: false,
                HasStoredProfile: _state.RecentUsernames.Count > 0,
                HasGuestIdentity: false,
                IsEditingGuest: false,
                AccountStateText: "Сессия не активна. Войдите или зарегистрируйтесь.",
                NicknameInput: nickname,
                CurrentNickname: nickname,
                AvatarUrl: string.Empty,
                AvatarPlaceholder: BuildAvatarPlaceholder(nickname),
                CanLogout: false,
                CanChangeAvatar: false,
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
                ProfileType: "Тип входа: требуется вход",
                CloudStatus: "Войдите в аккаунт Vesper, чтобы загрузить облачных друзей.",
                VesperNetStatus: _platform.Features.SupportsVesperNetService
                    ? "VesperNet доступен на Windows. Для Linux/macOS функция будет отключена до отдельного сетевого слоя."
                    : "VesperNet недоступен на этой платформе.",
                ProfileAvatarUrl: string.Empty,
                ProfileAvatarPlaceholder: BuildAvatarPlaceholder(nickname),
                FriendNicknameInput: _state.FriendNicknameInput,
                CanManage: false,
                CanAccess: false,
                OutgoingRequestCount: 0,
                SelectedRequestId: null,
                Friends: Array.Empty<object>(),
                IncomingRequests: Array.Empty<object>());
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

            var json = File.ReadAllText(_statePath);
            var loaded = JsonSerializer.Deserialize<AccountFriendsState>(json);
            if (loaded is null)
            {
                return CreateDefaultState();
            }

            loaded.CurrentNickname = NormalizeNickname(loaded.CurrentNickname);
            loaded.AccountMode = NormalizeMode(loaded.AccountMode);
            loaded.FriendNicknameInput = NormalizeNickname(loaded.FriendNicknameInput);
            loaded.RecentUsernames = loaded.RecentUsernames
                .Select(NormalizeNickname)
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

    private AccountFriendsState CreateDefaultState()
    {
        var nickname = NormalizeNickname(Environment.UserName);
        return new AccountFriendsState
        {
            CurrentNickname = nickname,
            RecentUsernames = UsernameRegex.IsMatch(nickname) ? [nickname] : []
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

    private static string NormalizeMode(string? mode)
    {
        return string.Equals(mode, "register", StringComparison.OrdinalIgnoreCase) ? "register" : "login";
    }

    private static string NormalizeNickname(string? username)
    {
        var value = string.IsNullOrWhiteSpace(username) ? "Player123" : username.Trim();
        var filtered = new string(value.Where(character => char.IsLetterOrDigit(character) || character == '_').ToArray());
        return string.IsNullOrWhiteSpace(filtered) ? "Player123" : filtered[..Math.Min(filtered.Length, 16)];
    }

    private static string BuildAvatarPlaceholder(string value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "AV" : value.Trim();
        return text.Length <= 2 ? text.ToUpperInvariant() : text[..2].ToUpperInvariant();
    }

    private sealed class AccountFriendsState
    {
        public string AccountMode { get; set; } = "login";
        public string CurrentNickname { get; set; } = "Player123";
        public string FriendNicknameInput { get; set; } = string.Empty;
        public List<string> RecentUsernames { get; set; } = [];
    }
}

internal sealed record AccountSubmitResult(bool Success, string Message);

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

