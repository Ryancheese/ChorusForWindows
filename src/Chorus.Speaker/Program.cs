using Chorus.Speaker;

Console.Title = "Chorus Speaker (Windows 测试)";
Console.WriteLine("=== Chorus Speaker (Windows) ===");
Console.WriteLine("广播模式：本机作为 Speaker，等 Host 主动连接。");
Console.WriteLine();

using var session = new SpeakerSession();
session.StateChanged += () =>
{
    var rtt = session.RTT.HasValue ? $"  RTT={(int)(session.RTT * 1000)}ms" : "";
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {session.Status}{rtt}");
};

// 启动广播模式：广播 _chorus._tcp + 监听端口等 Host 连
session.StartAdvertising();

Console.WriteLine();
Console.WriteLine("正在广播… Host 端会发现此设备并主动连接。");
Console.WriteLine("也可以输入 Host IP 手动连接 Host（备用）：直接输入 IP 回车");
Console.WriteLine("按 Enter 退出。");

// 后台读取：如果用户输入了 IP，切换到主动连接模式
_ = Task.Run(() =>
{
    var input = Console.ReadLine();
    if (!string.IsNullOrWhiteSpace(input))
    {
        try
        {
            session.Connect(input.Trim());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"手动连接失败：{ex.Message}");
        }
    }
});

// 主线程等待退出信号
while (true)
{
    var line = Console.ReadLine();
    if (line == null) break;
}
