using System.Windows.Controls;

namespace OML.Desktop.Views.Pages;

/// <summary>皮肤页（占位）。</summary>
public partial class SkinPage : Page
{
    public SkinPage()
    {
        InitializeComponent();
        DataContext = App.Shell?.Home;
    }
}
