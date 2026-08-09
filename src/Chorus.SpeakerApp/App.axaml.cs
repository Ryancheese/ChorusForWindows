using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Chorus.SpeakerApp.ViewModels;

namespace Chorus.SpeakerApp;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new SpeakerViewModel();
            var window = new MainWindow { DataContext = vm };
            window.Icon = LoadAppIcon();
            desktop.MainWindow = window;
            window.Closing += (_, _) => vm.Dispose();
            desktop.ShutdownRequested += (_, _) =>
            {
                vm.Dispose();
                System.Threading.Thread.Sleep(200);
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static WindowIcon? LoadAppIcon()
    {
        try
        {
            var uri = new Uri("avares://Chorus.SpeakerApp/Assets/chorus-speaker.png");
            using var stream = AssetLoader.Open(uri);
            return new WindowIcon(new Bitmap(stream));
        }
        catch
        {
            return null;
        }
    }
}
