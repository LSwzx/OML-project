using System.Windows.Controls;

namespace OML.Desktop.Views.Pages;

/// <summary>主页（导航页）。DataContext 来自全局 Shell。</summary>
public partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        DataContext = App.Shell?.Home;
    }

    private void LogBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }
}
