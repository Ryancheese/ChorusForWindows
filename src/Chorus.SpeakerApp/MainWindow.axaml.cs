using Avalonia.Controls;
using Avalonia.Interactivity;
using Chorus.SpeakerApp.ViewModels;

namespace Chorus.SpeakerApp;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private SpeakerViewModel? Vm => DataContext as SpeakerViewModel;

    private void Toggle_Click(object? sender, RoutedEventArgs e) => Vm?.ToggleBroadcast();
}
