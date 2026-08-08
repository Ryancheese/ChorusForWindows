# ChorusNet — Windows 版 Chorus Host

多设备音频同步的 Windows 主机：把电脑音频（文件 / 测试音 / WASAPI 系统环回）同步推到局域网 Speaker（iPhone / iPad；仓库内另有 Windows 控制台 Speaker 供联调）。

协议层 `ChorusCore` 与 Mac 版 Swift `ChorusCore` **字节级兼容**，连接模型为 **双 TCP**（控制 + 音频 + `audioChannelHello`），可对接现有 iOS Speaker。

## 技术栈

| 层 | 选型 |
|----|------|
| UI | Avalonia 11 + Semi.Avalonia（玻璃风布局对齐 mac Host） |
| 音频捕获 | WASAPI Loopback / 文件 / 测试音（NAudio） |
| 本机播放 | NAudio WaveOutEvent |
| 服务发现 | 自研 mDNS Browser（Speaker 广播，Host 浏览） |
| 协议 | 双 TCP + JSON 控制 + 二进制音频帧 + NTP 风格时钟 |
| 框架 | .NET 8 |

## 项目结构

```
ChorusNet/
├── ChorusNet.slnx
└── src/
    ├── ChorusCore/       # 协议 / 网络 / mDNS / 时钟
    ├── ChorusAudio/      # WASAPI / 文件 / 测试音 / 本机播放
    ├── Chorus.Host/      # Avalonia Host
    └── Chorus.Speaker/   # 控制台 Speaker（联调）
```

## 编译运行

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download)。

```bash
dotnet build src/Chorus.Host/Chorus.Host.csproj
dotnet run --project src/Chorus.Host/Chorus.Host.csproj
```

联调 Speaker：

```bash
dotnet run --project src/Chorus.Speaker/Chorus.Speaker.csproj
```

## 使用流程

1. 在 iPhone/iPad 打开 Chorus Speaker 并开始广播（或启动本仓库 `Chorus.Speaker`）
2. 启动 Windows Host：自动浏览 `_chorus._tcp`，或手动输入手机 IP:17482
3. 点「连接」——Host 建立 **两条 TCP**（控制 + 音频），音频通道发送 `audioChannelHello`
4. 校准进入就绪后：测试音调 / 选择音频 / 统一转播系统声音

请与手机同一 Wi‑Fi，关闭 VPN；公司/访客网常有客户端隔离，请改用个人热点。

## 协议兼容性（与 Mac 版）

- 服务名 `_chorus._tcp`，端口 `17482`
- **双 TCP**：控制通道传 hello/welcome/clock/prepare/start/stop；音频通道先发 `audioChannelHello`，再传 PCM
- JSON 字段名一致（`pingID` / `sessionID` / `deviceID`）
- `DeviceRole` 线格式为小写 `host` / `speaker`
- 音频帧 `[1B type=8][4B headerLen BE][header JSON][PCM Float32 LE mono @ 44.1 kHz]`
- TCP 分帧 `[4B length BE][payload]`

## 与 Mac 版的差异

| 项 | Mac 版 | Windows 版 |
|----|--------|-----------|
| 系统音频 | BlackHole 虚拟声卡 | WASAPI Loopback（无需虚拟驱动） |
| 连接模型 | 双 TCP | 双 TCP（已对齐） |
| UI | SwiftUI 液态玻璃 | Avalonia 玻璃风近似 |
| 时钟基准 | `ProcessInfo.systemUptime` | `Stopwatch` |
| 服务发现 | Bonjour | 自研 mDNS |

## 已实现

- [x] 双 TCP + `audioChannelHello`（对齐 iOS Speaker）
- [x] 时钟校准 + 自适应 lead
- [x] 文件 / 测试音 / 系统环回推流
- [x] 本机同时播放、播放列表
- [x] mDNS 浏览 + 手动 IP
- [x] Host UI 对齐 mac 三段式布局（状态 / 设备 / 播放+列表）

## 系统声音同步转播

与 Mac 端 BlackHole 同思路：需安装免费虚拟声卡 [VB-Audio Virtual Cable](https://vb-audio.com/Cable/)。Chorus 会把系统默认输出切到 `CABLE Input`，环回采集后再按同一 `hostPlayAt` 播放到本机音箱与手机；停止后恢复原设备。

## 已知限制

- 本机播放为同流镜像，调度精度弱于 CoreAudio `hostPlayAt`
- 暂停为本地丢弃采样，非协议级 pause
- DRM 流可能采不到（与 macOS 同类限制）
- 未安装虚拟声卡时无法做 Mac 式双方同延的系统转播
