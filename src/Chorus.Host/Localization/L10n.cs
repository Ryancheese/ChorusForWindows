using System.Globalization;

namespace Chorus.Host.Localization;

/// <summary>Lightweight i18n aligned with Mac Chorus L10n keys (en / zh-Hans / ja / ko).</summary>
public static class L10n
{
    public const string PrefKey = "chorus.language";
    public static readonly string[] Supported = ["system", "zh-Hans", "en", "ja", "ko"];

    private static string _selection = "system";

    public static event Action? LanguageChanged;

    public static string Selection
    {
        get => _selection;
        set
        {
            var next = Supported.Contains(value) ? value : "system";
            if (_selection == next) return;
            _selection = next;
            try { File.WriteAllText(PrefPath(), next); } catch { }
            LanguageChanged?.Invoke();
        }
    }

    public static string ActiveCode
    {
        get
        {
            if (_selection != "system") return _selection;
            var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return lang switch
            {
                "zh" => "zh-Hans",
                "ja" => "ja",
                "ko" => "ko",
                "en" => "en",
                _ => "zh-Hans",
            };
        }
    }

    public static void LoadPreference()
    {
        try
        {
            var path = PrefPath();
            if (File.Exists(path))
            {
                var v = File.ReadAllText(path).Trim();
                if (Supported.Contains(v)) _selection = v;
            }
        }
        catch { }
    }

    private static Dictionary<string, string> ActiveTable => ActiveCode switch
    {
        "en" => En,
        "ja" => Ja,
        "ko" => Ko,
        _ => ZhHans,
    };

    public static string T(string key)
    {
        if (ActiveTable.TryGetValue(key, out var s)) return s;
        if (ZhHans.TryGetValue(key, out var fallback)) return fallback;
        if (En.TryGetValue(key, out var en)) return en;
        return key;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(key), args);

    public static string CycleSelection()
    {
        int i = Array.IndexOf(Supported, _selection);
        Selection = Supported[(i + 1) % Supported.Length];
        return SelectionDisplay;
    }

    public static string SelectionDisplay => _selection switch
    {
        "system" => T("appearance.system"),
        "zh-Hans" => "简体中文",
        "en" => "English",
        "ja" => "日本語",
        "ko" => "한국어",
        _ => _selection,
    };

    private static string PrefPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChorusHost", "language.txt");

    private static readonly Dictionary<string, string> ZhHans = new()
    {
        ["host.tagline"] = "把 Windows 的声音，同步到身边的 iPhone 与 iPad",
        ["action.help"] = "使用帮助",
        ["action.language"] = "语言",
        ["action.appearance"] = "外观",
        ["action.close"] = "关闭",
        ["action.connect"] = "连接",
        ["action.connect.all"] = "全部连接",
        ["action.disconnect"] = "断开",
        ["action.remove"] = "移除",
        ["action.choose.audio"] = "选择音频",
        ["action.choose.folder"] = "选择文件夹",
        ["action.test.tone"] = "测试音调",
        ["action.sync.play"] = "同步播放",
        ["action.pause"] = "暂停",
        ["action.resume"] = "继续",
        ["action.stop"] = "停止",
        ["action.stream.system.start"] = "统一转播系统声音",
        ["action.stream.system.stop"] = "停止统一转播",
        ["action.playlist.clear"] = "清空列表",
        ["appearance.system"] = "跟随系统",
        ["appearance.light"] = "白天",
        ["appearance.dark"] = "黑夜",
        ["phase.idle"] = "未开始",
        ["phase.discoverable"] = "可被发现",
        ["phase.connected"] = "已连接",
        ["phase.calibrating"] = "校准时钟",
        ["phase.ready"] = "就绪",
        ["phase.playing"] = "播放中",
        ["phase.error"] = "错误",
        ["section.nearby"] = "附近扬声器",
        ["section.manual.connect"] = "手动连接",
        ["section.session"] = "已加入会话",
        ["section.playback"] = "播放",
        ["section.playlist"] = "播放列表",
        ["toggle.play.locally"] = "本机同时播放",
        ["toggle.auto.next"] = "播完自动下一首",
        ["hint.discovery"] = "自动发现失败时，请用下方手动连接。公司/访客 Wi‑Fi 常会屏蔽组播或隔离客户端，可改用个人热点。",
        ["hint.connect"] = "连接一台设备后即可同步播放",
        ["hint.playlist.empty"] = "从左侧选择音频或文件夹加入列表",
        ["hint.host.local.ip"] = "本机局域网 IP：{0}（请确认与手机同一网段）",
        ["field.phone.ip"] = "手机 IP，如 192.168.1.8",
        ["field.port"] = "端口",
        ["status.searching"] = "正在搜索附近扬声器…",
        ["status.devices.found"] = "已发现 {0} 台设备",
        ["status.connecting.all"] = "正在连接 {0} 台扬声器…",
        ["status.ready"] = "就绪",
        ["playlist.now.playing"] = "当前：",
        ["sync.trim"] = "本机同步微调",
        ["sync.trim.hint"] = "若本机比手机早，把滑块往右（延后本机）；若本机偏晚，往左。调完后重新点转播/同步播放生效。",
        ["tip.prev"] = "上一首",
        ["tip.next"] = "下一首",
        ["dialog.choose.audio"] = "选择音频文件",
        ["dialog.audio.files"] = "音频文件",
        ["dialog.all.files"] = "所有文件",
        ["dialog.choose.folder"] = "选择音乐文件夹",
        ["help.title"] = "使用帮助",
        ["help.body"] =
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
            · 需先安装 VB-Audio Virtual Cable（免费，等同 Mac 的 BlackHole）：https://vb-audio.com/Cable/
            · 点「统一转播系统声音」会把系统输出切到虚拟声卡，再按同一时间线播放到电脑音箱与手机
            · 准备阶段约 2 秒（切换设备 + 扬声器引擎），双方会一起延迟约 1.2–1.5 秒以保持同步
            · 停止转播后自动恢复原来的默认播放设备
            · 受 DRM 保护的内容可能采不到

            常见问题
            · 发现不到设备：改用个人热点或手动 IP
            · 已连接无声音：确认 Speaker 已允许本地网络，并已进入就绪
            """,
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["host.tagline"] = "Sync Windows audio to nearby iPhone and iPad speakers",
        ["action.help"] = "Help",
        ["action.language"] = "Language",
        ["action.appearance"] = "Appearance",
        ["action.close"] = "Close",
        ["action.connect"] = "Connect",
        ["action.connect.all"] = "Connect all",
        ["action.disconnect"] = "Disconnect",
        ["action.remove"] = "Remove",
        ["action.choose.audio"] = "Choose audio",
        ["action.choose.folder"] = "Choose folder",
        ["action.test.tone"] = "Test tone",
        ["action.sync.play"] = "Play in sync",
        ["action.pause"] = "Pause",
        ["action.resume"] = "Resume",
        ["action.stop"] = "Stop",
        ["action.stream.system.start"] = "Stream system audio",
        ["action.stream.system.stop"] = "Stop system audio",
        ["action.playlist.clear"] = "Clear playlist",
        ["appearance.system"] = "System",
        ["appearance.light"] = "Light",
        ["appearance.dark"] = "Dark",
        ["phase.idle"] = "Not started",
        ["phase.discoverable"] = "Discoverable",
        ["phase.connected"] = "Connected",
        ["phase.calibrating"] = "Calibrating clock",
        ["phase.ready"] = "Ready",
        ["phase.playing"] = "Playing",
        ["phase.error"] = "Error",
        ["section.nearby"] = "Nearby speakers",
        ["section.manual.connect"] = "Manual connection",
        ["section.session"] = "Session",
        ["section.playback"] = "Playback",
        ["section.playlist"] = "Playlist",
        ["toggle.play.locally"] = "Play on this PC too",
        ["toggle.auto.next"] = "Auto-play next track",
        ["hint.discovery"] = "If discovery fails, connect manually below. Guest/corporate Wi‑Fi often blocks multicast or isolates clients — try a personal hotspot.",
        ["hint.connect"] = "Connect a device to enable synchronized playback",
        ["hint.playlist.empty"] = "Choose audio or a folder on the left to build a playlist",
        ["hint.host.local.ip"] = "This PC’s LAN IP: {0} (same subnet as the phone)",
        ["field.phone.ip"] = "Phone IP, e.g. 192.168.1.8",
        ["field.port"] = "Port",
        ["status.searching"] = "Searching for nearby speakers…",
        ["status.devices.found"] = "Found {0} device(s)",
        ["status.connecting.all"] = "Connecting {0} speakers…",
        ["status.ready"] = "Ready",
        ["playlist.now.playing"] = "Now:",
        ["sync.trim"] = "Local sync trim",
        ["sync.trim.hint"] = "If the PC is ahead of the phone, drag right (delay PC). If the PC is late, drag left. Restart streaming/play to apply.",
        ["tip.prev"] = "Previous",
        ["tip.next"] = "Next",
        ["dialog.choose.audio"] = "Choose audio files",
        ["dialog.audio.files"] = "Audio files",
        ["dialog.all.files"] = "All files",
        ["dialog.choose.folder"] = "Choose music folder",
        ["help.title"] = "Help",
        ["help.body"] =
            """
            Connect a speaker
            · Open Chorus Speaker on iPhone/iPad and start broadcasting
            · Host discovers devices automatically; tap Connect for dual TCP
            · If discovery fails, enter the phone IP and port 17482
            · Stay on the same Wi‑Fi; turn off VPN

            Play in sync
            · Choose audio or a folder, or use Test tone
            · Optionally enable Play on this PC too
            · Tap Play in sync to stream on one timeline

            Stream system audio
            · Install free VB-Audio Virtual Cable first (Mac equivalent of BlackHole): https://vb-audio.com/Cable/
            · Chorus switches the system output to the cable, then plays the mix to your PC speakers and phones on one timeline
            · Prep takes ~2s; both sides share a ~1.2–1.5s delay so they stay in sync
            · Stopping restores your previous default playback device
            · DRM-protected content may not be capturable

            Troubleshooting
            · No devices: try a personal hotspot or manual IP
            · Connected but silent: allow Local Network on the Speaker
            """,
    };

    private static readonly Dictionary<string, string> Ja = new()
    {
        ["host.tagline"] = "Windows の音声を近くの iPhone / iPad に同期",
        ["action.help"] = "ヘルプ",
        ["action.language"] = "言語",
        ["action.appearance"] = "外観",
        ["action.close"] = "閉じる",
        ["action.connect"] = "接続",
        ["action.connect.all"] = "すべて接続",
        ["action.disconnect"] = "切断",
        ["action.remove"] = "削除",
        ["action.choose.audio"] = "音声を選択",
        ["action.choose.folder"] = "フォルダを選択",
        ["action.test.tone"] = "テスト音",
        ["action.sync.play"] = "同期再生",
        ["action.pause"] = "一時停止",
        ["action.resume"] = "再開",
        ["action.stop"] = "停止",
        ["action.stream.system.start"] = "システム音声を配信",
        ["action.stream.system.stop"] = "システム音声を停止",
        ["action.playlist.clear"] = "リストをクリア",
        ["appearance.system"] = "システムに合わせる",
        ["appearance.light"] = "ライト",
        ["appearance.dark"] = "ダーク",
        ["phase.idle"] = "未開始",
        ["phase.discoverable"] = "検出可能",
        ["phase.connected"] = "接続済み",
        ["phase.calibrating"] = "時計校正中",
        ["phase.ready"] = "準備完了",
        ["phase.playing"] = "再生中",
        ["phase.error"] = "エラー",
        ["section.nearby"] = "近くのスピーカー",
        ["section.manual.connect"] = "手動接続",
        ["section.session"] = "セッション",
        ["section.playback"] = "再生",
        ["section.playlist"] = "プレイリスト",
        ["toggle.play.locally"] = "この PC でも再生",
        ["toggle.auto.next"] = "終了後に次へ",
        ["hint.discovery"] = "自動検出に失敗したら下で手動接続してください。ゲスト/会社 Wi‑Fi はマルチキャストや端末間通信を制限することがあります。",
        ["hint.connect"] = "デバイスを接続すると同期再生できます",
        ["hint.playlist.empty"] = "左から音声やフォルダを選んでリストに追加",
        ["hint.host.local.ip"] = "この PC の LAN IP：{0}（スマホと同じサブネット）",
        ["field.phone.ip"] = "スマホ IP（例 192.168.1.8）",
        ["field.port"] = "ポート",
        ["status.searching"] = "近くのスピーカーを検索中…",
        ["status.devices.found"] = "{0} 台見つかりました",
        ["status.connecting.all"] = "{0} 台のスピーカーに接続中…",
        ["status.ready"] = "準備完了",
        ["playlist.now.playing"] = "再生中：",
        ["sync.trim"] = "ローカル同期微調整",
        ["sync.trim.hint"] = "PC が早い場合は右へ（PC を遅らせる）。遅い場合は左へ。変更後は配信/再生をやり直してください。",
        ["tip.prev"] = "前へ",
        ["tip.next"] = "次へ",
        ["dialog.choose.audio"] = "音声ファイルを選択",
        ["dialog.audio.files"] = "音声ファイル",
        ["dialog.all.files"] = "すべてのファイル",
        ["dialog.choose.folder"] = "音楽フォルダを選択",
        ["help.title"] = "ヘルプ",
        ["help.body"] = En["help.body"],
    };

    private static readonly Dictionary<string, string> Ko = new()
    {
        ["host.tagline"] = "Windows 소리를 근처 iPhone/iPad와 동기화",
        ["action.help"] = "도움말",
        ["action.language"] = "언어",
        ["action.appearance"] = "모양",
        ["action.close"] = "닫기",
        ["action.connect"] = "연결",
        ["action.connect.all"] = "모두 연결",
        ["action.disconnect"] = "연결 해제",
        ["action.remove"] = "제거",
        ["action.choose.audio"] = "오디오 선택",
        ["action.choose.folder"] = "폴더 선택",
        ["action.test.tone"] = "테스트 톤",
        ["action.sync.play"] = "동기 재생",
        ["action.pause"] = "일시정지",
        ["action.resume"] = "계속",
        ["action.stop"] = "중지",
        ["action.stream.system.start"] = "시스템 소리 전송",
        ["action.stream.system.stop"] = "시스템 소리 중지",
        ["action.playlist.clear"] = "목록 비우기",
        ["appearance.system"] = "시스템",
        ["appearance.light"] = "라이트",
        ["appearance.dark"] = "다크",
        ["phase.idle"] = "시작 전",
        ["phase.discoverable"] = "검색 가능",
        ["phase.connected"] = "연결됨",
        ["phase.calibrating"] = "시계 보정",
        ["phase.ready"] = "준비됨",
        ["phase.playing"] = "재생 중",
        ["phase.error"] = "오류",
        ["section.nearby"] = "근처 스피커",
        ["section.manual.connect"] = "수동 연결",
        ["section.session"] = "세션",
        ["section.playback"] = "재생",
        ["section.playlist"] = "재생목록",
        ["toggle.play.locally"] = "이 PC에서도 재생",
        ["toggle.auto.next"] = "끝나면 다음 곡",
        ["hint.discovery"] = "자동 검색이 안 되면 아래에서 수동 연결하세요. 회사/게스트 Wi‑Fi는 멀티캐스트나 기기 간 통신을 막을 수 있습니다.",
        ["hint.connect"] = "기기를 연결하면 동기 재생을 할 수 있습니다",
        ["hint.playlist.empty"] = "왼쪽에서 오디오나 폴더를 선택해 목록에 추가",
        ["hint.host.local.ip"] = "이 PC LAN IP: {0} (휴대폰과 같은 서브넷)",
        ["field.phone.ip"] = "휴대폰 IP, 예: 192.168.1.8",
        ["field.port"] = "포트",
        ["status.searching"] = "근처 스피커 검색 중…",
        ["status.devices.found"] = "기기 {0}대 발견",
        ["status.connecting.all"] = "스피커 {0}대 연결 중…",
        ["status.ready"] = "준비됨",
        ["playlist.now.playing"] = "현재:",
        ["sync.trim"] = "로컬 동기 미세조정",
        ["sync.trim.hint"] = "PC가 더 빠르면 오른쪽으로(PC 지연). 더 느리면 왼쪽으로. 변경 후 전송/재생을 다시 시작하세요.",
        ["tip.prev"] = "이전",
        ["tip.next"] = "다음",
        ["dialog.choose.audio"] = "오디오 파일 선택",
        ["dialog.audio.files"] = "오디오 파일",
        ["dialog.all.files"] = "모든 파일",
        ["dialog.choose.folder"] = "음악 폴더 선택",
        ["help.title"] = "도움말",
        ["help.body"] = En["help.body"],
    };
}
