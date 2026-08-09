using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Chorus.Host.Localization;
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
    private readonly DispatcherTimer _refreshTimer;
    private readonly Action _onLanguageChanged;
    private string _status = "";
    private string _phaseDisplay = "";
    private string _localIpDisplay = "";
    private string? _error;
    private bool _hasLocalIp;
    private bool _hasError;
    private bool _hasSpeakers;
    private int _speakerCount;
    private bool _isPlaying;
    private bool _isPaused;
    private bool _playLocal = true;
    private bool _muteLocal;
    private bool _streamingSystemAudio;
    private string _currentSource = "";
    private string _manualIp = "";
    private string _manualPort = SyncBonjour.ControlPort.ToString();
    private bool _isDark = true;
    private bool _helpVisible;
    private string _browserStatus = "";
    private string _languageLabel = "";
    private double _localSyncOffsetMs;

    public ObservableCollection<DeviceInfo> Speakers { get; } = new();
    public ObservableCollection<PeerRowViewModel> DiscoveredPeers { get; } = new();
    public Playlist Playlist => _controller.Playlist;

    public string Status { get => _status; set => Set(ref _status, value); }
    public string PhaseDisplay { get => _phaseDisplay; set => Set(ref _phaseDisplay, value); }
    public bool HasRTT => _controller.BestRTT.HasValue;
    public string RTTDisplay => _controller.BestRTT is { } rtt
        ? $"{(int)Math.Round(rtt * 1000)} ms"
        : "";
    public string LocalIPDisplay { get => _localIpDisplay; set => Set(ref _localIpDisplay, value); }
    public string? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) HasError = !string.IsNullOrEmpty(value); }
    }
    public bool HasLocalIP { get => _hasLocalIp; set => Set(ref _hasLocalIp, value); }
    public bool HasError { get => _hasError; set => Set(ref _hasError, value); }
    public bool HasSpeakers { get => _hasSpeakers; set => Set(ref _hasSpeakers, value); }
    public int SpeakerCount { get => _speakerCount; set => Set(ref _speakerCount, value); }
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }
    public bool IsPaused { get => _isPaused; set => Set(ref _isPaused, value); }
    public bool CanPauseResume => IsPlaying;
    public string PauseResumeLabel => IsPaused ? L10n.T("action.resume") : L10n.T("action.pause");
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
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPlayLocal)));
            }
        }
    }
    public string SystemAudioButtonLabel => IsStreamingSystemAudio
        ? L10n.T("action.stream.system.stop")
        : L10n.T("action.stream.system.start");
    public bool CanStartSystemAudio => HasSpeakers || IsStreamingSystemAudio;
    public bool AutoAdvanceEnabled => !IsStreamingSystemAudio;
    /// <summary>Local mirror is incompatible with WASAPI loopback (feedback).</summary>
    public bool CanPlayLocal => !IsStreamingSystemAudio;
    public string CurrentSource { get => _currentSource; set => Set(ref _currentSource, value); }
    public string ManualIP
    {
        get => _manualIp;
        set
        {
            if (Set(ref _manualIp, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ManualConnectLabel)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanManualConnect)));
            }
        }
    }
    public string ManualPort
    {
        get => _manualPort;
        set
        {
            if (Set(ref _manualPort, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ManualConnectLabel)));
        }
    }
    public bool HasDiscoveredPeers => DiscoveredPeers.Count > 0;
    public bool CanConnectAll => DiscoveredPeers.Any(r => !r.IsConnected);
    public string LocConnectAll => L10n.T("action.connect.all");
    public string ManualConnectLabel => IsManualEndpointConnected
        ? L10n.T("action.disconnect")
        : L10n.T("action.connect");
    public bool IsManualEndpointConnected
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ManualIP)) return false;
            if (!ushort.TryParse(ManualPort.Trim(), out var port)) port = SyncBonjour.ControlPort;
            return _controller.IsConnectedTo($"{ManualIP.Trim()}:{port}");
        }
    }
    public bool HasCurrentItem => Playlist.Current != null;
    public bool HasItems => Playlist.Items.Count > 0;
    public bool CanPlay => HasCurrentItem && (HasSpeakers || PlayLocal) && !IsStreamingSystemAudio;
    public bool CanManualConnect => !string.IsNullOrWhiteSpace(ManualIP);
    public string BrowserStatus { get => _browserStatus; set => Set(ref _browserStatus, value); }
    public bool IsLive => _controller.CurrentPhase is HostSessionController.Phase.Playing
        or HostSessionController.Phase.Ready
        or HostSessionController.Phase.SyncingClock;

    /// <summary>Peek+decay audio envelope for reactive glass (once per UI frame).</summary>
    public float TakeAudioLevel() => _controller.TakeAudioLevel();
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

    public string LanguageLabel { get => _languageLabel; set => Set(ref _languageLabel, value); }
    public string AppearanceMode { get; private set; } = "dark"; // system | light | dark

    /// <summary>Local trim vs phone in milliseconds (−120…+120). Positive delays the PC.</summary>
    public double LocalSyncOffsetMs
    {
        get => _localSyncOffsetMs;
        set
        {
            var v = Math.Clamp(value, -120, 120);
            if (!Set(ref _localSyncOffsetMs, v)) return;
            _controller.LocalSyncOffsetSeconds = v / 1000.0;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LocalSyncOffsetLabel)));
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChorusHost");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "local-sync-offset.txt"), v.ToString("0"));
            }
            catch { }
        }
    }

    public string LocalSyncOffsetLabel =>
        $"{L10n.T("sync.trim")}: {(LocalSyncOffsetMs >= 0 ? "+" : "")}{(int)LocalSyncOffsetMs} ms";

    // Bound UI copy
    public string LocTagline => L10n.T("host.tagline");
    public string LocHelp => L10n.T("action.help");
    public string LocLanguage => L10n.T("action.language");
    public string LocAppearance => L10n.T("action.appearance");
    public string LocAppearanceSystem => L10n.T("appearance.system");
    public string LocAppearanceLight => L10n.T("appearance.light");
    public string LocAppearanceDark => L10n.T("appearance.dark");
    public string LocLangSystem => L10n.T("appearance.system");
    public string LocLangZh => "简体中文";
    public string LocLangEn => "English";
    public string LocLangJa => "日本語";
    public string LocLangKo => "한국어";
    public string LocClose => L10n.T("action.close");
    public string LocConnect => L10n.T("action.connect");
    public string LocDisconnect => L10n.T("action.disconnect");
    public string LocNearby => L10n.T("section.nearby");
    public string LocManual => L10n.T("section.manual.connect");
    public string LocSession => L10n.T("section.session");
    public string LocPlayback => L10n.T("section.playback");
    public string LocPlaylist => L10n.T("section.playlist");
    public string LocPlayLocal => L10n.T("toggle.play.locally");
    public string LocAutoNext => L10n.T("toggle.auto.next");
    public string LocChooseAudio => L10n.T("action.choose.audio");
    public string LocChooseFolder => L10n.T("action.choose.folder");
    public string LocTestTone => L10n.T("action.test.tone");
    public string LocSyncPlay => L10n.T("action.sync.play");
    public string LocStop => L10n.T("action.stop");
    public string LocClearPlaylist => L10n.T("action.playlist.clear");
    public string LocHintDiscovery => L10n.T("hint.discovery");
    public string LocHintConnect => L10n.T("hint.connect");
    public string LocHintPlaylistEmpty => L10n.T("hint.playlist.empty");
    public string LocPhoneIpWatermark => L10n.T("field.phone.ip");
    public string LocPortWatermark => L10n.T("field.port");
    public string LocNowPlaying => L10n.T("playlist.now.playing");
    public string LocReady => L10n.T("status.ready");
    public string LocTipPrev => L10n.T("tip.prev");
    public string LocTipNext => L10n.T("tip.next");
    public string LocHelpTitle => L10n.T("help.title");
    public string LocSyncTrimHint => L10n.T("sync.trim.hint");
    public string HelpText => L10n.T("help.body");

    public HostViewModel()
    {
        L10n.LoadPreference();
        LanguageLabel = L10n.SelectionDisplay;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ChorusHost", "local-sync-offset.txt");
            if (File.Exists(path) && double.TryParse(File.ReadAllText(path).Trim(), out var saved))
                _localSyncOffsetMs = Math.Clamp(saved, -120, 120);
        }
        catch { }
        _onLanguageChanged = () => Dispatcher.UIThread.Post(OnLanguageChanged);
        _controller = new HostSessionController();
        _controller.LocalSyncOffsetSeconds = _localSyncOffsetMs / 1000.0;
        _controller.StateChanged += () => Dispatcher.UIThread.Post(Refresh);
        L10n.LanguageChanged += _onLanguageChanged;
        try
        {
            _controller.StartListening();
        }
        catch (Exception ex)
        {
            Error = $"启动失败：{ex.Message}";
        }
        Refresh();
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh());
        _refreshTimer.Start();
    }

    private void OnLanguageChanged()
    {
        LanguageLabel = L10n.SelectionDisplay;
        RaiseLocProperties();
        foreach (var row in DiscoveredPeers) row.RefreshLabels();
        Refresh();
    }

    public void SetLanguage(string code) => L10n.Selection = code;

    public void SetAppearanceMode(string mode)
    {
        AppearanceMode = mode;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AppearanceMode)));
    }

    private void RaiseLocProperties()
    {
        foreach (var name in new[]
        {
            nameof(LocTagline), nameof(LocHelp), nameof(LocLanguage), nameof(LocAppearance),
            nameof(LocAppearanceSystem), nameof(LocAppearanceLight), nameof(LocAppearanceDark),
            nameof(LocLangSystem), nameof(LocClose), nameof(LocConnect), nameof(LocDisconnect),
            nameof(LocConnectAll), nameof(CanConnectAll),
            nameof(LocNearby), nameof(LocManual), nameof(LocSession), nameof(LocPlayback),
            nameof(LocPlaylist), nameof(LocPlayLocal), nameof(LocAutoNext), nameof(LocChooseAudio),
            nameof(LocChooseFolder), nameof(LocTestTone), nameof(LocSyncPlay), nameof(LocStop),
            nameof(LocClearPlaylist), nameof(LocHintDiscovery), nameof(LocHintConnect),
            nameof(LocHintPlaylistEmpty), nameof(LocPhoneIpWatermark), nameof(LocPortWatermark),
            nameof(LocNowPlaying), nameof(LocReady), nameof(LocTipPrev), nameof(LocTipNext),
            nameof(LocHelpTitle), nameof(HelpText), nameof(PauseResumeLabel),
            nameof(SystemAudioButtonLabel), nameof(ManualConnectLabel), nameof(LanguageLabel),
            nameof(LocSyncTrimHint), nameof(LocalSyncOffsetLabel),
        })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Refresh()
    {
        Status = _controller.StatusText;
        PhaseDisplay = _controller.CurrentPhase switch
        {
            HostSessionController.Phase.Idle => L10n.T("phase.idle"),
            HostSessionController.Phase.Discoverable => L10n.T("phase.discoverable"),
            HostSessionController.Phase.Connected => L10n.T("phase.connected"),
            HostSessionController.Phase.SyncingClock => L10n.T("phase.calibrating"),
            HostSessionController.Phase.Ready => L10n.T("phase.ready"),
            HostSessionController.Phase.Playing => L10n.T("phase.playing"),
            HostSessionController.Phase.Error => L10n.T("phase.error"),
            _ => _controller.CurrentPhase.ToString()
        };

        CurrentSource = _controller.CurrentSource;
        IsStreamingSystemAudio = _controller.IsStreamingSystemAudio;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCurrentItem)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasItems)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPlay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanManualConnect)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ManualConnectLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PauseResumeLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLive)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRTT)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RTTDisplay)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStartSystemAudio)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanPlayLocal)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SystemAudioButtonLabel)));

        if (!string.IsNullOrEmpty(_controller.LocalIPv4))
        {
            LocalIPDisplay = L10n.Format("hint.host.local.ip", _controller.LocalIPv4);
            HasLocalIP = true;
        }
        else HasLocalIP = false;

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

        SyncDiscoveredPeers();

        BrowserStatus = DiscoveredPeers.Count > 0
            ? L10n.Format("status.devices.found", DiscoveredPeers.Count)
            : L10n.T("status.searching");
    }

    private void SyncDiscoveredPeers()
    {
        var discovered = _controller.Browser.Peers.Values
            .OrderBy(p => p.InstanceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var keys = discovered.Select(p => $"{p.IPAddress}:{p.Port}").ToHashSet(StringComparer.Ordinal);
        bool structuralChange = DiscoveredPeers.Count != discovered.Count
            || DiscoveredPeers.Any(r => !keys.Contains(r.EndpointKey));

        if (structuralChange)
        {
            DiscoveredPeers.Clear();
            foreach (var p in discovered)
            {
                DiscoveredPeers.Add(new PeerRowViewModel(p)
                {
                    IsConnected = _controller.IsConnectedToPeer(p),
                });
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasDiscoveredPeers)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConnectAll)));
            return;
        }

        foreach (var row in DiscoveredPeers)
            row.IsConnected = _controller.IsConnectedToPeer(row.Peer);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConnectAll)));
    }

    public void ConnectToPeerRow(PeerRowViewModel row) => ConnectToPeer(row.Peer);

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

    /// <summary>Connect every discovered peer that is not already linked.</summary>
    public void ConnectAllPeers()
    {
        var pending = DiscoveredPeers.Where(r => !r.IsConnected).Select(r => r.Peer).ToList();
        if (pending.Count == 0) return;

        Status = L10n.Format("status.connecting.all", pending.Count);

        // Connect off the UI thread — TcpClient.Connect is synchronous.
        _ = Task.Run(() =>
        {
            foreach (var peer in pending)
            {
                try
                {
                    if (_controller.IsConnectedToPeer(peer)) continue;
                    _controller.Connect(peer.IPAddress.ToString(), peer.Port, peer.InstanceName);
                }
                catch
                {
                    // Per-peer errors are surfaced via LastError / StatusText.
                }
            }
        });
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
        _refreshTimer.Stop();
        L10n.LanguageChanged -= _onLanguageChanged;
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
