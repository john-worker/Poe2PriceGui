using System.IO;
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

    private readonly string? _existingSessionId;
    private bool _isClosing;

    /// <param name="existingSessionId">已保存的 POESESSID，打开时注入以恢复登录态。</param>
    public LoginBrowserWindow(string? existingSessionId = null)
    {
        InitializeComponent();
        _existingSessionId = existingSessionId;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 使用固定的持久化目录存储 WebView2 用户数据（含 Cookie），
            // 避免默认位置随工作目录变化或被 Velopack 更新覆盖，导致登录态丢失。
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Poe2PriceGui", "WebView2Data");
            Directory.CreateDirectory(userDataFolder);
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await WebView.EnsureCoreWebView2Async(env);

            // POESESSID 是 Session Cookie（无 Expires），不会写入磁盘持久化。
            // 每次打开浏览器时，从 settings.json 读取已保存的值并注入，恢复登录态。
            if (!string.IsNullOrWhiteSpace(_existingSessionId))
            {
                var cookie = WebView.CoreWebView2.CookieManager.CreateCookie(
                    "POESESSID", _existingSessionId, ".poe.game.qq.com", "/");
                WebView.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
                AppLogger.Instance.Info("已注入上次保存的 POESESSID，尝试恢复登录态");
            }
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
            if (!args.IsSuccess || _isClosing) return;

            var currentUrl = WebView.CoreWebView2.Source ?? "";

            if (firstNavigation)
            {
                // 首次导航完成：不检测，仅更新 UI。
                firstNavigation = false;
                Dispatcher.Invoke(() =>
                {
                    ConfirmLoginButton.IsEnabled = true;
                    StatusText.Text = "请登录国服市集账号";
                    StatusText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
                });
                return;
            }

            if (_isClosing) return;

            // 后续导航：只在回到 poe.game.qq.com 时才检测（排除 QQ 登录页等中间跳转）。
            // 等待 2 秒让 Cookie 更新完成后再读取，避免拿到旧值。
            if (currentUrl.Contains("poe.game.qq.com"))
            {
                await Task.Delay(2000);
                if (!_isClosing)
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
        if (_isClosing) return;

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

            var sessionId = sessionCookie.Value;

            // 自动检测时：如果读到的 POESESSID 和注入的旧值完全相同，
            // 说明可能是注入的旧 cookie（不确定是否仍然有效），不自动关闭，
            // 让用户确认登录状态或重新登录获取新 cookie。
            if (isAutoCheck && !string.IsNullOrWhiteSpace(_existingSessionId) && sessionId == _existingSessionId)
            {
                Dispatcher.Invoke(() =>
                {
                    ConfirmLoginButton.IsEnabled = true;
                    StatusText.Text = "已恢复上次登录，请确认是否有效；如已过期请重新登录";
                    StatusText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
                });
                return;
            }

            CapturedPoeSessionId = sessionId;
            _isClosing = true;
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
