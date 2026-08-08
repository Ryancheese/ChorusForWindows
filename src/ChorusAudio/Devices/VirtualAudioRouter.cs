using NAudio.CoreAudioApi;

namespace ChorusAudio.Devices;

/// <summary>
/// Mac BlackHole equivalent on Windows: find a virtual render device (VB-Cable /
/// VoiceMeeter), temporarily make it the system default so apps render into it,
/// loopback-capture that mix, and play the delayed stream on the real speakers.
/// </summary>
public sealed class VirtualAudioRouter : IDisposable
{
    private readonly string _previousDefaultId;
    private bool _activated;
    private bool _disposed;

    public string VirtualRenderId { get; }
    public string VirtualRenderName { get; }
    public string PhysicalRenderId { get; }
    public string PhysicalRenderName { get; }

    private VirtualAudioRouter(
        string virtualRenderId, string virtualRenderName,
        string physicalRenderId, string physicalRenderName,
        string previousDefaultId)
    {
        VirtualRenderId = virtualRenderId;
        VirtualRenderName = virtualRenderName;
        PhysicalRenderId = physicalRenderId;
        PhysicalRenderName = physicalRenderName;
        _previousDefaultId = previousDefaultId;
    }

    /// <summary>
    /// Returns null when no usable virtual cable is installed.
    /// </summary>
    public static VirtualAudioRouter? TryCreate()
    {
        using var enumerator = new MMDeviceEnumerator();
        MMDevice? previous;
        try { previous = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
        catch { return null; }

        using (previous)
        {
            var renders = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .ToList();

            // Prefer classic stereo "CABLE Input" over "CABLE In 16ch".
            var virtualRender = renders.FirstOrDefault(d =>
                                    d.FriendlyName.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase))
                                ?? renders.FirstOrDefault(d => IsVirtualRender(d.FriendlyName));
            if (virtualRender == null) return null;

            // Prefer the current default if it is a real speaker; otherwise first non-virtual.
            // Never treat another CABLE/VAIO endpoint as the "physical" restore target.
            MMDevice? physical = null;
            if (!IsVirtualRender(previous.FriendlyName))
                physical = renders.FirstOrDefault(d => d.ID == previous.ID);
            physical ??= renders.FirstOrDefault(d => !IsVirtualRender(d.FriendlyName));
            if (physical == null) return null;

            // If the user already had a cable as default, restore back to the real speakers.
            string previousId = IsVirtualRender(previous.FriendlyName) ? physical.ID : previous.ID;

            return new VirtualAudioRouter(
                virtualRender.ID, virtualRender.FriendlyName,
                physical.ID, physical.FriendlyName,
                previousId);
        }
    }

    public static bool IsVirtualCableInstalled()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Any(d => IsVirtualRender(d.FriendlyName));
        }
        catch { return false; }
    }

    public static string MissingCableMessage =>
        "同步转播系统声音需要虚拟声卡（与 Mac 端 BlackHole 同类）。\n" +
        "请安装免费的 VB-Audio Virtual Cable：https://vb-audio.com/Cable/\n" +
        "安装后重启 Chorus，再点「统一转播系统声音」。";

    public static bool IsVirtualRender(string? friendlyName)
    {
        if (string.IsNullOrEmpty(friendlyName)) return false;
        var n = friendlyName;
        return n.Contains("cable input", StringComparison.OrdinalIgnoreCase)
            || n.Contains("cable in ", StringComparison.OrdinalIgnoreCase) // "CABLE In 16ch"
            || (n.Contains("cable", StringComparison.OrdinalIgnoreCase)
                && n.Contains("vb-audio", StringComparison.OrdinalIgnoreCase))
            || n.Contains("vb-audio virtual cable", StringComparison.OrdinalIgnoreCase)
            || n.Contains("voicemeeter input", StringComparison.OrdinalIgnoreCase)
            || n.Contains("voicemeeter vaio", StringComparison.OrdinalIgnoreCase)
            || n.Contains("voicemeeter aux", StringComparison.OrdinalIgnoreCase)
            || n.Contains("blackhole", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Point the system default output at the virtual cable (apps render into it).</summary>
    public void Activate()
    {
        AudioEndpointPolicy.SetDefaultEndpointAllRoles(VirtualRenderId);
        _activated = true;
    }

    /// <summary>Restore the previous default output device.</summary>
    public void Restore()
    {
        if (!_activated) return;
        try { AudioEndpointPolicy.SetDefaultEndpointAllRoles(_previousDefaultId); }
        catch { }
        _activated = false;
    }

    public MMDevice OpenVirtualRender()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(VirtualRenderId);
    }

    public MMDevice OpenPhysicalRender()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.GetDevice(PhysicalRenderId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Restore();
    }
}
