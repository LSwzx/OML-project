using System.Windows.Controls;

namespace OML.Desktop.Views.Pages;

/// <summary>账号页（导航页）。</summary>
public partial class AccountsPage : Page
{
    public AccountsPage()
    {
        InitializeComponent();
        DataContext = App.Shell?.Accounts;
    }
}
