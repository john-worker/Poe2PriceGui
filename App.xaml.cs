using System.Windows;
using Velopack;

namespace Poe2PriceGui;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Velopack 必须在应用启动最早期初始化，处理安装/更新钩子。
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
