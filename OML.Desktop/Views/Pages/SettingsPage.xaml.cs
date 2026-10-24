using System.Windows;
using System.Windows.Controls;
using OML.Desktop.ViewModels;

namespace OML.Desktop.Views.Pages;

/// <summary>设置页：首次显示时触发一次 Java 探测。</summary>
public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = App.Shell?.Settings;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel viewModel)
        {
            viewModel.EnsureJavaScanned();
        }
    }
}
