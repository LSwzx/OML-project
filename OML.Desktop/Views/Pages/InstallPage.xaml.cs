using System.Windows.Controls;
using OML.Desktop.ViewModels;

namespace OML.Desktop.Views.Pages;

/// <summary>安装页：首次显示时加载在线版本清单。</summary>
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
    }
}
