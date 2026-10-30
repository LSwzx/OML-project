using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OML.Abstractions.Auth;
using OML.Core.Auth;
using OML.Desktop.Infrastructure;

namespace OML.Desktop.ViewModels;

/// <summary>
/// 账号页：当前账号展示 + 微软正版登录（设备码流程）。
/// <para>仅支持微软正版账号；离线与外置登录已从代码库中删除。</para>
/// </summary>
public partial class AccountsViewModel : ObservableObject
{
    private const string PendingReviewMessage =
        "微软正版登录等待 Mojang 应用白名单审核，当前仅支持已过审的测试环境，暂未对普通用户开放。";

    private readonly LauncherContext _context;
    private CancellationTokenSource? _loginCts;

    public AccountsViewModel(LauncherContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    [ObservableProperty]
    private UserProfile? currentProfile;

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

        if (CurrentProfile is not null &&
            !string.Equals(CurrentProfile.Type, AuthTypes.Microsoft, StringComparison.OrdinalIgnoreCase))
        {
            // 旧配置里的非微软账号：不再允许使用。
            CurrentProfile = null;
            _context.Config.UserProfile = null;
            StatusText = "不支持此账号类型：当前版本仅支持微软正版登录。";
            _ = _context.SaveAsync();
            return;
        }

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

        var hasEnvironmentClientId =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(MicrosoftAuthService.ClientIdEnvironmentVariable));

        if (string.IsNullOrWhiteSpace(MicrosoftAuthService.DefaultClientId) && !hasEnvironmentClientId)
        {
            MicrosoftLoginStatus = "等待 Mojang 审核中，暂不可用。";
            StatusText = MicrosoftLoginStatus;
            await DialogService.Instance.ShowInfoAsync("微软登录暂不可用", PendingReviewMessage);
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
        catch (InvalidOperationException ex) when (ex.Message.Contains("Client ID", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "等待 Mojang 审核中，暂不可用。";
            MicrosoftLoginStatus = StatusText;
            await DialogService.Instance.ShowInfoAsync("微软登录暂不可用", PendingReviewMessage);
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

    private async Task ApplyProfileAsync(UserProfile profile)
    {
        _context.Config.UserProfile = profile;
        await _context.SaveAsync();
        CurrentProfile = profile;
    }
}
