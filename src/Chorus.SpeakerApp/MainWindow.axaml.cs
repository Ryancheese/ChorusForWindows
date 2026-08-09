using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Chorus.SpeakerApp.ViewModels;

namespace Chorus.SpeakerApp;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _orbTimer;
    private double _orbPhase;
    private double _bgPhase;
    private double _levelSmooth;

    private const double BlobCyanBase = 320;
    private const double BlobMintBase = 280;
    private const double BlobMistBase = 260;

    public MainWindow()
    {
        InitializeComponent();

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

    private SpeakerViewModel? Vm => DataContext as SpeakerViewModel;

    private void Toggle_Click(object? sender, RoutedEventArgs e) => Vm?.ToggleBroadcast();

    private void Ambient_Tick(object? sender, EventArgs e)
    {
        double level = Vm?.TakeAudioLevel() ?? 0;
        _levelSmooth = _levelSmooth * 0.72 + level * 0.28;

        TickOrbGlow(_levelSmooth);
        _bgPhase += (0.033 / 8.0) * (1 + _levelSmooth * 1.8);
        if (_bgPhase > 1) _bgPhase -= 1;
        UpdateLiquidGlass(_bgPhase * Math.PI * 2, _levelSmooth);
    }

    private void TickOrbGlow(double level)
    {
        bool live = Vm?.IsLive == true;
        double period = (live ? 1.7 : 2.3) / (1 + level * 1.4);
        _orbPhase += 0.033 / period;
        if (_orbPhase > 1) _orbPhase -= 1;

        double t = 0.5 - 0.5 * Math.Cos(_orbPhase * Math.PI * 2);
        double size = (live ? 108 + 22 * t : 100 + 14 * t) + 36 * level;
        double opacity = (live ? 0.5 + 0.45 * t : 0.32 + 0.45 * t) * (0.85 + 0.45 * level);

        if (OrbGlow != null)
        {
            OrbGlow.Width = size;
            OrbGlow.Height = size;
            OrbGlow.Opacity = Math.Clamp(opacity, 0, 1);
        }

        if (OrbGlowRipple != null)
        {
            double t2 = 0.5 - 0.5 * Math.Cos((_orbPhase + 0.35) * Math.PI * 2);
            OrbGlowRipple.Width = 96 + 34 * t2 + 42 * level;
            OrbGlowRipple.Height = 96 + 34 * t2 + 42 * level;
            OrbGlowRipple.Opacity = Math.Clamp(
                (live ? 0.18 + 0.4 * t2 : 0.05 + 0.18 * t2) + 0.35 * level,
                0, 1);
        }
    }

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
        double cy = h * 0.38;
        double amp = 1 + level * 2.2;
        double breath = 1 + level * 0.42;

        PlaceBlob(
            GlassBlobCyan,
            cx - 70 + (16 * amp) * Math.Sin(phase),
            cy - 110 + (10 * amp) * Math.Cos(phase * 0.8),
            BlobCyanBase * breath,
            Math.Clamp(0.42 + level * 0.4, 0, 0.95));
        PlaceBlob(
            GlassBlobMint,
            cx + 90 + (12 * amp) * Math.Cos(phase * 1.1),
            cy + 50 + (14 * amp) * Math.Sin(phase * 0.7),
            BlobMintBase * breath,
            Math.Clamp(0.36 + level * 0.38, 0, 0.9));
        PlaceBlob(
            GlassBlobMist,
            cx + 10 + (8 * level) * Math.Cos(phase * 0.9),
            cy + 140 + (10 * amp) * Math.Sin(phase * 1.3),
            BlobMistBase * (1 + level * 0.28),
            Math.Clamp(0.5 + level * 0.25, 0, 0.92));
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
}
