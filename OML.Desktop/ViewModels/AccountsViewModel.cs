using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OML.Abstractions.Auth;
using OML.Core.Auth;
using OML.Desktop.Infrastructure;

namespace OML.Desktop.ViewModels;

/// <summary>
/// 账号页：当前账号展示 + 离线 / LittleSkin 登录。
/// <para>微软登录等 Mojang 白名单通过后再接（服务层已实现）。</para>
/// </summary>
public partial class AccountsViewModel : ObservableObject
{
    private readonly LauncherContext _context;
    private readonly YggdrasilAuthService _yggdrasil = new();

    public AccountsViewModel(LauncherContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    [ObservableProperty]
    private UserProfile? currentProfile;

    [ObservableProperty]
    private string offlineUsername = "Player";

    [ObservableProperty]
    private string littleSkinEmail = string.Empty;

    [ObservableProperty]
    private string littleSkinPassword = string.Empty;

    [ObservableProperty]
    private string statusText = "尚未登录，启动时会自动创建离线账号。";

    [ObservableProperty]
    private bool isBusy;

    public string ProfileName => CurrentProfile?.Username ?? "未登录";

    /// <summary>头像占位用的首字母。</summary>
    public string ProfileInitial =>
        string.IsNullOrWhiteSpace(CurrentProfile?.Username)
            ? "?"
            : CurrentProfile!.Username[..1].ToUpperInvariant();

    public string ProfileUuid => CurrentProfile?.Uuid ?? "—";

    public string ProfileType => CurrentProfile?.Type ?? "—";

    public string ProfileSkin => CurrentProfile?.SkinModel ?? "—";

    public bool HasProfile => CurrentProfile is not null;

    partial void OnCurrentProfileChanged(UserProfile? value)
    {
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ProfileInitial));
        OnPropertyChanged(nameof(ProfileUuid));
        OnPropertyChanged(nameof(ProfileType));
        OnPropertyChanged(nameof(ProfileSkin));
        OnPropertyChanged(nameof(HasProfile));
    }

    /// <summary>从共享配置同步（初始化/配置变更时调用）。</summary>
    public void RefreshFromConfig()
    {
        CurrentProfile = _context.Config.UserProfile;
        if (CurrentProfile is not null)
        {
            StatusText = $"当前账号：{CurrentProfile.Username}（{CurrentProfile.Type}）";
        }
    }

    [RelayCommand]
    private async Task CreateOfflineAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var profile = OfflineAuthService.CreateProfile(OfflineUsername);
            await ApplyProfileAsync(profile);
            StatusText = $"已创建离线账号：{profile.Username}";
        }
        catch (ArgumentException ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoginLittleSkinAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            StatusText = "正在登录 LittleSkin…";
            var session = await _yggdrasil.LoginAsync(LittleSkinEmail, LittleSkinPassword);

            if (string.IsNullOrWhiteSpace(session.Profile.AccessToken))
            {
                // Yggdrasil 的令牌是 accessToken 而不是档案里的字段，回填便于持久化。
                session.Profile.AccessToken = session.AccessToken;
            }

            await ApplyProfileAsync(session.Profile);
            StatusText = $"已登录：{session.Profile.Username}（皮肤：{session.Profile.SkinModel ?? "未知"}）";
        }
        catch (YggdrasilException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = $"登录失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>调试入口：直接创建一个离线账号（供自动化截图/冒烟使用）。</summary>
    public async Task CreateOfflineAccountAsync(string username)
    {
        OfflineUsername = username;
        await CreateOfflineAsync();
    }

    private async Task ApplyProfileAsync(UserProfile profile)
    {
        _context.Config.UserProfile = profile;
        await _context.SaveAsync();
        CurrentProfile = profile;
    }
}
