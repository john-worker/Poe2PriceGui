using System.Windows;
using System.Windows.Input;
using Poe2PriceGui.ViewModels;

namespace Poe2PriceGui.Windows;

/// <summary>
/// 查价器叠加层窗口。居中显示，ESC 或 X 按钮关闭，不自动关闭。
/// </summary>
public partial class PriceOverlayWindow : Window
{
    public PriceOverlayWindow(PriceOverlayViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // 注入关闭回调，让 ViewModel 的 CloseCommand 可以关闭窗口。
        viewModel.CloseAction = Close;

        KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }
}
