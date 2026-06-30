using System.Diagnostics;
using System.Windows;
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
}
