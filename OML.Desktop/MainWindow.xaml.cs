using System.Windows;
using System.Windows.Controls;
using OML.Desktop.Infrastructure;
using OML.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace OML.Desktop;

/// <summary>外壳窗口：只负责导航，业务全在 ViewModel。</summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // NavigationView 默认没有页面提供者，必须注入后才能 Navigate(Type)。
        RootNavigation.SetPageProviderService(new NavigationPageProvider());
    }

    /// <summary>按标签切页（供启动参数与导航栏共用）。</summary>
    public void NavigateTo(string tag)
    {
        foreach (var item in RootNavigation.MenuItems)
        {
            if (item is NavigationViewItem navigationItem &&
                string.Equals(navigationItem.Tag as string, tag, StringComparison.OrdinalIgnoreCase) &&
                navigationItem.TargetPageType is { } pageType)
            {
                navigationItem.Activate(RootNavigation);
                RootNavigation.Navigate(pageType, null);
                break;
            }
        }

        if (DataContext is ShellViewModel shell)
        {
            shell.Navigate(tag);
        }
    }
}
