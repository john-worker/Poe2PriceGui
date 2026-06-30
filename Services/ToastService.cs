using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 全局 Toast 通知服务：在右上角弹出圆角提示框，5 秒后自动消失，新提示会往下挤旧提示。
/// </summary>
public class ToastService
{
    private readonly ObservableCollection<ToastNotification> _toasts = [];
    private readonly TimeSpan _displayDuration = TimeSpan.FromSeconds(5);

    public ObservableCollection<ToastNotification> Toasts => _toasts;

    /// <summary>
    /// 显示一条普通信息提示。
    /// </summary>
    public void ShowInfo(string message)
    {
        Show(message, Brushes.DodgerBlue, Brushes.White);
    }

    /// <summary>
    /// 显示一条成功提示。
    /// </summary>
    public void ShowSuccess(string message)
    {
        Show(message, Brushes.ForestGreen, Brushes.White);
    }

    /// <summary>
    /// 显示一条警告提示。
    /// </summary>
    public void ShowWarning(string message)
    {
        Show(message, Brushes.DarkOrange, Brushes.White);
    }

    /// <summary>
    /// 显示一条错误提示。
    /// </summary>
    public void ShowError(string message)
    {
        Show(message, Brushes.Crimson, Brushes.White);
    }

    public void Show(string message, Brush background, Brush foreground)
    {
        var toast = new ToastNotification
        {
            Message = message,
            Background = background,
            Foreground = foreground,
        };

        // 插入到最前面，新提示在上，旧提示被往下挤。
        _toasts.Insert(0, toast);

        // 5 秒后自动移除。
        _ = Task.Run(async () =>
        {
            await Task.Delay(_displayDuration);
            await Application.Current.Dispatcher.InvokeAsync(() => _toasts.Remove(toast));
        });
    }
}
