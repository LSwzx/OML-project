using System.Windows.Controls;

namespace OML.Desktop.Views.Pages;

/// <summary>设置页（导航页）。</summary>
public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = App.Shell?.Settings;
    }
}
