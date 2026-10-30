using System.Windows.Controls;
using Wpf.Ui.Controls;
using UiContentDialog = Wpf.Ui.Controls.ContentDialog;
using UiMessageBox = Wpf.Ui.Controls.MessageBox;
using UiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace OML.Desktop.Infrastructure;

/// <summary>弹窗选择结果。</summary>
public enum DialogChoice
{
    None,
    Primary,
    Secondary,
    Close,
}

/// <summary>全局弹窗服务。</summary>
public interface IDialogService
{
    Task<bool> ShowConfirmAsync(string title, string message, string confirmText = "确定", string cancelText = "取消");

    Task ShowInfoAsync(string title, string message);

    Task ShowErrorAsync(string title, string message);

    Task<DialogChoice> ShowChoiceAsync(
        string title,
        string message,
        string primaryText,
        string? secondaryText = null,
        string? closeText = null);

    /// <summary>只保留一个主按钮的强制弹窗（用于必须更新）。</summary>
    Task ShowMandatoryAsync(string title, object content, string primaryText);
}

/// <summary>
/// 基于 WPF-UI 的弹窗服务：优先使用主窗口里的 <see cref="ContentDialogHost"/>，
/// 主机尚未就绪时退回 <see cref="Wpf.Ui.Controls.MessageBox"/>（同样是深色主题）。
/// </summary>
public sealed class DialogService : IDialogService
{
    public static DialogService Instance { get; } = new();

    private ContentDialogHost? _host;

    /// <summary>由主窗口在初始化时注入。</summary>
    public void SetHost(ContentDialogHost host) => _host = host;

    public Task<bool> ShowConfirmAsync(
        string title,
        string message,
        string confirmText = "确定",
        string cancelText = "取消") =>
        ShowChoiceAsync(title, message, confirmText, secondaryText: null, closeText: cancelText)
            .ContinueWith(task => task.Result == DialogChoice.Primary, TaskScheduler.FromCurrentSynchronizationContext());

    public Task ShowInfoAsync(string title, string message) =>
        ShowChoiceAsync(title, message, "确定");

    public Task ShowErrorAsync(string title, string message) =>
        ShowChoiceAsync(title, message, "确定");

    public async Task ShowMandatoryAsync(string title, object content, string primaryText)
    {
        if (_host is null)
        {
            var text = content as string ?? content?.ToString() ?? string.Empty;
            await ShowChoiceAsync(title, text, primaryText);
            return;
        }

        var panel = new System.Windows.Controls.StackPanel();
        if (content is System.Windows.UIElement element)
        {
            panel.Children.Add(element);
        }
        else
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = content?.ToString() ?? string.Empty,
                TextWrapping = System.Windows.TextWrapping.Wrap,
            });
        }

        var dialog = new UiContentDialog(_host)
        {
            Title = title,
            Content = panel,
            IsFooterVisible = false,
        };

        var primaryButton = new Wpf.Ui.Controls.Button
        {
            Content = primaryText,
            Appearance = ControlAppearance.Primary,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Height = 36,
            MinWidth = 140,
            Margin = new System.Windows.Thickness(0, 16, 0, 0),
        };

        var closeRequestedByButton = false;
        primaryButton.Click += (_, _) =>
        {
            closeRequestedByButton = true;
            dialog.Hide(ContentDialogResult.Primary);
        };

        // 强制更新：除了「立即更新」按钮，任何关闭请求（Esc / 遮罩）都拒绝。
        dialog.Closing += (_, args) =>
        {
            if (!closeRequestedByButton)
            {
                args.Cancel = true;
            }
        };

        panel.Children.Add(primaryButton);

        await dialog.ShowAsync();
    }
    public async Task<DialogChoice> ShowChoiceAsync(
        string title,
        string message,
        string primaryText,
        string? secondaryText = null,
        string? closeText = null)
    {
        // 没有 host（例如启动早期）时用 WPF-UI 的独立 MessageBox。
        if (_host is null)
        {
            return await ShowFallbackAsync(title, message, primaryText, secondaryText, closeText);
        }

        var dialog = new UiContentDialog(_host)
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = message,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                MaxWidth = 560,
            },
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText ?? string.Empty,
            CloseButtonText = closeText ?? string.Empty,
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => DialogChoice.Primary,
            ContentDialogResult.Secondary => DialogChoice.Secondary,
            ContentDialogResult.None => DialogChoice.Close,
            _ => DialogChoice.None,
        };
    }

    private static async Task<DialogChoice> ShowFallbackAsync(
        string title,
        string message,
        string primaryText,
        string? secondaryText,
        string? closeText)
    {
        var box = new UiMessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryText,
            SecondaryButtonText = secondaryText ?? string.Empty,
            CloseButtonText = closeText ?? string.Empty,
        };

        var result = await box.ShowDialogAsync();
        return result switch
        {
            UiMessageBoxResult.Primary => DialogChoice.Primary,
            UiMessageBoxResult.Secondary => DialogChoice.Secondary,
            _ => DialogChoice.None,
        };
    }
}
