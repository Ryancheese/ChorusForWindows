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

    public MainWindow()
    {
        InitializeComponent();

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

    private SpeakerViewModel? Vm => DataContext as SpeakerViewModel;

    private void Toggle_Click(object? sender, RoutedEventArgs e) => Vm?.ToggleBroadcast();

    private void Ambient_Tick(object? sender, EventArgs e)
    {
        TickOrbGlow();
        _bgPhase += 0.033 / 8.0;
        if (_bgPhase > 1) _bgPhase -= 1;
        UpdateLiquidGlass(_bgPhase * Math.PI * 2);
    }

    private void TickOrbGlow()
    {
        bool live = Vm?.IsLive == true;
        double period = live ? 1.7 : 2.3;
        _orbPhase += 0.033 / period;
        if (_orbPhase > 1) _orbPhase -= 1;

        double t = 0.5 - 0.5 * Math.Cos(_orbPhase * Math.PI * 2);
        double size = live ? 108 + 22 * t : 100 + 14 * t;
        double opacity = live ? 0.5 + 0.45 * t : 0.32 + 0.45 * t;

        if (OrbGlow != null)
        {
            OrbGlow.Width = size;
            OrbGlow.Height = size;
            OrbGlow.Opacity = opacity;
        }

        if (OrbGlowRipple != null)
        {
            double t2 = 0.5 - 0.5 * Math.Cos((_orbPhase + 0.35) * Math.PI * 2);
            OrbGlowRipple.Width = 96 + 34 * t2;
            OrbGlowRipple.Height = 96 + 34 * t2;
            OrbGlowRipple.Opacity = live ? 0.18 + 0.4 * t2 : 0.05 + 0.18 * t2;
        }
    }

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
        double cy = h * 0.38;

        PlaceBlob(GlassBlobCyan, cx - 70 + 16 * Math.Sin(phase), cy - 110 + 10 * Math.Cos(phase * 0.8));
        PlaceBlob(GlassBlobMint, cx + 90 + 12 * Math.Cos(phase * 1.1), cy + 50 + 14 * Math.Sin(phase * 0.7));
        PlaceBlob(GlassBlobMist, cx + 10, cy + 140 + 10 * Math.Sin(phase * 1.3));
    }

    private static void PlaceBlob(Avalonia.Controls.Shapes.Ellipse? blob, double centerX, double centerY)
    {
        if (blob == null) return;
        Canvas.SetLeft(blob, centerX - blob.Width * 0.5);
        Canvas.SetTop(blob, centerY - blob.Height * 0.5);
    }
}
