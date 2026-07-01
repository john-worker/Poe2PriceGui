using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Poe2PriceGui.Services;

namespace Poe2PriceGui.Windows;

/// <summary>
/// 内置浏览器窗口：用户登录国服/国际服市集后，手动点击按钮获取 POESESSID Cookie。
/// </summary>
public partial class LoginBrowserWindow : Window
{
    public string? CapturedPoeSessionId { get; private set; }

    private readonly string? _existingSessionId;
    private readonly bool _isChina;

    /// <param name="existingSessionId">已保存的 POESESSID，打开时注入以恢复登录态。</param>
    /// <param name="isChina">是否为国服，决定登录地址与 Cookie 域。</param>
    public LoginBrowserWindow(string? existingSessionId = null, bool isChina = true)
    {
        InitializeComponent();
        _existingSessionId = existingSessionId;
        _isChina = isChina;
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
                var domain = _isChina ? ".poe.game.qq.com" : ".pathofexile.com";
                var cookie = WebView.CoreWebView2.CookieManager.CreateCookie(
                    "POESESSID", _existingSessionId, domain, "/");
                WebView.CoreWebView2.CookieManager.AddOrUpdateCookie(cookie);
                AppLogger.Instance.Info($"已注入上次保存的 POESESSID（{_isChinaSwitchText}），尝试恢复登录态");
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

        // 导航完成后启用确认按钮，不自动检测 POESESSID。
        WebView.CoreWebView2.NavigationCompleted += (_, args) =>
        {
            if (!args.IsSuccess) return;

            Dispatcher.Invoke(() =>
            {
                ConfirmLoginButton.IsEnabled = true;
                StatusText.Text = "登录后请点击下方按钮获取 POESESSID";
                StatusText.Foreground = System.Windows.Media.Brushes.DodgerBlue;
            });
        };

        var loginUrl = _isChina ? "https://poe.game.qq.com/trade2" : "https://www.pathofexile.com/trade2";
        WebView.CoreWebView2.Navigate(loginUrl);
    }

    private string _isChinaSwitchText => _isChina ? "国服" : "国际服";

    /// <summary>
    /// 手动按钮：用户确认已登录后主动检测 POESESSID。
    /// </summary>
    private async void ConfirmLoginButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cookieUrl = _isChina ? "https://poe.game.qq.com" : "https://www.pathofexile.com";
            var cookies = await WebView.CoreWebView2.CookieManager.GetCookiesAsync(cookieUrl);
            var sessionCookie = cookies.FirstOrDefault(c => c.Name == "POESESSID");

            if (sessionCookie == null || string.IsNullOrWhiteSpace(sessionCookie.Value))
            {
                StatusText.Text = "未检测到 POESESSID，请先登录";
                StatusText.Foreground = System.Windows.Media.Brushes.Crimson;
                return;
            }

            CapturedPoeSessionId = sessionCookie.Value;
            AppLogger.Instance.Info($"成功获取 POESESSID（{_isChinaSwitchText}，来源：手动点击）");

            StatusText.Text = "登录成功，正在关闭...";
            StatusText.Foreground = System.Windows.Media.Brushes.DarkGreen;

            await Task.Delay(800);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "读取 POESESSID 失败");
            StatusText.Text = $"读取失败：{ex.Message}";
            StatusText.Foreground = System.Windows.Media.Brushes.Crimson;
        }
    }
}
