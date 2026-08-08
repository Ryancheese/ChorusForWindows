namespace ChorusCore.Audio;

/// <summary>Static helpers for supported audio file formats.</summary>
public static class AudioFormats
{
    public static readonly string[] SupportedExtensions =
        ["mp3", "m4a", "aac", "wav", "aiff", "aif", "caf", "flac", "mp4", "alac", "ogg", "wma"];

    public static bool IsSupported(string filePath)
    {
        var ext = System.IO.Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        return Array.IndexOf(SupportedExtensions, ext) >= 0;
    }
}
