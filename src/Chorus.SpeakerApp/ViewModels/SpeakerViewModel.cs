using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Chorus.Speaker;

namespace Chorus.SpeakerApp.ViewModels;

public sealed class SpeakerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly SpeakerSession _session;
    private bool _disposed;

    public SpeakerViewModel()
    {
        _session = new SpeakerSession();
        _session.StateChanged += OnSessionChanged;
        RefreshFromSession();
    }

    public string Title => "Chorus Speaker";
    public string Subtitle => "扬声器";
    public string Hint => "与 Host 同一 Wi‑Fi。请勿在本机同时运行 Chorus Host（会占用 17482 端口）。";

    public string Status { get; private set; } = "未广播";
    public string PhaseLabel { get; private set; } = "空闲";
    public string? LocalAddress { get; private set; }
    public string? HostName { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool IsAdvertising { get; private set; }
    public bool IsClockCalibrated { get; private set; }
    public bool IsPlaying { get; private set; }
    /// <summary>Orb pulse intensifies while advertising or playing.</summary>
    public bool IsLive => IsAdvertising || IsPlaying;
    public bool HasHost => !string.IsNullOrEmpty(HostName);
    public bool HasLocalAddress => !string.IsNullOrEmpty(LocalAddress);
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
    public string PrimaryActionLabel => IsAdvertising ? "停止广播" : "开始广播";
    public string ClockLabel => IsClockCalibrated ? "已校准" : "未校准";

    public void ToggleBroadcast()
    {
        if (IsAdvertising)
        {
            _session.StopAdvertising();
            return;
        }

        // Error / retry path: always tear down before rebinding 17482.
        if (PhaseLabel == "错误")
            _session.StopAdvertising();
        _session.StartAdvertising();
    }

    private void OnSessionChanged()
    {
        Dispatcher.UIThread.Post(RefreshFromSession);
    }

    private void RefreshFromSession()
    {
        Status = _session.Status;
        LocalAddress = _session.LocalAddress;
        HostName = _session.HostName;
        ErrorMessage = _session.ErrorMessage;
        IsAdvertising = _session.IsAdvertising;
        IsClockCalibrated = _session.IsClockCalibrated;
        IsPlaying = _session.Phase == SpeakerPhase.Playing;
        PhaseLabel = _session.Phase switch
        {
            SpeakerPhase.Idle => "空闲",
            SpeakerPhase.Advertising => "广播中",
            SpeakerPhase.Connected => "已连接",
            SpeakerPhase.Ready => "就绪",
            SpeakerPhase.Playing => "播放中",
            SpeakerPhase.Error => "错误",
            _ => _session.Phase.ToString(),
        };

        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(LocalAddress));
        OnPropertyChanged(nameof(HostName));
        OnPropertyChanged(nameof(ErrorMessage));
        OnPropertyChanged(nameof(IsAdvertising));
        OnPropertyChanged(nameof(IsClockCalibrated));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(HasHost));
        OnPropertyChanged(nameof(HasLocalAddress));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(ClockLabel));
        OnPropertyChanged(nameof(PhaseLabel));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.StateChanged -= OnSessionChanged;
        _session.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
