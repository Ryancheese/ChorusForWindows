using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Chorus.Host.ViewModels;

namespace Chorus.Host;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new HostViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Icon = LoadAppIcon();
            desktop.MainWindow = window;

            // 窗口关闭时清理（双重保险）
            window.Closing += (s, e) => vm.Dispose();

            // 应用退出时清理 + 给 Dispose 时间完成
            desktop.ShutdownRequested += (s, e) =>
            {
                vm.Dispose();
                System.Threading.Thread.Sleep(300); // 等 WASAPI/网络线程清理
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static WindowIcon? LoadAppIcon()
    {
        try
        {
            var uri = new Uri("avares://Chorus.Host/Assets/chorus-host.png");
            using var stream = AssetLoader.Open(uri);
            return new WindowIcon(new Bitmap(stream));
        }
        catch
        {
            return null;
        }
    }
}
