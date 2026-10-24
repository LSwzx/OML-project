using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace OML.Desktop;

/// <summary>主窗口。视图逻辑只有自动滚动，其余全在 ViewModel。</summary>
public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>日志追加后滚到底部，否则用户永远看到第一屏。</summary>
    private void LogBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox)
        {
            textBox.ScrollToEnd();
        }
    }
}
