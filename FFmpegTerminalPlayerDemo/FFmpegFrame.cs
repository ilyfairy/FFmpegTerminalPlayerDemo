using System.Buffers;

public abstract class FFmpegFrame : IDisposable
{
    public abstract TimeSpan StartTime { get; }
    public abstract void Dispose();
}


/// <summary>
/// RGBA 格式的图像帧
/// </summary>
public class FFmpegVideoFrame : FFmpegFrame
{
    private byte[] _frame;
    public int Width { get; set; }
    public int Height { get; set; }
    public int RowBytes { get; set; }
    public override TimeSpan StartTime { get; }

    public Memory<byte> Frame => _frame.AsMemory(0, RowBytes * Height);

    public FFmpegVideoFrame(byte[] frame, TimeSpan startTime, int width, int height, int rowBytes)
    {
        _frame = frame;
        Width = width;
        Height = height;
        RowBytes = rowBytes;
        StartTime = startTime;
    }

    public override void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_frame);
        _frame = null!;
    }
}



public class FFmpegAudioFrame : FFmpegFrame
{
    private byte[] _frame;
    public int Channel { get; set; }
    public AudioFormat Format { get; set; }

    // 采样率
    public int SampleRate { get; set; }

    // 当前帧的采样数
    public int SampleCount { get; set; }

    public override TimeSpan StartTime { get; }

    public Memory<byte> Frame => _frame.AsMemory(0, Channel * SampleCount * Format switch
    {
        AudioFormat.Float => 4,
        AudioFormat.S16 => 2,
        _ => throw new NotSupportedException(),
    });

    public FFmpegAudioFrame(byte[] frame, TimeSpan startTime, int channel, int sampleRate, int sampleCount, AudioFormat format)
    {
        Channel = channel;
        Format = format;
        SampleRate = sampleRate;
        SampleCount = sampleCount;
        _frame = frame;
        StartTime = startTime;
    }

    public override void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_frame);
        _frame = null!;
    }
}

public enum AudioFormat
{
    Unknown,
    Float,
    S16,
    S32,
}
