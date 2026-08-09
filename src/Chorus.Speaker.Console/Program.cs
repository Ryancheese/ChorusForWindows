using Chorus.Speaker;

Console.Title = "Chorus Speaker (Windows 控制台)";
Console.WriteLine("=== Chorus Speaker (Windows) ===");
Console.WriteLine("广播模式：本机作为 Speaker，等 Host 主动连接。");
Console.WriteLine("提示：同一台电脑不要同时开 Host 与 Speaker（端口 17482）。");
Console.WriteLine();

using var session = new SpeakerSession();
session.StateChanged += () =>
{
    var rtt = session.RTT.HasValue ? $"  RTT={(int)(session.RTT * 1000)}ms" : "";
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{session.Phase}] {session.Status}{rtt}");
};

session.StartAdvertising();

Console.WriteLine();
Console.WriteLine("正在广播… Host 端会发现此设备并主动连接。");
Console.WriteLine("也可以输入 Host IP 手动连接 Host（备用）：直接输入 IP 回车");
Console.WriteLine("按 Enter 退出。");

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

while (true)
{
    var line = Console.ReadLine();
    if (line == null) break;
}
