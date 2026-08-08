using System.Net.Sockets;
using System.Threading;
using ChorusAudio.Playback;
using ChorusCore.Network;
using ChorusCore.Network.Mdns;
using ChorusCore.Protocol;
using ChorusCore.Sync;

namespace Chorus.Speaker;

/// <summary>
/// Speaker-side session for testing the Windows Host. Accepts dual TCP (control + audio)
/// like iOS Speaker: PCM is only handled on the connection that sent <c>audioChannelHello</c>.
/// Falls back to single-TCP multiplex for older hosts.
/// </summary>
public sealed class SpeakerSession : IDisposable
{
    private readonly DeviceInfo _localDevice;
    private SyncConnection? _control;
    private SyncConnection? _audio;
    private double _clockOffset;
    private AudioJitterBuffer? _jitter;
    private LocalAudioPlayer? _player;
    private Guid? _sessionId;
    private double _sessionSampleRate = SyncProtocol.SampleRate;
    private double _hostPlayAt;
    private volatile bool _playStarted;
    private Thread? _scheduleThread;
    private volatile bool _disposed;

    private MdnsAdvertiser? _advertiser;
    private HostListener? _listener;
    private volatile bool _isAdvertisingMode;

    private string? _host;
    private ushort _port;
    private int _reconnectAttempts;
    private volatile bool _reconnecting;
    private const int MaxReconnectAttempts = 5;
    private const int ReconnectDelayMs = 3000;

    private long _framesReceived;
    private long _bytesReceived;
    private long _bytesEnqueued;
    private float _peakSample;

    public string Status { get; private set; } = "未连接";
    public double? RTT { get; private set; }
    public event Action? StateChanged;

    public SpeakerSession()
    {
        _localDevice = new DeviceInfo(
            Guid.NewGuid().ToString(),
            Environment.MachineName + "-Speaker",
            DeviceRole.Speaker);
    }

    public void StartAdvertising(ushort port = SyncBonjour.ControlPort)
    {
        _isAdvertisingMode = true;
        _port = port;
        _listener = new HostListener();
        _listener.ConnectionAccepted += OnInboundConnection;
        _listener.Start(port);

        var ip = LocalAddress.PrimaryIPv4();
        if (!string.IsNullOrEmpty(ip) && System.Net.IPAddress.TryParse(ip, out var addr))
        {
            var instance = $"Chorus-Speaker-{Environment.MachineName}";
            _advertiser = new MdnsAdvertiser(instance, Environment.MachineName, addr, _listener.ListeningPort);
            _advertiser.ErrorOccurred += err => Log($"mDNS 广播错误: {err}");
            _advertiser.Start();
        }

        Status = $"正在广播… 本机 {ip}:{_listener.ListeningPort}，等 Host 连入";
        Log(Status);
        StateChanged?.Invoke();
    }

    private void OnInboundConnection(SyncConnection conn)
    {
        if (_control == null)
        {
            _control = conn;
            conn.Start(evt => HandleControlEvent(evt, conn));
            Status = "Host 控制通道已连入，握手中…";
            Log(Status);
            StateChanged?.Invoke();
            return;
        }

        if (_audio == null)
        {
            // Provisional audio socket — confirmed by audioChannelHello.
            _audio = conn;
            conn.Start(evt => HandleAudioEvent(evt, conn));
            Log("第二条 TCP 已连入（待 audioChannelHello）");
            return;
        }

        Log("忽略多余入站连接");
        try { conn.Dispose(); } catch { }
    }

    public void Connect(string host, ushort port = SyncBonjour.ControlPort)
    {
        _isAdvertisingMode = false;
        _host = host;
        _port = port;
        ConnectInternal(host, port);
    }

    private void ConnectInternal(string host, ushort port)
    {
        // Legacy single-TCP dial to Host (multiplexed). Prefer advertising + Host dialing in.
        var client = new TcpClient();
        client.Connect(host, port);
        _control = new SyncConnection(client, $"{host}:{port}");
        _audio = null;
        var current = _control;
        _control.Start(evt => HandleControlEvent(evt, current));
        Status = "已连接，握手中…";
        Log(Status);
        StateChanged?.Invoke();
    }

    private void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

    private void HandleControlEvent(SyncConnectionEvent evt, SyncConnection conn)
    {
        if (conn != _control) return;

        switch (evt)
        {
            case SyncConnectionEvent.Connected:
                // When we dial the Host, announce ourselves. When Host dials us,
                // wait for their Hello and reply with Welcome (iOS Speaker behavior).
                if (!_isAdvertisingMode)
                    _control!.SendControl(new ControlPayload.Hello(_localDevice));
                break;
            case SyncConnectionEvent.Control c:
                if (c.Payload is ControlPayload.AudioChannelHello)
                {
                    // Host mistakenly sent hello on control — treat control as audio too (compat).
                    _audio ??= conn;
                    Log("在控制通道收到 audioChannelHello（单连接兼容）");
                    break;
                }
                HandleControl(c.Payload);
                break;
            case SyncConnectionEvent.Audio a:
                // Legacy multiplex: audio on control when no dedicated audio channel.
                if (_audio == null || ReferenceEquals(_audio, conn))
                    HandleAudio(a.Header, a.Pcm);
                break;
            case SyncConnectionEvent.Disconnected d:
                OnControlDisconnected(d.Reason);
                break;
        }
    }

    private void HandleAudioEvent(SyncConnectionEvent evt, SyncConnection conn)
    {
        if (conn != _audio) return;

        switch (evt)
        {
            case SyncConnectionEvent.Control c when c.Payload is ControlPayload.AudioChannelHello:
                Log($"音频通道已确认（audioChannelHello）");
                Status = "双通道已就绪";
                StateChanged?.Invoke();
                break;
            case SyncConnectionEvent.Audio a:
                HandleAudio(a.Header, a.Pcm);
                break;
            case SyncConnectionEvent.Disconnected d:
                Log($"音频通道断开：{d.Reason ?? "未知"}");
                _audio = null;
                // Tear down control as well — matches iOS pairing semantics.
                var control = _control;
                _control = null;
                try { control?.Dispose(); } catch { }
                OnSessionLost("音频通道已断开");
                break;
            case SyncConnectionEvent.Connected:
            case SyncConnectionEvent.Control:
                break;
        }
    }

    private void OnControlDisconnected(string? reason)
    {
        _playStarted = false;
        try { _player?.Stop(); } catch { }
        _player = null;
        _jitter?.Reset();
        try { _audio?.Dispose(); } catch { }
        _audio = null;
        _control = null;
        OnSessionLost(reason);
    }

    private void OnSessionLost(string? reason)
    {
        Status = $"断开：{reason ?? "未知"}";
        Log(Status);
        StateChanged?.Invoke();

        if (_isAdvertisingMode)
        {
            _clockOffset = 0;
            _sessionId = null;
            Log("重新进入广播模式…");
            try { _advertiser?.Dispose(); } catch { }
            try { _listener?.Dispose(); } catch { }
            _advertiser = null;
            _listener = null;
            Thread.Sleep(1000);
            if (!_disposed) StartAdvertising(_port);
        }
        else
        {
            TryReconnect();
        }
    }

    private void HandleControl(ControlPayload payload)
    {
        switch (payload)
        {
            case ControlPayload.Welcome w:
                Status = $"收到 Host：{w.Info.Name}";
                Log(Status);
                StateChanged?.Invoke();
                break;

            case ControlPayload.Hello h:
                // Host sent Hello — reply Welcome (Host-initiated connect).
                _control?.SendControl(new ControlPayload.Welcome(_localDevice));
                Status = $"收到 Host：{h.Info.Name}";
                Log(Status);
                StateChanged?.Invoke();
                break;

            case ControlPayload.ClockPing ping:
                double recv = HostTime.Now();
                double send = HostTime.Now();
                _control?.SendControl(new ControlPayload.ClockPong(
                    new ClockPongData(ping.Ping.PingId, ping.Ping.HostSendTime, recv, send)));
                break;

            case ControlPayload.ClockOffset co:
                _clockOffset = co.Seconds;
                Log($"时钟偏移更新：{co.Seconds:F3}s");
                break;

            case ControlPayload.PrepareSession ps:
                _sessionId = ps.Session.SessionId;
                _sessionSampleRate = ps.Session.SampleRate;
                _jitter = new AudioJitterBuffer(_sessionSampleRate);
                Status = $"准备会话：{ps.Session.Title}（{_sessionSampleRate} Hz）";
                Log(Status);
                StateChanged?.Invoke();
                break;

            case ControlPayload.StartPlayback sp:
                _hostPlayAt = sp.Start.HostPlayAt;
                Log($"StartPlayback hostPlayAt={_hostPlayAt:F2} offset={_clockOffset:F3} leadTime={sp.Start.LeadTime:F2}");
                StartPlaying();
                Status = "播放中";
                Log(Status);
                StateChanged?.Invoke();
                break;

            case ControlPayload.StopPlayback:
                StopPlaying();
                Status = "已停止";
                Log(Status);
                StateChanged?.Invoke();
                _control?.SendControl(new ControlPayload.StopAcknowledged(_sessionId ?? Guid.Empty));
                break;

            case ControlPayload.Goodbye:
                StopPlaying();
                Status = "Host 已断开";
                Log(Status);
                StateChanged?.Invoke();
                break;
        }
    }

    private void HandleAudio(AudioChunkHeader header, byte[] pcm)
    {
        if (_sessionId != header.SessionId || _jitter == null)
        {
            if (_framesReceived == 0)
                Log($"丢弃音频帧：sessionId 匹配={_sessionId == header.SessionId}, jitter={_jitter != null}");
            return;
        }
        Interlocked.Increment(ref _framesReceived);
        Interlocked.Add(ref _bytesReceived, pcm.Length);

        float peak = 0;
        for (int i = 0; i + 4 <= pcm.Length; i += 4)
        {
            float s = BitConverter.ToSingle(pcm, i);
            var a = Math.Abs(s);
            if (a > peak) peak = a;
        }
        if (peak > _peakSample) _peakSample = peak;

        var ready = _jitter.Append(header, pcm);
        foreach (var chunk in ready)
            EnqueueForPlayback(chunk.Pcm);

        if (_framesReceived % 500 == 1)
        {
            Log($"已收 {_framesReceived} 帧 / {_bytesReceived / 1024} KB，已喂播放器 {_bytesEnqueued / 1024} KB，本帧peak={peak:F4} 累计peak={_peakSample:F4} playStarted={_playStarted}");
        }
    }

    private void StartPlaying()
    {
        StopPlaying();
        _player = new LocalAudioPlayer(_sessionSampleRate, 1);
        _player.Start();
        _playStarted = false;

        _scheduleThread = new Thread(() =>
        {
            try
            {
                double localPlayAt = _hostPlayAt + _clockOffset;
                double wait = localPlayAt - HostTime.Now();
                if (wait > 0 && wait < 3)
                {
                    Log($"等待起播 {wait:F2}s 后开始喂播放器");
                    Thread.Sleep((int)(wait * 1000));
                }
                else if (wait >= 3)
                {
                    Log($"wait={wait:F2}s 过大，跳过等待立即起播（时钟可能未校准）");
                }
                _playStarted = true;
                Log("起播：playStarted=true，后续音频将喂给播放器");
            }
            catch (Exception ex) { Log($"调度线程异常：{ex.Message}"); }
        }) { IsBackground = true, Name = "speaker-schedule" };
        _scheduleThread.Start();
    }

    private void EnqueueForPlayback(byte[] pcm)
    {
        if (!_playStarted || _player == null) return;
        Interlocked.Add(ref _bytesEnqueued, pcm.Length);
        var samples = new float[pcm.Length / 4];
        Buffer.BlockCopy(pcm, 0, samples, 0, pcm.Length);
        _player.Enqueue(samples);
    }

    private void StopPlaying()
    {
        _playStarted = false;
        try { _player?.Stop(); } catch { }
        _player = null;
        _jitter?.Reset();
    }

    private void TryReconnect()
    {
        if (_disposed || _host == null) return;
        if (_reconnecting) return;
        _reconnecting = true;

        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            Status = $"重连失败（已尝试 {MaxReconnectAttempts} 次），按 Enter 退出";
            Log(Status);
            StateChanged?.Invoke();
            _reconnecting = false;
            return;
        }
        _reconnectAttempts++;
        Status = $"{ReconnectDelayMs / 1000} 秒后重连（第 {_reconnectAttempts}/{MaxReconnectAttempts} 次）…";
        Log(Status);
        StateChanged?.Invoke();

        Thread reconnectThread = new(() =>
        {
            Thread.Sleep(ReconnectDelayMs);
            if (_disposed || _host == null) { _reconnecting = false; return; }
            try
            {
                Log($"正在重连 {_host}:{_port} …");
                try { _control?.Dispose(); } catch { }
                try { _audio?.Dispose(); } catch { }
                _control = null;
                _audio = null;
                _clockOffset = 0;
                _sessionId = null;
                ConnectInternal(_host, _port);
                _reconnectAttempts = 0;
                _reconnecting = false;
            }
            catch (Exception ex)
            {
                Log($"重连失败：{ex.Message}");
                _reconnecting = false;
                TryReconnect();
            }
        }) { IsBackground = true, Name = "speaker-reconnect" };
        reconnectThread.Start();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopPlaying();
        try { _advertiser?.Dispose(); } catch { }
        try { _listener?.Dispose(); } catch { }
        try { _audio?.Dispose(); } catch { }
        try { _control?.Dispose(); } catch { }
    }
}
