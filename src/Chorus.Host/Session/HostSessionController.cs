using System.Net.Sockets;
using System.Threading;
using ChorusAudio.Capture;
using ChorusAudio.Devices;
using ChorusAudio.Playback;
using ChorusCore.Audio;
using ChorusCore.Network;
using ChorusCore.Network.Mdns;
using ChorusCore.Protocol;
using ChorusCore.Sync;
using NAudio.CoreAudioApi;

namespace Chorus.Host.Session;

/// <summary>
/// Host-side session: discovers/connects Speakers over dual TCP (control + audio),
/// calibrates clocks, and streams PCM. Mirrors macOS <c>HostSessionController</c>
/// connection model — audio frames only on the channel that sent <c>audioChannelHello</c>.
/// </summary>
public sealed class HostSessionController : IDisposable
{
    public enum Phase { Idle, Discoverable, Connected, SyncingClock, Ready, Playing, Error }

    private readonly DeviceInfo _localDevice;
    private readonly List<SyncConnection> _connections = new();
    private readonly List<SyncConnection> _audioConnections = new();
    private readonly Dictionary<SyncConnection, SyncConnection> _audioByControl = new();
    private readonly Dictionary<SyncConnection, SyncConnection> _controlByAudio = new();
    private readonly Dictionary<SyncConnection, ClockSynchronizer> _synchronizers = new();
    private readonly Dictionary<SyncConnection, Timer> _clockTimers = new();
    private readonly Dictionary<SyncConnection, string> _speakerIdByConn = new();
    private readonly Dictionary<string, SyncConnection> _controlBySpeakerId = new();
    private readonly Dictionary<SyncConnection, double> _lastRtt = new();
    private readonly Dictionary<SyncConnection, int> _stablePings = new();
    private readonly Dictionary<SyncConnection, DateTime> _lastPingTime = new();
    private readonly AdaptiveLeadTime _adaptiveLeadTime = new();
    private readonly object _lock = new();

    private HostListener? _listener;
    private MdnsBrowser _mdnsBrowser = null!;
    private LocalAudioPlayer? _localPlayer;
    private bool _playLocal;
    private bool _muteLocal;
    private bool _savedMute;
    private bool _hasSavedMute;
    private IAudioCapture? _capture;
    private FileAudioCapture? _currentFileCapture;
    private Guid? _currentSessionId;
    private double _sessionStartHostPlayAt;
    private double _sessionLead = 1.4;
    private bool _sessionIsSystemAudio;
    private CancellationTokenSource? _prepareCts;
    private double _liveSampleRate = SyncProtocol.SampleRate;
    private ulong _liveSequence;
    private readonly System.Collections.Concurrent.ConcurrentQueue<(AudioChunkHeader Header, byte[] Pcm)> _sendQueue = new();
    private Task? _sendTask;
    private volatile bool _sending;
    private float _maxSample;
    private Action<ReadOnlyMemory<float>>? _samplesHandler;
    private ulong _liveSampleIndex;
    private volatile bool _paused;
    private volatile bool _disposed;
    private bool _streamingSystemAudio;
    private VirtualAudioRouter? _virtualRouter;

    /// <summary>
    /// Local audible trim vs phone, in seconds. Positive = delay PC (fix PC-early);
    /// negative = advance PC (fix PC-late). Applied on the next StartAt.
    /// </summary>
    public double LocalSyncOffsetSeconds { get; set; }

    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public string StatusText { get; private set; } = "未启动";
    public string? LastError { get; private set; }
    public double? BestRTT { get; private set; }
    public string? LocalIPv4 { get; private set; }
    public ushort ListeningPort { get; private set; }
    public List<DeviceInfo> ConnectedSpeakers { get; } = new();
    public long SamplesSent;
    public string CaptureInfo => BuildCaptureInfo();
    public string CurrentSource { get; private set; } = "未播放";
    public bool IsPaused => _paused;
    public bool IsStreamingSystemAudio => _streamingSystemAudio;
    public bool MuteLocalOutput { get => _muteLocal; set => _muteLocal = value; }
    public Playlist Playlist { get; } = new();

    public event Action? StateChanged;

    public HostSessionController(string? deviceName = null)
    {
        _localDevice = new DeviceInfo(
            Guid.NewGuid().ToString(),
            deviceName ?? Environment.MachineName,
            DeviceRole.Host);
        _mdnsBrowser = new MdnsBrowser();
        _mdnsBrowser.PeersChanged += () => StateChanged?.Invoke();
        _mdnsBrowser.ErrorOccurred += err => { LastError = $"mDNS 浏览器: {err}"; StateChanged?.Invoke(); };
        _mdnsBrowser.Start();
    }

    public MdnsBrowser Browser => _mdnsBrowser;

    /// <param name="acceptInbound">
    /// When true, also listen on <paramref name="port"/> for Speakers that dial the Host.
    /// Default false so same-machine Speaker advertising can own 17482 without a port clash.
    /// </param>
    public void StartListening(ushort port = SyncBonjour.ControlPort, bool acceptInbound = false)
    {
        // Host browses for Speakers. Inbound listen is optional (legacy Speakers that dial Host).
        LocalIPv4 = LocalAddress.PrimaryIPv4();
        ListeningPort = port;
        if (acceptInbound)
        {
            _listener = new HostListener();
            _listener.ConnectionAccepted += conn => AttachIncoming(conn);
            _listener.Start(port);
            LocalIPv4 = _listener.LocalIPv4 ?? LocalIPv4;
            ListeningPort = _listener.ListeningPort;
        }
        CurrentPhase = Phase.Discoverable;
        StatusText = string.IsNullOrEmpty(LocalIPv4)
            ? "正在搜索附近扬声器…"
            : $"正在搜索附近扬声器… 本机 {LocalIPv4}";
        StateChanged?.Invoke();
    }

    /// <summary>Active dual-TCP connect (control + audio), matching macOS Host.</summary>
    public void Connect(string host, ushort port = SyncBonjour.ControlPort, string? displayName = null)
    {
        var trimmed = host.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            LastError = "请输入手机 IP";
            StateChanged?.Invoke();
            return;
        }

        var label = $"{trimmed}:{port}";
        lock (_lock)
        {
            if (_connections.Any(c => c.RemoteLabel == label))
                return;
        }

        StatusText = $"正在连接 {displayName ?? label}…";
        CurrentPhase = Phase.Connected;
        LastError = null;
        StateChanged?.Invoke();

        TcpClient? controlClient = null;
        TcpClient? audioClient = null;
        try
        {
            controlClient = new TcpClient();
            audioClient = new TcpClient();
            controlClient.Connect(trimmed, port);
            audioClient.Connect(trimmed, port);
            var control = new SyncConnection(controlClient, label);
            var audio = new SyncConnection(audioClient, label + "-audio");
            controlClient = null;
            audioClient = null;
            AttachPaired(control, audio, displayName ?? label);
        }
        catch (Exception ex)
        {
            try { controlClient?.Dispose(); } catch { }
            try { audioClient?.Dispose(); } catch { }
            LastError = $"连接失败：{ex.Message}";
            CurrentPhase = Phase.Error;
            StatusText = "连接失败";
            StateChanged?.Invoke();
        }
    }

    public void AttachPaired(SyncConnection control, SyncConnection audio, string displayName)
    {
        lock (_lock)
        {
            _connections.Add(control);
            _audioConnections.Add(audio);
            _audioByControl[control] = audio;
            _controlByAudio[audio] = control;
            _synchronizers[control] = new ClockSynchronizer();
        }

        CurrentPhase = Phase.Connected;
        StatusText = $"已连接 {displayName}，握手中…";
        StateChanged?.Invoke();

        control.Start(evt => HandleControlChannel(evt, control));
        audio.Start(evt => HandleAudioChannel(evt, audio));
    }

    /// <summary>Inbound single TCP (Speaker dialed Host). Audio may arrive on a second inbound socket.</summary>
    public void AttachIncoming(SyncConnection conn)
    {
        lock (_lock)
        {
            _connections.Add(conn);
            _synchronizers[conn] = new ClockSynchronizer();
        }
        conn.Start(evt => HandleControlChannel(evt, conn));
        CurrentPhase = Phase.Connected;
        StatusText = "已连接，握手中…";
        StateChanged?.Invoke();
    }

    public bool IsConnectedTo(string endpointLabel)
    {
        lock (_lock)
            return _connections.Any(c => c.RemoteLabel == endpointLabel || c.RemoteLabel.StartsWith(endpointLabel, StringComparison.Ordinal));
    }

    public bool IsConnectedToPeer(DiscoveredPeer peer)
    {
        var label = $"{peer.IPAddress}:{peer.Port}";
        lock (_lock)
            return _connections.Any(c => c.RemoteLabel == label);
    }

    public void DisconnectSpeaker(DeviceInfo speaker)
    {
        SyncConnection? control;
        lock (_lock)
            _controlBySpeakerId.TryGetValue(speaker.Id, out control);
        if (control == null) return;
        try { control.SendControl(new ControlPayload.Goodbye(_localDevice.Id)); } catch { }
        RemovePairedAudio(control);
        RemoveConnection(control, "已断开设备");
    }

    public void DisconnectEndpoint(string host, ushort port = SyncBonjour.ControlPort)
    {
        var label = $"{host.Trim()}:{port}";
        SyncConnection? control;
        lock (_lock)
            control = _connections.FirstOrDefault(c => c.RemoteLabel == label);
        if (control == null) return;
        try { control.SendControl(new ControlPayload.Goodbye(_localDevice.Id)); } catch { }
        RemovePairedAudio(control);
        RemoveConnection(control, "已断开设备");
    }

    private void HandleControlChannel(SyncConnectionEvent evt, SyncConnection conn)
    {
        switch (evt)
        {
            case SyncConnectionEvent.Connected:
                conn.SendControl(new ControlPayload.Hello(_localDevice));
                StartClockPings(conn);
                break;
            case SyncConnectionEvent.Disconnected d:
                RemovePairedAudio(conn);
                RemoveConnection(conn, d.Reason);
                break;
            case SyncConnectionEvent.Control c:
                HandleControl(c.Payload, conn);
                break;
            case SyncConnectionEvent.Audio:
                break;
        }
    }

    private void HandleAudioChannel(SyncConnectionEvent evt, SyncConnection audio)
    {
        switch (evt)
        {
            case SyncConnectionEvent.Connected:
                audio.SendControl(new ControlPayload.AudioChannelHello(_localDevice.Id));
                break;
            case SyncConnectionEvent.Disconnected:
                SyncConnection? control;
                lock (_lock)
                    _controlByAudio.TryGetValue(audio, out control);
                DetachAudioOnly(audio);
                if (control != null)
                {
                    try { control.Cancel(); } catch { }
                    RemoveConnection(control, "音频通道已断开");
                }
                break;
            case SyncConnectionEvent.Control:
            case SyncConnectionEvent.Audio:
                break;
        }
    }

    private void HandleControl(ControlPayload payload, SyncConnection conn)
    {
        switch (payload)
        {
            case ControlPayload.Hello h:
                RegisterSpeaker(conn, h.Info);
                conn.SendControl(new ControlPayload.Welcome(_localDevice));
                LastError = null;
                CurrentPhase = Phase.SyncingClock;
                StatusText = $"已连接 {h.Info.Name}，校准时钟…";
                StateChanged?.Invoke();
                break;

            case ControlPayload.Welcome w:
                RegisterSpeaker(conn, w.Info);
                LastError = null;
                CurrentPhase = Phase.SyncingClock;
                StatusText = $"已连接 {w.Info.Name}，校准时钟…";
                StateChanged?.Invoke();
                break;

            case ControlPayload.AudioChannelHello:
                // Hosts send this on the audio socket; ignore if it arrives here.
                break;

            case ControlPayload.ClockPong pong:
                ClockSynchronizer? syncer;
                lock (_lock) _synchronizers.TryGetValue(conn, out syncer);
                syncer?.RecordPong(pong.Pong, HostTime.Now());
                var estimate = syncer?.BestEstimate;
                if (estimate.HasValue)
                {
                    BestRTT = estimate.Value.RoundTrip;
                    conn.SendControl(new ControlPayload.ClockOffset(estimate.Value.Offset));
                    _adaptiveLeadTime.RecordRoundTrip(estimate.Value.RoundTrip);

                    double rtt = estimate.Value.RoundTrip;
                    lock (_lock)
                    {
                        double prev = _lastRtt.GetValueOrDefault(conn, -1);
                        _lastRtt[conn] = rtt;
                        if (prev >= 0 && Math.Abs(rtt - prev) < 0.005)
                            _stablePings[conn] = _stablePings.GetValueOrDefault(conn) + 1;
                        else
                            _stablePings[conn] = 0;
                    }

                    if (estimate.Value.RoundTrip < 0.08 && CurrentPhase != Phase.Playing)
                    {
                        CurrentPhase = Phase.Ready;
                        StatusText = $"就绪（RTT ≈ {(int)(estimate.Value.RoundTrip * 1000)} ms）";
                        StateChanged?.Invoke();
                    }
                }
                break;

            case ControlPayload.Goodbye:
                RemovePairedAudio(conn);
                RemoveConnection(conn, "扬声器已退出同步");
                break;

            case ControlPayload.StopAcknowledged:
                StatusText = "已停止（已确认）";
                StateChanged?.Invoke();
                break;
        }
    }

    private void RegisterSpeaker(SyncConnection conn, DeviceInfo info)
    {
        lock (_lock)
        {
            _speakerIdByConn[conn] = info.Id;
            _controlBySpeakerId[info.Id] = conn;
            if (!ConnectedSpeakers.Any(s => s.Id == info.Id))
                ConnectedSpeakers.Add(info);
        }
    }

    private void StartClockPings(SyncConnection conn)
    {
        var timer = new Timer(_ =>
        {
            try
            {
                int stable;
                DateTime lastPing;
                lock (_lock)
                {
                    if (!_connections.Contains(conn)) return;
                    stable = _stablePings.GetValueOrDefault(conn);
                    lastPing = _lastPingTime.GetValueOrDefault(conn, DateTime.MinValue);
                }
                double interval = stable >= 3 ? 2.0 : 0.5;
                if ((DateTime.UtcNow - lastPing).TotalSeconds < interval - 0.05) return;

                lock (_lock) _lastPingTime[conn] = DateTime.UtcNow;
                var ping = new ClockPingData(Guid.NewGuid(), HostTime.Now());
                conn.SendControl(new ControlPayload.ClockPing(ping));
            }
            catch { /* connection may be gone */ }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
        lock (_lock) _clockTimers[conn] = timer;
    }

    private void RemovePairedAudio(SyncConnection control)
    {
        SyncConnection? audio;
        lock (_lock)
        {
            if (!_audioByControl.TryGetValue(control, out audio))
                return;
            _audioByControl.Remove(control);
            _controlByAudio.Remove(audio);
            _audioConnections.Remove(audio);
        }
        try { audio.Dispose(); } catch { }
    }

    private void DetachAudioOnly(SyncConnection audio)
    {
        lock (_lock)
        {
            if (_controlByAudio.TryGetValue(audio, out var control))
            {
                _audioByControl.Remove(control);
                _controlByAudio.Remove(audio);
            }
            _audioConnections.Remove(audio);
        }
        try { audio.Dispose(); } catch { }
    }

    private void RemoveConnection(SyncConnection conn, string? reason)
    {
        lock (_lock)
        {
            if (_clockTimers.TryGetValue(conn, out var t)) { t.Dispose(); _clockTimers.Remove(conn); }
            _synchronizers.Remove(conn);
            _lastRtt.Remove(conn);
            _stablePings.Remove(conn);
            _lastPingTime.Remove(conn);
            _connections.Remove(conn);
            if (_speakerIdByConn.TryGetValue(conn, out var sid))
            {
                _speakerIdByConn.Remove(conn);
                _controlBySpeakerId.Remove(sid);
                ConnectedSpeakers.RemoveAll(s => s.Id == sid);
            }
        }
        try { conn.Dispose(); } catch { }

        bool anyLeft;
        lock (_lock) anyLeft = _connections.Count > 0;
        if (!anyLeft && CurrentPhase != Phase.Playing)
            CurrentPhase = Phase.Discoverable;

        StatusText = reason != null ? $"连接断开：{reason}" : "连接断开";
        StateChanged?.Invoke();
    }

    public void PlayDemoTone(bool playLocal = false)
        => StartPlayback(new DemoToneCapture(), "Demo Tone A4", playLocal, systemAudio: false);

    /// <summary>
    /// Mac-style system audio: route default output through a virtual cable (VB-Cable),
    /// capture that mix, then play locally + to speakers on one hostPlayAt timeline.
    /// </summary>
    public void PlaySystemAudio(bool playLocal = false)
    {
        bool hasRemote;
        lock (_lock)
        {
            hasRemote = _audioByControl.Count > 0
                ? _audioConnections.Count > 0
                : _connections.Count > 0;
        }
        if (!hasRemote)
        {
            LastError = "转播系统声音需要先连接至少一台扬声器";
            CurrentPhase = Phase.Error;
            StateChanged?.Invoke();
            return;
        }

        if (!VirtualAudioRouter.IsVirtualCableInstalled())
        {
            LastError = VirtualAudioRouter.MissingCableMessage;
            CurrentPhase = Phase.Error;
            StatusText = "缺少虚拟声卡";
            StateChanged?.Invoke();
            return;
        }

        StopPlayback();
        _prepareCts = new CancellationTokenSource();
        var token = _prepareCts.Token;
        CurrentPhase = Phase.SyncingClock;
        StatusText = "正在准备同步转播（切换虚拟声卡，请稍候）…";
        StateChanged?.Invoke();

        _ = Task.Run(async () =>
        {
            try
            {
                await StartSyncedSystemAudioAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CurrentPhase = Phase.Error;
                StatusText = "系统音频转播失败";
                try { _virtualRouter?.Dispose(); } catch { }
                _virtualRouter = null;
                StateChanged?.Invoke();
            }
        }, token);
    }

    private async Task StartSyncedSystemAudioAsync(CancellationToken token)
    {
        var router = VirtualAudioRouter.TryCreate()
            ?? throw new InvalidOperationException(VirtualAudioRouter.MissingCableMessage);
        _virtualRouter = router;

        StatusText = $"正在将系统输出切换到 {router.VirtualRenderName}…";
        StateChanged?.Invoke();
        router.Activate();
        // Core Audio / WASAPI needs time after default-device switch (Mac waits similarly).
        await Task.Delay(1200, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        const string title = "Windows 系统音频";
        var sessionId = Guid.NewGuid();
        _currentSessionId = sessionId;
        _streamingSystemAudio = true;
        _sessionIsSystemAudio = true;
        _playLocal = true;
        _liveSampleRate = SyncProtocol.SampleRate;
        _liveSequence = 0;
        _liveSampleIndex = 0;
        _maxSample = 0;
        _sendQueue.Clear();
        CurrentSource = title;

        Broadcast(new ControlPayload.PrepareSession(
            new PrepareSessionData(sessionId, SyncProtocol.SampleRate, SyncProtocol.Channels, title)));
        StatusText = "正在等待扬声器准备引擎…";
        StateChanged?.Invoke();
        await Task.Delay(700, token).ConfigureAwait(false);

        if (token.IsCancellationRequested || _currentSessionId != sessionId) return;

        // Same runway as file sync so PC speakers + iPhone share one timeline.
        double lead = Math.Max(_adaptiveLeadTime.RecommendedLeadTime, 1.4);
        _sessionLead = lead;
        _sessionStartHostPlayAt = HostTime.Now() + lead;
        Broadcast(new ControlPayload.StartPlayback(
            new StartPlaybackData(sessionId, _sessionStartHostPlayAt, lead)));

        var capture = new WindowsLoopbackCapture(router.VirtualRenderId);
        _capture = capture;
        _sending = true;
        _sendTask = Task.Run(SendLoop);
        _samplesHandler = samples => OnSamplesAvailable(samples, sessionId);
        capture.SamplesAvailable += _samplesHandler;
        capture.ErrorOccurred += err =>
        {
            LastError = err;
            CurrentPhase = Phase.Error;
            StateChanged?.Invoke();
        };

        // Play on the real speakers (not the virtual cable) at hostPlayAt ± trim.
        _localPlayer = new LocalAudioPlayer(SyncProtocol.SampleRate, SyncProtocol.Channels);
        _localPlayer.StartAt(_sessionStartHostPlayAt, router.PhysicalRenderId, LocalSyncOffsetSeconds);

        capture.Start();
        if (capture.CaptureFormat.Length == 0 && !string.IsNullOrEmpty(LastError))
            throw new InvalidOperationException(LastError);

        CurrentPhase = Phase.Playing;
        StatusText =
            $"同步转播中：本机「{router.PhysicalRenderName}」+ 手机（lead {(int)(lead * 1000)} ms）";
        StateChanged?.Invoke();
    }

    public void PlayPlaylistTrack(string filePath, string title, bool playLocal = false)
        => StartPlayback(new FileAudioCapture(filePath, title), title, playLocal, systemAudio: false);

    private void StartPlayback(IAudioCapture capture, string title, bool playLocal, bool systemAudio)
    {
        bool hasRemote;
        lock (_lock)
        {
            // Dual-TCP sessions require a paired audio channel; legacy inbound uses control only.
            hasRemote = _audioByControl.Count > 0
                ? _audioConnections.Count > 0
                : _connections.Count > 0;
        }

        if (!hasRemote && !playLocal)
        {
            LastError = "没有连接的扬声器，请先连接设备";
            CurrentPhase = Phase.Error;
            StateChanged?.Invoke();
            capture.Dispose();
            return;
        }

        StopPlayback();

        _capture = capture;
        _streamingSystemAudio = systemAudio;
        _sessionIsSystemAudio = systemAudio;
        _playLocal = playLocal;
        var sessionId = Guid.NewGuid();
        _currentSessionId = sessionId;
        _liveSampleRate = SyncProtocol.SampleRate;
        _liveSequence = 0;
        _liveSampleIndex = 0;
        _maxSample = 0;
        _sendQueue.Clear();

        // Match Mac: prepare first so iOS can rebuild AVAudioEngine before hostPlayAt is frozen.
        Broadcast(new ControlPayload.PrepareSession(
            new PrepareSessionData(sessionId, SyncProtocol.SampleRate, SyncProtocol.Channels, title)));
        CurrentSource = title;
        CurrentPhase = Phase.SyncingClock;
        StatusText = $"正在准备：{title}";
        StateChanged?.Invoke();

        _prepareCts = new CancellationTokenSource();
        var token = _prepareCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(700, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (token.IsCancellationRequested || _currentSessionId != sessionId) return;
            BeginStreamingAfterPrepare(capture, title, playLocal, sessionId);
        }, token);
    }

    private void BeginStreamingAfterPrepare(
        IAudioCapture capture, string title, bool playLocal, Guid sessionId)
    {
        if (_currentSessionId != sessionId || _disposed) return;

        double lead = Math.Max(_adaptiveLeadTime.RecommendedLeadTime, 1.4);
        _sessionLead = lead;
        _sessionStartHostPlayAt = HostTime.Now() + lead;

        Broadcast(new ControlPayload.StartPlayback(
            new StartPlaybackData(sessionId, _sessionStartHostPlayAt, lead)));

        _sending = true;
        _sendTask = Task.Run(SendLoop);
        _samplesHandler = samples => OnSamplesAvailable(samples, sessionId);
        capture.SamplesAvailable -= _samplesHandler;
        capture.SamplesAvailable += _samplesHandler;
        capture.ErrorOccurred += err =>
        {
            LastError = err;
            CurrentPhase = Phase.Error;
            StateChanged?.Invoke();
        };

        if (capture is FileAudioCapture fac)
        {
            _currentFileCapture = fac;
            fac.FileEnded += OnFileEnded;
        }

        if (playLocal)
        {
            _localPlayer = new LocalAudioPlayer(SyncProtocol.SampleRate, SyncProtocol.Channels);
            _localPlayer.StartAt(_sessionStartHostPlayAt, outputDeviceId: null, LocalSyncOffsetSeconds);
        }

        try
        {
            capture.Start();
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            CurrentPhase = Phase.Error;
            StatusText = "捕获启动失败";
            StateChanged?.Invoke();
            return;
        }

        CurrentSource = title;
        CurrentPhase = Phase.Playing;
        StatusText = $"播放中：{title}";
        StateChanged?.Invoke();
    }

    private void OnSamplesAvailable(ReadOnlyMemory<float> samples, Guid sessionId)
    {
        if (_currentSessionId != sessionId) return;
        if (_paused) return;

        Interlocked.Add(ref SamplesSent, samples.Length);
        _localPlayer?.Enqueue(samples.Span);

        var span = samples.Span;
        float peak = 0;
        for (int i = 0; i < span.Length; i++)
        {
            var a = Math.Abs(span[i]);
            if (a > peak) peak = a;
        }
        if (peak > _maxSample) _maxSample = peak;

        const int chunkSize = 4096;
        var arr = samples.ToArray();
        int offset = 0;
        while (offset < arr.Length)
        {
            if (_currentSessionId != sessionId) return;
            int take = Math.Min(chunkSize, arr.Length - offset);
            var pcm = new byte[take * sizeof(float)];
            Buffer.BlockCopy(arr, offset * sizeof(float), pcm, 0, pcm.Length);
            offset += take;

            var header = new AudioChunkHeader(
                sessionId,
                _liveSequence,
                _liveSampleIndex,
                (uint)take,
                _sessionStartHostPlayAt + _liveSampleIndex / _liveSampleRate);
            _liveSequence++;
            _liveSampleIndex += (ulong)take;

            _sendQueue.Enqueue((header, pcm));
        }
    }

    private async Task SendLoop()
    {
        while (_sending)
        {
            if (_paused)
            {
                while (_sendQueue.TryDequeue(out _)) { }
                Thread.Sleep(20);
                continue;
            }

            if (!_sendQueue.TryDequeue(out var item))
            {
                Thread.Sleep(5);
                continue;
            }

            // Keep 1.1–1.3s buffered on speakers (Mac file + synced system-audio path).
            double targetBuffered = Math.Clamp(_sessionLead * 0.9, 1.1, 1.3);
            double wait = item.Header.HostPlayAt - HostTime.Now() - targetBuffered;
            if (wait > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(wait * 1000));

            if (!_sending) break;

            List<SyncConnection> snapshot;
            lock (_lock)
            {
                snapshot = _audioConnections.Count > 0
                    ? _audioConnections.ToList()
                    : _connections.ToList(); // legacy single-TCP fallback
            }

            // Independent send per speaker so one slow TCP doesn't stall the others.
            var tasks = snapshot.Select(async conn =>
            {
                try { await conn.SendAudioAsync(item.Header, item.Pcm); } catch { }
            });
            await Task.WhenAll(tasks);
        }
    }

    private void OnFileEnded()
    {
        if (!Playlist.AutoAdvance) return;
        var next = Playlist.MoveToNext();
        if (next == null)
        {
            StatusText = "播放列表已结束";
            StateChanged?.Invoke();
            return;
        }
        StateChanged?.Invoke();
        Task.Run(() =>
        {
            Thread.Sleep(200);
            PlayPlaylistTrack(next.FilePath, next.Title, _playLocal);
        });
    }

    public void PausePlayback()
    {
        if (CurrentPhase != Phase.Playing || _paused) return;
        _paused = true;
        while (_sendQueue.TryDequeue(out _)) { }
        StatusText = $"已暂停：{CurrentSource}";
        StateChanged?.Invoke();
    }

    public void ResumePlayback()
    {
        if (_paused && CurrentPhase == Phase.Playing)
        {
            _paused = false;
            StatusText = $"播放中：{CurrentSource}";
            StateChanged?.Invoke();
        }
    }

    private void ApplyLocalMute()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            _savedMute = device.AudioEndpointVolume.Mute;
            _hasSavedMute = true;
            device.AudioEndpointVolume.Mute = true;
        }
        catch { }
    }

    private void RestoreLocalMute()
    {
        if (!_hasSavedMute) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            device.AudioEndpointVolume.Mute = _savedMute;
        }
        catch { }
        _hasSavedMute = false;
    }

    public void StopPlayback()
    {
        try { _prepareCts?.Cancel(); } catch { }
        try { _prepareCts?.Dispose(); } catch { }
        _prepareCts = null;

        if (_currentSessionId.HasValue)
            Broadcast(new ControlPayload.StopPlayback(_currentSessionId.Value));

        RestoreLocalMute();
        _sending = false;
        _paused = false;
        _streamingSystemAudio = false;
        _sessionIsSystemAudio = false;
        try { _sendTask?.Wait(500); } catch { }
        _sendTask = null;
        while (_sendQueue.TryDequeue(out _)) { }
        try { _localPlayer?.Stop(); } catch { }
        _localPlayer = null;
        // Restore Windows default output after virtual-cable routing (Mac restores BlackHole too).
        try { _virtualRouter?.Dispose(); } catch { }
        _virtualRouter = null;
        if (_currentFileCapture != null)
        {
            try { _currentFileCapture.FileEnded -= OnFileEnded; } catch { }
            _currentFileCapture = null;
        }
        try { if (_capture != null && _samplesHandler != null) _capture.SamplesAvailable -= _samplesHandler; } catch { }
        _samplesHandler = null;
        try { _capture?.Stop(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        _currentSessionId = null;
        _liveSequence = 0;
        _liveSampleIndex = 0;

        if (CurrentPhase == Phase.Playing)
        {
            bool hasConnections;
            lock (_lock) hasConnections = _connections.Count > 0;
            CurrentSource = "未播放";
            CurrentPhase = hasConnections ? Phase.Ready : Phase.Discoverable;
            StatusText = "已停止";
            StateChanged?.Invoke();
        }
    }

    private void Broadcast(ControlPayload payload)
    {
        List<SyncConnection> snapshot;
        lock (_lock) snapshot = _connections.ToList();
        foreach (var conn in snapshot)
        {
            try { conn.SendControl(payload); } catch { }
        }
    }

    private string BuildCaptureInfo()
    {
        if (_capture == null) return "未在捕获";
        var parts = new List<string> { $"已发送 {SamplesSent / 1000}k 样本", $"峰值 {_maxSample:F3}" };
        if (_capture is WindowsLoopbackCapture wlc)
        {
            parts.Add($"捕获 {wlc.TotalBytesCaptured / 1024} KB · 原始峰值 {wlc.RawPeak:F3} · 输出峰值 {wlc.OutPeak:F3}");
            parts.Add(wlc.CaptureFormat);
        }
        else if (_capture is DemoToneCapture)
        {
            parts.Add("测试音源 440Hz");
        }
        return string.Join(" · ", parts);
    }

    public void Teardown()
    {
        StopPlayback();
        RestoreLocalMute();
        try { _mdnsBrowser.Dispose(); } catch { }
        List<Timer> timers;
        List<SyncConnection> conns;
        List<SyncConnection> audios;
        lock (_lock)
        {
            timers = _clockTimers.Values.ToList();
            _clockTimers.Clear();
            conns = _connections.ToList();
            audios = _audioConnections.ToList();
            _connections.Clear();
            _audioConnections.Clear();
            _audioByControl.Clear();
            _controlByAudio.Clear();
            _synchronizers.Clear();
            _speakerIdByConn.Clear();
            _controlBySpeakerId.Clear();
            ConnectedSpeakers.Clear();
        }
        foreach (var t in timers) t.Dispose();
        foreach (var c in audios) try { c.Dispose(); } catch { }
        foreach (var c in conns) try { c.Dispose(); } catch { }
        _listener?.Dispose();
        _listener = null;
        CurrentPhase = Phase.Idle;
        StatusText = "已关闭";
        StateChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Teardown();
    }
}
