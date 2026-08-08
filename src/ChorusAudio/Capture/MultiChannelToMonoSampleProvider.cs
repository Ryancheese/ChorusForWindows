using NAudio.Wave;

namespace ChorusAudio.Capture;

/// <summary>
/// Averages any channel count down to mono. Used only for the rare case where WASAPI
/// loopback reports more than 2 channels (e.g. 5.1); stereo is handled directly by
/// <c>StereoToMonoSampleProvider</c>.
/// </summary>
public sealed class MultiChannelToMonoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[]? _temp;

    public MultiChannelToMonoSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int needed = count * _channels;
        if (_temp == null || _temp.Length < needed)
            _temp = new float[needed];

        int read = _source.Read(_temp, 0, needed);
        int frames = read / _channels;
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            int baseIdx = f * _channels;
            for (int c = 0; c < _channels; c++)
                sum += _temp[baseIdx + c];
            buffer[offset + f] = sum / _channels;
        }
        return frames;
    }
}
