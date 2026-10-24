using System.Windows.Controls;
using System.Windows.Input;
using OML.Desktop.ViewModels;

namespace OML.Desktop.Views.Pages;

/// <summary>下载页：版本列表 + 点击后的附件向导。</summary>
public partial class InstallPage : Page
{
    private bool _loaded;

    public InstallPage()
    {
        InitializeComponent();
        DataContext = App.Shell?.Install;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_loaded || DataContext is not InstallViewModel viewModel)
        {
            return;
        }

        _loaded = true;
        viewModel.LoadManifestCommand.Execute(null);

        // 开发/自动化：直接打开向导面板，方便离屏检查布局。
        if (Environment.GetCommandLineArgs().Contains("--install-detail"))
        {
            viewModel.IsDetailOpen = true;
        }
    }

    private void OnVersionListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is InstallViewModel { IsDetailOpen: false, SelectedVersion: { } version } viewModel)
        {
            viewModel.OpenDetailCommand.Execute(version);
        }
    }
}
