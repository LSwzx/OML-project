using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OML.Desktop.ViewModels;

namespace OML.Desktop.Views.Pages;

/// <summary>主页：Java/基岩平台卡片、版本切换、账号与启动。</summary>
public partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        DataContext = App.Shell?.Home;
    }

    private HomeViewModel? ViewModel => DataContext as HomeViewModel;

    private void OnJavaCardClick(object sender, MouseButtonEventArgs e) =>
        ViewModel?.SelectJavaCommand.Execute(null);

    private void OnBedrockCardClick(object sender, MouseButtonEventArgs e) =>
        ViewModel?.SelectBedrockCommand.Execute(null);

    private void OnVersionPicked(object sender, SelectionChangedEventArgs e)
    {
        if (ViewModel is { } viewModel && viewModel.SelectedVersion is not null)
        {
            viewModel.IsVersionPickerOpen = false;
        }
    }

    private void OnVersionSettings(object sender, RoutedEventArgs e) =>
        App.Shell?.RequestNavigation("settings");

    private void OnModsClick(object sender, RoutedEventArgs e) =>
        App.Shell?.RequestNavigation("mods");

    private void OnAdvancedSettings(object sender, RoutedEventArgs e) =>
        App.Shell?.RequestNavigation("settings");

    private void OnAccountCardClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as UIElement };

        var manage = new MenuItem { Header = "切换账号…" };
        manage.Click += (_, _) => App.Shell?.RequestNavigation("accounts");

        var skin = new MenuItem { Header = "皮肤设置…" };
        skin.Click += (_, _) => App.Shell?.RequestNavigation("skin");

        menu.Items.Add(manage);
        menu.Items.Add(skin);
        menu.IsOpen = true;
    }

    private void LogBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }
}
