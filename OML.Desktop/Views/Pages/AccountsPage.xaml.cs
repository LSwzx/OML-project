using System.Windows;
using System.Windows.Controls;

namespace OML.Desktop.Views.Pages;

/// <summary>账号页（导航页）。密码框通过事件推给 ViewModel。</summary>
public partial class AccountsPage : Page
{
    public AccountsPage()
    {
        InitializeComponent();
        DataContext = App.Shell?.Accounts;
    }

    private void PasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.AccountsViewModel viewModel && sender is PasswordBox passwordBox)
        {
            viewModel.LittleSkinPassword = passwordBox.Password;
        }
    }
}
