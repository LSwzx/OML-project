using System.Windows.Controls;

namespace OML.Desktop.Views.Pages;

/// <summary>资源页（逻辑全在 ViewModel）。</summary>
public partial class ModsPage : Page
{
    public ModsPage()
    {
        InitializeComponent();
        DataContext = App.Shell?.Mods;
    }
}
