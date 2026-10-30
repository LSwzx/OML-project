using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OML.Abstractions.Auth;
using OML.Core.Auth;
using OML.Desktop.Infrastructure;

namespace OML.Desktop.ViewModels;

/// <summary>
/// 账号页：当前账号展示 + 微软正版登录（设备码流程）。
/// <para>
/// 合规构建（<c>STRICT_ONLINE_ONLY</c>）下，离线与 LittleSkin 认证在编译期被物理剔除，
/// 只允许微软正版账号。
/// </para>
/// </summary>
public partial class AccountsViewModel : ObservableObject
{
    private readonly LauncherContext _context;

#if !STRICT_ONLINE_ONLY
    private readonly YggdrasilAuthService _yggdrasil = new();
#endif

    private CancellationTokenSource? _loginCts;

    public AccountsViewModel(LauncherContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    [ObservableProperty]
    private UserProfile? currentProfile;

#if !STRICT_ONLINE_ONLY
    [ObservableProperty]
    private string offlineUsername = "Player";

    [ObservableProperty]
    private string littleSkinEmail = string.Empty;

    [ObservableProperty]
    private string littleSkinPassword = string.Empty;
#endif

    [ObservableProperty]
    private string statusText = "尚未登录微软账号。";

    [ObservableProperty]
    private bool isBusy;

    // ---------------------------------------------------------------- 微软设备码

    [ObservableProperty]
    private bool isMicrosoftLoginActive;

    [ObservableProperty]
    private string microsoftUserCode = string.Empty;

    [ObservableProperty]
    private string microsoftVerificationUri = string.Empty;

    [ObservableProperty]
    private string microsoftLoginStatus = string.Empty;

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

#if STRICT_ONLINE_ONLY
        if (CurrentProfile is not null &&
            !string.Equals(CurrentProfile.Type, AuthTypes.Microsoft, StringComparison.OrdinalIgnoreCase))
        {
            // 旧配置里的离线/LittleSkin 账号：不再允许使用。
            CurrentProfile = null;
            _context.Config.UserProfile = null;
            StatusText = "不支持此账号类型：当前版本仅支持微软正版登录。";
            _ = _context.SaveAsync();
            return;
        }
#endif

        StatusText = CurrentProfile is null
            ? "尚未登录微软账号。"
            : $"当前账号：{CurrentProfile.Username}（{CurrentProfile.Type}）";
    }

    /// <summary>发起微软设备码登录并显示 user_code / 验证地址。</summary>
    [RelayCommand]
    private async Task StartMicrosoftLoginAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        IsMicrosoftLoginActive = true;
        MicrosoftUserCode = string.Empty;
        MicrosoftVerificationUri = string.Empty;
        MicrosoftLoginStatus = "正在向微软请求设备代码…";
        _loginCts = new CancellationTokenSource();

        MicrosoftAuthService? microsoft = null;

        try
        {
            // 延迟创建：未配置 Client ID 时构造函数会抛错，不能让它拖垮整个启动器。
            microsoft = new MicrosoftAuthService();

            var code = await microsoft.StartDeviceCodeAsync(_loginCts.Token);
            MicrosoftUserCode = code.UserCode;
            MicrosoftVerificationUri = code.VerificationUri;
            MicrosoftLoginStatus = "请在浏览器中打开下面的地址，并输入代码。";
            StatusText = $"等待微软授权（{code.ExpiresAt:HH:mm} 前有效）…";

            try
            {
                System.Windows.Clipboard.SetText(code.UserCode);
            }
            catch
            {
                // 剪贴板被占用不影响登录。
            }

            var session = await microsoft.WaitForAuthorizationAsync(
                code,
                new Progress<string>(message => MicrosoftLoginStatus = message),
                _loginCts.Token);

            await ApplyProfileAsync(session.Profile);
            StatusText = $"已登录微软账号：{session.Profile.Username}";
            MicrosoftLoginStatus = "授权成功。";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消微软登录。";
            MicrosoftLoginStatus = "已取消。";
        }
        catch (MicrosoftAuthException ex)
        {
            StatusText = $"微软登录失败：{ex.Message}";
            MicrosoftLoginStatus = ex.Message;
            await DialogService.Instance.ShowErrorAsync("微软登录失败", ex.Message);
        }
        catch (Exception ex)
        {
            StatusText = $"微软登录失败：{ex.Message}";
            MicrosoftLoginStatus = ex.Message;
            await DialogService.Instance.ShowErrorAsync("微软登录失败", ex.Message);
        }
        finally
        {
            microsoft?.Dispose();
            IsMicrosoftLoginActive = false;
            IsBusy = false;
            _loginCts?.Dispose();
            _loginCts = null;
        }
    }

    [RelayCommand]
    private void CopyMicrosoftCode()
    {
        if (string.IsNullOrWhiteSpace(MicrosoftUserCode))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(MicrosoftUserCode);
            MicrosoftLoginStatus = "代码已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            MicrosoftLoginStatus = $"复制失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenMicrosoftVerification()
    {
        if (string.IsNullOrWhiteSpace(MicrosoftVerificationUri))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(MicrosoftVerificationUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MicrosoftLoginStatus = $"打开浏览器失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelMicrosoftLogin()
    {
        _loginCts?.Cancel();
        IsMicrosoftLoginActive = false;
        MicrosoftLoginStatus = "已取消。";
    }

#if !STRICT_ONLINE_ONLY
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
#endif

    private async Task ApplyProfileAsync(UserProfile profile)
    {
        _context.Config.UserProfile = profile;
        await _context.SaveAsync();
        CurrentProfile = profile;
    }
}
