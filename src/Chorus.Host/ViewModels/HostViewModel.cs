using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Chorus.Host.Session;
using ChorusCore.Audio;
using ChorusCore.Network.Mdns;
using ChorusCore.Protocol;

namespace Chorus.Host.ViewModels;

/// <summary>
/// Binds the UI to <see cref="HostSessionController"/>. State changes arrive on
/// background threads and are marshalled to the UI thread via <see cref="Dispatcher.UIThread"/>.
/// </summary>
public sealed class HostViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly HostSessionController _controller;
    private readonly DispatcherTimer _diagTimer;
    private string _status = "未启动";
    private string _phaseDisplay = "未开始";
    private string _localIpDisplay = "";
    private string _rttDisplay = "";
    private string? _error;
    private bool _hasLocalIp;
    private bool _hasRtt;
    private bool _hasError;
    private bool _hasSpeakers;
    private int _speakerCount;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _playLocal = true;
    private bool _muteLocal;
    private bool _streamingSystemAudio;
    private string _currentSource = "未播放";
    private string _captureInfo = "";
    private string _manualIp = "";
    private string _manualPort = SyncBonjour.ControlPort.ToString();
    private bool _showDiagnostics;
    private bool _isDark = true;
    private bool _helpVisible;
    private string _browserStatus = "";

    public ObservableCollection<DeviceInfo> Speakers { get; } = new();
    public ObservableCollection<DiscoveredPeer> DiscoveredPeers { get; } = new();
    public Playlist Playlist => _controller.Playlist;

    public string Status { get => _status; set => Set(ref _status, value); }
    public string PhaseDisplay { get => _phaseDisplay; set => Set(ref _phaseDisplay, value); }
    public string LocalIPDisplay { get => _localIpDisplay; set => Set(ref _localIpDisplay, value); }
    public string RTTDisplay { get => _rttDisplay; set => Set(ref _rttDisplay, value); }
    public string? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) HasError = !string.IsNullOrEmpty(value); }
    }
    public bool HasLocalIP { get => _hasLocalIp; set => Set(ref _hasLocalIp, value); }
    public bool HasRTT { get => _hasRtt; set => Set(ref _hasRtt, value); }
    public bool HasError { get => _hasError; set => Set(ref _hasError, value); }
    public bool HasSpeakers { get => _hasSpeakers; set => Set(ref _hasSpeakers, value); }
    public int SpeakerCount { get => _speakerCount; set => Set(ref _speakerCount, value); }
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }
    public bool IsPaused { get => _isPaused; set => Set(ref _isPaused, value); }
    public bool CanPauseResume => IsPlaying;
    public string PauseResumeLabel => IsPaused ? "继续" : "暂停";
    public bool PlayLocal
    {
        get => _playLocal;
        set
        {
            if (Set(ref _playLocal, value) && value)
                MuteLocal = false;
        }
    }
    public bool MuteLocal
    {
        get => _muteLocal;
        set
        {
            if (Set(ref _muteLocal, value))
            {
                if (value) PlayLocal = false;
                _controller.MuteLocalOutput = value;
            }
        }
    }
    public bool IsStreamingSystemAudio
    {
        get => _streamingSystemAudio;
        private set
        {
            if (Set(ref _streamingSystemAudio, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemAudioButtonLabel)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStartSystemAudio)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoAdvanceEnabled)));
            }
        }
    }
    public string SystemAudioButtonLabel => IsStreamingSystemAudio ? "停止统一转播" : "统一转播系统声音";
    public bool CanStartSystemAudio => HasSpeakers || IsStreamingSystemAudio;
    public bool AutoAdvanceEnabled => !IsStreamingSystemAudio;
    public string CurrentSource { get => _currentSource; set => Set(ref _currentSource, value); }
    public string CaptureInfo { get => _captureInfo; set => Set(ref _captureInfo, value); }
    public string ManualIP { get => _manualIp; set => Set(ref _manualIp, value); }
    public string ManualPort { get => _manualPort; set => Set(ref _manualPort, value); }
    public bool HasDiscoveredPeers => DiscoveredPeers.Count > 0;
    public bool HasCurrentItem => Playlist.Current != null;
    public bool HasItems => Playlist.Items.Count > 0;
    public bool CanPlay => HasCurrentItem && (HasSpeakers || PlayLocal) && !IsStreamingSystemAudio;
    public bool CanManualConnect => !string.IsNullOrWhiteSpace(ManualIP);
    public string BrowserStatus { get => _browserStatus; set => Set(ref _browserStatus, value); }
    public bool IsLive => _controller.CurrentPhase is HostSessionController.Phase.Playing
        or HostSessionController.Phase.Ready
        or HostSessionController.Phase.SyncingClock;
    public bool IsDark
    {
        get => _isDark;
        set => Set(ref _isDark, value);
    }
    public bool HelpVisible
    {
        get => _helpVisible;
        set => Set(ref _helpVisible, value);
    }
    public bool ShowDiagnostics
    {
        get => _showDiagnostics;
        set
        {
            if (Set(ref _showDiagnostics, value))
            {
                if (value) _diagTimer.Start();
                else _diagTimer.Stop();
            }
        }
    }

    public string HelpText =>
        """
        连接扬声器
        · 先在 iPhone/iPad 上打开 Chorus Speaker 并开始广播
        · Host 会自动发现附近设备；点「连接」建立双通道
        · 若发现失败，输入手机 IP 与端口 17482 手动连接
        · 请与手机同一 Wi‑Fi，关闭 VPN；公司网常有客户端隔离

        同步播放音频
        · 选择音频或文件夹，或加载测试音调
        · 可选「本机同时播放」
        · 点「同步播放」按统一时间线推流

        转播系统声音
        · 点「统一转播系统声音」捕获 Windows 正在播放的声音
        · 电脑与手机都会延迟约 1.2–1.5 秒以保持对齐
        · 受 DRM 保护的内容可能采不到

        常见问题
        · 发现不到设备：改用个人热点或手动 IP
        · 已连接无声音：确认 Speaker 已允许本地网络，并已进入就绪
        """;

    public HostViewModel()
    {
        _controller = new HostSessionController();
        _controller.StateChanged += () => Dispatcher.UIThread.Post(Refresh);
        try
        {
            _controller.StartListening();
        }
        catch (Exception ex)
        {
            Error = $"启动失败：{ex.Message}（请检查端口是否被占用）";
        }
        Refresh();
        _diagTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh());
        _diagTimer.Start();
    }

    private void Refresh()
    {
        Status = _controller.StatusText;
        PhaseDisplay = _controller.CurrentPhase switch
        {
            HostSessionController.Phase.Idle => "未开始",
            HostSessionController.Phase.Discoverable => "可被发现",
            HostSessionController.Phase.Connected => "已连接",
            HostSessionController.Phase.SyncingClock => "校准时钟",
            HostSessionController.Phase.Ready => "就绪",
            HostSessionController.Phase.Playing => "播放中",
            HostSessionController.Phase.Error => "错误",
            _ => _controller.CurrentPhase.ToString()
        };

        CurrentSource = _controller.CurrentSource;
        CaptureInfo = _controller.CaptureInfo;
        IsStreamingSystemAudio = _controller.IsStreamingSystemAudio;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCurrentItem)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasItems)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPlay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanManualConnect)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PauseResumeLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStartSystemAudio)));

        if (!string.IsNullOrEmpty(_controller.LocalIPv4))
        {
            LocalIPDisplay = $"本机局域网 IP：{_controller.LocalIPv4}（请确认与手机同一网段）";
            HasLocalIP = true;
        }
        else HasLocalIP = false;

        if (_controller.BestRTT.HasValue)
        {
            RTTDisplay = $"{(int)(_controller.BestRTT.Value * 1000)} ms";
            HasRTT = true;
        }
        else HasRTT = false;

        Error = _controller.LastError;
        IsPlaying = _controller.CurrentPhase == HostSessionController.Phase.Playing;
        IsPaused = _controller.IsPaused;

        var live = _controller.ConnectedSpeakers;
        if (Speakers.Count != live.Count || !Speakers.SequenceEqual(live))
        {
            Speakers.Clear();
            foreach (var s in live) Speakers.Add(s);
        }
        HasSpeakers = Speakers.Count > 0;
        SpeakerCount = Speakers.Count;

        var discovered = _controller.Browser.Peers.Values.ToList();
        bool peersChanged = DiscoveredPeers.Count != discovered.Count
            || !DiscoveredPeers.Select(p => p.InstanceName + p.IPAddress).SequenceEqual(
                discovered.Select(p => p.InstanceName + p.IPAddress));
        if (peersChanged)
        {
            DiscoveredPeers.Clear();
            foreach (var p in discovered) DiscoveredPeers.Add(p);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDiscoveredPeers)));
        }

        BrowserStatus = DiscoveredPeers.Count > 0
            ? $"已发现 {DiscoveredPeers.Count} 台设备"
            : "正在搜索附近扬声器…";
    }

    public bool IsPeerConnected(DiscoveredPeer peer) => _controller.IsConnectedToPeer(peer);

    public void PlayDemoTone() => _controller.PlayDemoTone(PlayLocal);

    public void ToggleSystemAudio()
    {
        if (IsStreamingSystemAudio) _controller.StopPlayback();
        else _controller.PlaySystemAudio(PlayLocal);
    }

    public void Stop() => _controller.StopPlayback();

    public void PauseResume()
    {
        if (_controller.IsPaused) _controller.ResumePlayback();
        else _controller.PausePlayback();
    }

    public void AddFilesToPlaylist(IEnumerable<string> files)
    {
        foreach (var f in files)
            if (AudioFormats.IsSupported(f))
                Playlist.AddFile(f);
        Refresh();
    }

    public void AddFolderToPlaylist(string folder)
    {
        Playlist.AddFolder(folder);
        Refresh();
    }

    public void PlayPlaylistTrack(Guid id)
    {
        Playlist.Select(id);
        var item = Playlist.Current;
        if (item != null) _controller.PlayPlaylistTrack(item.FilePath, item.Title, PlayLocal);
    }

    public void PlayNext()
    {
        var next = Playlist.MoveToNext();
        if (next != null) _controller.PlayPlaylistTrack(next.FilePath, next.Title, PlayLocal);
    }

    public void PlayPrevious()
    {
        var prev = Playlist.MoveToPrevious();
        if (prev != null) _controller.PlayPlaylistTrack(prev.FilePath, prev.Title, PlayLocal);
    }

    public void RemoveFromPlaylist(Guid id)
    {
        Playlist.Remove(id);
        Refresh();
    }

    public void ClearPlaylist()
    {
        Playlist.Clear();
        Refresh();
    }

    public void PlayCurrent()
    {
        var item = Playlist.Current;
        if (item != null) _controller.PlayPlaylistTrack(item.FilePath, item.Title, PlayLocal);
    }

    public void ConnectToPeer(DiscoveredPeer peer)
    {
        if (_controller.IsConnectedToPeer(peer))
        {
            _controller.DisconnectEndpoint(peer.IPAddress.ToString(), peer.Port);
            return;
        }
        _controller.Connect(peer.IPAddress.ToString(), peer.Port, peer.InstanceName);
    }

    public void ManualConnectOrDisconnect()
    {
        if (string.IsNullOrWhiteSpace(ManualIP)) return;
        if (!ushort.TryParse(ManualPort.Trim(), out var port))
            port = SyncBonjour.ControlPort;

        var ip = ManualIP.Trim();
        if (_controller.IsConnectedTo($"{ip}:{port}"))
        {
            _controller.DisconnectEndpoint(ip, port);
            return;
        }
        _controller.Connect(ip, port);
    }

    public void RemoveSpeaker(DeviceInfo speaker) => _controller.DisconnectSpeaker(speaker);

    public void Dispose()
    {
        _diagTimer.Stop();
        _controller.Dispose();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
