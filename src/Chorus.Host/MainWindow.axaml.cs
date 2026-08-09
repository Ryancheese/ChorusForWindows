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
    private double _levelSmooth;

    private const double BlobCyanBase = 420;
    private const double BlobMintBase = 360;
    private const double BlobMistBase = 340;

    public MainWindow()
    {
        InitializeComponent();
        L10n.LoadPreference();

        _orbTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _orbTimer.Tick += Ambient_Tick;
        Opened += (_, _) =>
        {
            UpdateLiquidGlass(0, 0);
            _orbTimer.Start();
        };
        Closed += (_, _) => _orbTimer.Stop();
        SizeChanged += (_, _) => UpdateLiquidGlass(_bgPhase * Math.PI * 2, _levelSmooth);
    }

    private HostViewModel? ViewModel => DataContext as HostViewModel;

    private void Ambient_Tick(object? sender, EventArgs e)
    {
        double level = ViewModel?.TakeAudioLevel() ?? 0;
        // Soft UI envelope so beats feel like Apple Music ambient, not strobing.
        _levelSmooth = _levelSmooth * 0.72 + level * 0.28;

        TickOrbGlow(_levelSmooth);
        // Drift faster when loud (still gentle).
        _bgPhase += (0.033 / 8.0) * (1 + _levelSmooth * 1.8);
        if (_bgPhase > 1) _bgPhase -= 1;
        UpdateLiquidGlass(_bgPhase * Math.PI * 2, _levelSmooth);
    }

    private void TickOrbGlow(double level)
    {
        bool live = ViewModel?.IsLive == true;
        double period = (live ? 1.8 : 2.2) / (1 + level * 1.4);
        _orbPhase += 0.033 / period;
        if (_orbPhase > 1) _orbPhase -= 1;

        double t = 0.5 - 0.5 * Math.Cos(_orbPhase * Math.PI * 2);
        double size = (live ? 148 + 28 * t : 148 + 16 * t) + 48 * level;
        double opacity = (live ? 0.45 + 0.55 * t : 0.35 + 0.55 * t) * (0.85 + 0.45 * level);

        if (OrbGlow != null)
        {
            OrbGlow.Width = size;
            OrbGlow.Height = size;
            OrbGlow.Opacity = Math.Clamp(opacity, 0, 1);
        }

        if (OrbGlowRipple != null)
        {
            double t2 = 0.5 - 0.5 * Math.Cos((_orbPhase + 0.35) * Math.PI * 2);
            OrbGlowRipple.Width = 140 + 40 * t2 + 56 * level;
            OrbGlowRipple.Height = 140 + 40 * t2 + 56 * level;
            OrbGlowRipple.Opacity = Math.Clamp(
                (live ? 0.15 + 0.45 * t2 : 0.05 + 0.2 * t2) + 0.35 * level,
                0, 1);
        }
    }

    /// <summary>Port of Mac <c>LiquidGlassBackground</c> with audio-reactive drift.</summary>
    private void UpdateLiquidGlass(double phase, double level)
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
        double cy = h * 0.42;
        double amp = 1 + level * 2.2;
        double breath = 1 + level * 0.42;

        PlaceBlob(
            GlassBlobCyan,
            cx - 90 + (18 * amp) * Math.Sin(phase),
            cy - 160 + (12 * amp) * Math.Cos(phase * 0.8),
            BlobCyanBase * breath,
            Math.Clamp(0.45 + level * 0.4, 0, 0.95));
        PlaceBlob(
            GlassBlobMint,
            cx + 120 + (14 * amp) * Math.Cos(phase * 1.1),
            cy + 40 + (16 * amp) * Math.Sin(phase * 0.7),
            BlobMintBase * breath,
            Math.Clamp(0.38 + level * 0.38, 0, 0.9));
        PlaceBlob(
            GlassBlobMist,
            cx + 20 + (8 * level) * Math.Cos(phase * 0.9),
            cy + 120 + (10 * amp) * Math.Sin(phase * 1.3),
            BlobMistBase * (1 + level * 0.28),
            Math.Clamp(0.55 + level * 0.25, 0, 0.92));
    }

    private static void PlaceBlob(
        Avalonia.Controls.Shapes.Ellipse? blob,
        double centerX,
        double centerY,
        double size,
        double opacity)
    {
        if (blob == null) return;
        blob.Width = size;
        blob.Height = size;
        blob.Opacity = opacity;
        Canvas.SetLeft(blob, centerX - size * 0.5);
        Canvas.SetTop(blob, centerY - size * 0.5);
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

    public void ConnectAll_Click(object? sender, RoutedEventArgs e) => ViewModel?.ConnectAllPeers();

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
        e.Handled = true;
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
