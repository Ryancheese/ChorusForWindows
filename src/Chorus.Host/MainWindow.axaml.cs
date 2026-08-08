using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Chorus.Host.ViewModels;
using ChorusCore.Audio;
using ChorusCore.Network.Mdns;
using ChorusCore.Protocol;

namespace Chorus.Host;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private HostViewModel? ViewModel => DataContext as HostViewModel;

    public void PlayDemoTone_Click(object? sender, RoutedEventArgs e) => ViewModel?.PlayDemoTone();
    public void PlaySystemAudio_Click(object? sender, RoutedEventArgs e) => ViewModel?.ToggleSystemAudio();
    public void Stop_Click(object? sender, RoutedEventArgs e) => ViewModel?.Stop();
    public void PauseResume_Click(object? sender, RoutedEventArgs e) => ViewModel?.PauseResume();
    public void PlayCurrent_Click(object? sender, RoutedEventArgs e) => ViewModel?.PlayCurrent();
    public void PlayNext_Click(object? sender, RoutedEventArgs e) => ViewModel?.PlayNext();
    public void PlayPrev_Click(object? sender, RoutedEventArgs e) => ViewModel?.PlayPrevious();
    public void ClearPlaylist_Click(object? sender, RoutedEventArgs e) => ViewModel?.ClearPlaylist();
    public void ManualConnect_Click(object? sender, RoutedEventArgs e) => ViewModel?.ManualConnectOrDisconnect();

    public void Help_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.HelpVisible = true;
    }

    public void CloseHelp_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel != null) ViewModel.HelpVisible = false;
    }

    public void Appearance_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        ViewModel.IsDark = !ViewModel.IsDark;
        var app = Application.Current;
        if (app != null)
            app.RequestedThemeVariant = ViewModel.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        Background = ViewModel.IsDark
            ? Avalonia.Media.Brush.Parse("#0A0F1A")
            : Avalonia.Media.Brush.Parse("#E8F0F8");
    }

    public void PeerConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DiscoveredPeer peer)
            ViewModel?.ConnectToPeer(peer);
    }

    public void RemoveSpeaker_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is DeviceInfo speaker)
            ViewModel?.RemoveSpeaker(speaker);
    }

    public void PlaylistPlay_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PlaylistItem item)
            ViewModel?.PlayPlaylistTrack(item.Id);
    }

    public void PlaylistRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PlaylistItem item)
            ViewModel?.RemoveFromPlaylist(item.Id);
    }

    public async void SelectAudio_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择音频文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("音频文件")
                {
                    Patterns = new[] { "*.mp3", "*.wav", "*.m4a", "*.flac", "*.aac", "*.ogg", "*.wma", "*.aiff", "*.aif" }
                },
                new FilePickerFileType("所有文件") { Patterns = new[] { "*.*" } }
            }
        });
        if (files == null || files.Count == 0) return;
        var paths = new List<string>();
        foreach (var f in files)
        {
            try { paths.Add(f.Path.LocalPath); } catch { }
        }
        ViewModel.AddFilesToPlaylist(paths);
    }

    public async void SelectFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择音乐文件夹",
            AllowMultiple = false
        });
        if (folders == null || folders.Count == 0) return;
        try { ViewModel.AddFolderToPlaylist(folders[0].Path.LocalPath); } catch { }
    }
}
