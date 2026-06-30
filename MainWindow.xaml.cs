using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using Poe2PriceGui.Services;
using Poe2PriceGui.ViewModels;

namespace Poe2PriceGui;

public partial class MainWindow : Window
{
    private GlobalHotkeyService? _globalHotkeyService;

    public MainWindow()
    {
        InitializeComponent();
        var viewModel = new MainViewModel();
        DataContext = viewModel;

        _globalHotkeyService = new GlobalHotkeyService(this, hotkeyId: 1);
        _globalHotkeyService.HotkeyPressed += async (_, _) => await viewModel.RunPriceCheckerAsync();
        viewModel.PriceCheckerSettingsChanged += (_, _) => UpdateHotkeyRegistration(viewModel);

        // 窗口句柄准备好后尝试注册一次热键。
        SourceInitialized += (_, _) => UpdateHotkeyRegistration(viewModel);
    }

    private void UpdateHotkeyRegistration(MainViewModel viewModel)
    {
        if (_globalHotkeyService == null) return;

        if (viewModel.PriceCheckerEnabled)
        {
            if (!_globalHotkeyService.Register(viewModel.PriceCheckerHotkey))
            {
                AppLogger.Instance.Warn($"注册查价器热键失败：{viewModel.PriceCheckerHotkey}");
            }
        }
        else
        {
            _globalHotkeyService.Unregister();
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "打开链接失败");
        }
        e.Handled = true;
    }

    private void OpenQQGroup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://qm.qq.com/q/2OGbgG0z6w") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "打开 QQ 群链接失败");
            MessageBox.Show("打开链接失败，请手动访问：https://qm.qq.com/q/2OGbgG0z6w", "提示");
        }
    }

    private void CopyQQGroupNumber_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        var originalContent = btn.Content;

        try
        {
            // 不保留模式：不阻塞、不需独占剪贴板，几乎不会失败
            Clipboard.SetDataObject("1001850913", false);
            btn.Content = "✓ 已复制";
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Error(ex, "复制 QQ 群号失败");
            btn.Content = "✗ 失败，请手输";
        }

        // 1.5 秒后恢复按钮文字（异步，不阻塞 UI）
        Task.Delay(1500).ContinueWith(_ =>
        {
            Dispatcher.Invoke(() => btn.Content = originalContent);
        });
    }
}
