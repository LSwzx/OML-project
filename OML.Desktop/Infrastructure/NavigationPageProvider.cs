using System.Windows;
using Wpf.Ui.Abstractions;

namespace OML.Desktop.Infrastructure;

/// <summary>
/// 导航页工厂。
/// <para>
/// WPF-UI 的 NavigationView 默认不带 DI 容器，需要显式注入页面提供者，
/// 否则 <c>Navigate(Type)</c> 只会更新选中态而不创建页面（表现为内容区空白）。
/// 页面都是无参构造，这里直接 Activator。
/// </para>
/// </summary>
public sealed class NavigationPageProvider : INavigationViewPageProvider
{
    public object? GetPage(Type pageType)
    {
        ArgumentNullException.ThrowIfNull(pageType);

        if (!typeof(FrameworkElement).IsAssignableFrom(pageType))
        {
            throw new ArgumentException($"页面类型必须继承 FrameworkElement：{pageType.FullName}", nameof(pageType));
        }

        return Activator.CreateInstance(pageType);
    }
}
