using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Chorus.Host.Localization;
using Chorus.Host.ViewModels;
using ChorusCore.Audio;
using ChorusCore.Protocol;

namespace Chorus.Host;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _orbTimer;
    private double _orbPhase;
    private double _bgPhase;

    public MainWindow()
    {
        InitializeComponent();
        L10n.LoadPreference();

        _orbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _orbTimer.Tick += Ambient_Tick;
        Opened += (_, _) =>
        {
            UpdateLiquidGlass(0);
            _orbTimer.Start();
        };
        Closed += (_, _) => _orbTimer.Stop();
        SizeChanged += (_, _) => UpdateLiquidGlass(_bgPhase * Math.PI * 2);
    }

    private HostViewModel? ViewModel => DataContext as HostViewModel;

    private void Ambient_Tick(object? sender, EventArgs e)
    {
        TickOrbGlow();
        // Mac LiquidGlassBackground: 8s ease-in-out drift
        _bgPhase += 0.033 / 8.0;
        if (_bgPhase > 1) _bgPhase -= 1;
        UpdateLiquidGlass(_bgPhase * Math.PI * 2);
    }

    private void TickOrbGlow()
    {
        bool live = ViewModel?.IsLive == true;
        double period = live ? 1.8 : 2.2;
        _orbPhase += 0.033 / period;
        if (_orbPhase > 1) _orbPhase -= 1;

        double t = 0.5 - 0.5 * Math.Cos(_orbPhase * Math.PI * 2);
        double size = live ? 148 + 28 * t : 148 + 16 * t;
        double opacity = live ? 0.45 + 0.55 * t : 0.35 + 0.55 * t;

        if (OrbGlow != null)
        {
            OrbGlow.Width = size;
            OrbGlow.Height = size;
            OrbGlow.Opacity = opacity;
        }

        if (OrbGlowRipple != null)
        {
            double t2 = 0.5 - 0.5 * Math.Cos((_orbPhase + 0.35) * Math.PI * 2);
            OrbGlowRipple.Width = 140 + 40 * t2;
            OrbGlowRipple.Height = 140 + 40 * t2;
            OrbGlowRipple.Opacity = live ? 0.15 + 0.45 * t2 : 0.05 + 0.2 * t2;
        }
    }

    /// <summary>Port of Mac <c>LiquidGlassBackground</c> blob offsets.</summary>
    private void UpdateLiquidGlass(double phase)
    {
        if (LiquidGlassLayer == null) return;
        double w = LiquidGlassLayer.Bounds.Width;
        double h = LiquidGlassLayer.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            w = Bounds.Width;
            h = Bounds.Height;
        }
        if (w <= 0 || h <= 0) return;

        double cx = w * 0.5;
        double cy = h * 0.42; // Mac glow sits upper-center behind content

        PlaceBlob(GlassBlobCyan, cx - 90 + 18 * Math.Sin(phase), cy - 160 + 12 * Math.Cos(phase * 0.8));
        PlaceBlob(GlassBlobMint, cx + 120 + 14 * Math.Cos(phase * 1.1), cy + 40 + 16 * Math.Sin(phase * 0.7));
        // Soft mist / "white" highlight — lower-center, gently drifting
        PlaceBlob(GlassBlobMist, cx + 20, cy + 120 + 10 * Math.Sin(phase * 1.3));
    }

    private static void PlaceBlob(Avalonia.Controls.Shapes.Ellipse? blob, double centerX, double centerY)
    {
        if (blob == null) return;
        Canvas.SetLeft(blob, centerX - blob.Width * 0.5);
        Canvas.SetTop(blob, centerY - blob.Height * 0.5);
    }

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

    public void LanguageMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string code })
            ViewModel?.SetLanguage(code);
    }

    public void AppearanceMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string mode } || ViewModel == null) return;
        ViewModel.SetAppearanceMode(mode);
        var app = Application.Current;
        if (app == null) return;
        app.RequestedThemeVariant = mode switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        ViewModel.IsDark = app.ActualThemeVariant == ThemeVariant.Dark
            || (mode == "dark")
            || (mode == "system" && app.ActualThemeVariant != ThemeVariant.Light);
    }

    public void PeerConnect_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is PeerRowViewModel row)
            ViewModel?.ConnectToPeerRow(row);
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
            Title = L10n.T("dialog.choose.audio"),
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(L10n.T("dialog.audio.files"))
                {
                    Patterns = new[] { "*.mp3", "*.wav", "*.m4a", "*.flac", "*.aac", "*.ogg", "*.wma", "*.aiff", "*.aif" }
                },
                new FilePickerFileType(L10n.T("dialog.all.files")) { Patterns = new[] { "*.*" } }
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
            Title = L10n.T("dialog.choose.folder"),
            AllowMultiple = false
        });
        if (folders == null || folders.Count == 0) return;
        try { ViewModel.AddFolderToPlaylist(folders[0].Path.LocalPath); } catch { }
    }
}
