using System.Windows;
using Microsoft.Web.WebView2.Core;
using Poe2PriceGui.Services;

namespace Poe2PriceGui.Windows;

/// <summary>
/// 内置浏览器窗口：用户登录国服市集后，自动检测 POESESSID Cookie 并关闭。
/// </summary>
public partial class LoginBrowserWindow : Window
{
    public string? CapturedPoeSessionId { get; private set; }

    public LoginBrowserWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "初始化 WebView2 失败");
            MessageBox.Show(
                $"无法初始化内置浏览器，请确保系统已安装 WebView2 运行时。\n{ex.Message}",
                "错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
            return;
        }

        var firstNavigation = true;
        WebView.CoreWebView2.NavigationCompleted += async (_, args) =>
        {
            if (!args.IsSuccess) return;

            var currentUrl = WebView.CoreWebView2.Source ?? "";

            if (firstNavigation)
            {
                // 首次导航：仅为加载页面，Cookie 可能为上次缓存的旧值，不检测。
                firstNavigation = false;
                Dispatcher.Invoke(() =>
                {
                    ConfirmLoginButton.IsEnabled = true;
                    StatusText.Text = "请登录国服市集账号";
                    StatusText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
                });
                return;
            }

            // 后续导航：只在回到 poe.game.qq.com 时才检测（排除 QQ 登录页等中间跳转）。
            // 等待 2 秒让 Cookie 更新完成后再读取，避免拿到旧值。
            if (currentUrl.Contains("poe.game.qq.com"))
            {
                await Task.Delay(2000);
                await TryCaptureSessionIdAsync(isAutoCheck: true);
            }
        };

        WebView.CoreWebView2.Navigate("https://poe.game.qq.com/trade2");
    }

    /// <summary>
    /// 尝试读取 POESESSID Cookie。自动检测模式下检测到则自动关闭窗口。
    /// </summary>
    private async Task TryCaptureSessionIdAsync(bool isAutoCheck)
    {
        try
        {
            var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync("https://poe.game.qq.com");
            var sessionCookie = cookies.FirstOrDefault(c => c.Name == "POESESSID");

            if (sessionCookie == null || string.IsNullOrWhiteSpace(sessionCookie.Value))
            {
                Dispatcher.Invoke(() =>
                {
                    ConfirmLoginButton.IsEnabled = true;
                    StatusText.Text = isAutoCheck ? "请登录国服市集账号" : "未检测到 POESESSID，请先登录";
                    StatusText.Foreground = isAutoCheck
                        ? System.Windows.Media.Brushes.DodgerBlue
                        : System.Windows.Media.Brushes.Crimson;
                });
                return;
            }

            CapturedPoeSessionId = sessionCookie.Value;
            AppLogger.Instance.Info($"成功获取 POESESSID（来源：{(isAutoCheck ? "自动检测" : "手动点击")}）");

            Dispatcher.Invoke(() =>
            {
                StatusText.Text = "登录成功，正在关闭...";
                StatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;
            });

            await Task.Delay(800);
            Dispatcher.Invoke(() =>
            {
                DialogResult = true;
                Close();
            });
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "读取 POESESSID 失败");
            Dispatcher.Invoke(() =>
            {
                StatusText.Text = $"读取失败：{ex.Message}";
                StatusText.Foreground = System.Windows.Media.Brushes.Crimson;
            });
        }
    }

    /// <summary>
    /// 手动按钮：用户确认已登录后主动检测。
    /// </summary>
    private async void ConfirmLoginButton_Click(object sender, RoutedEventArgs e)
    {
        await TryCaptureSessionIdAsync(isAutoCheck: false);
    }
}
