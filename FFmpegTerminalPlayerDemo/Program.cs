using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using NAudio.Utils;
using NAudio.Wave;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

// TODO: 内存占用过高, 提交400MB, 工作集300MB

using SemaphoreSlim ffmpegLock = new(1, 1);
var audioPipeCancellationTokenSource = new CancellationTokenSource();

Console.CursorVisible = false;
//Console.Write("\e[?1049h"); // 切换缓冲区

string input = @"Y:\Downloads\S01\S01E01.mp4";

using var decoder = new FFmpegDecoder(input, handleVideo: true, handleAudio: true);
decoder.Initialize();

var audioOutputDevice = new WasapiOut();
var audioPipe = new Pipe();

var waveFormat = decoder.AudioFormat switch
{
    AudioFormat.Float => WaveFormat.CreateIeeeFloatWaveFormat(decoder.SampleRate, decoder.AudioChannel),
    AudioFormat.S16 => new WaveFormat(decoder.SampleRate, 16, decoder.AudioChannel),
    AudioFormat.S32 => new WaveFormat(decoder.SampleRate, 32, decoder.AudioChannel),
    _ => throw new NotSupportedException(),
};
var waveProvider = new AudioAndTimestampProvider(new MediaFoundationResampler(new RawSourceWaveStream(audioPipe.Reader.AsStream(), waveFormat), audioOutputDevice.OutputWaveFormat));
audioOutputDevice.Init(waveProvider);

var audioChannel = Channel.CreateUnbounded<FFmpegAudioFrame>();
var videoChannel = Channel.CreateBounded<FFmpegVideoFrame>(5);

_ = Task.Run(async () =>
{
    while (true)
    {
        using (await ffmpegLock.WaitScopeAsync())
        {
            var item = decoder.GetNextFrame();

            if (item is FFmpegAudioFrame audioFrame)
            {
                await audioChannel.Writer.WriteAsync(audioFrame);
            }
            else if (item is FFmpegVideoFrame videoFrame)
            {
                await videoChannel.Writer.WriteAsync(videoFrame);
            }
        }
    }
});

_ = Task.Run(async () =>
{
    await foreach (var _item in audioChannel.Reader.ReadAllAsync())
        using (var item = _item)
        {
            try
            {
                await audioPipe.Writer.WriteAsync(item.Frame, audioPipeCancellationTokenSource.Token);
                await audioPipe.Writer.FlushAsync(audioPipeCancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }
});

_ = Task.Run(async () =>
{
    await foreach (var _item in videoChannel.Reader.ReadAllAsync())
        using (var item = _item)
        {
            // 同步显示, 对齐音频
            var d = item.StartTime - waveProvider.GetPositionTimeSpan();
            var frameInterval = 1.0 / decoder.FrameRate * 1000;

            while (audioOutputDevice.PlaybackState != PlaybackState.Playing)
            {
                await Task.Delay(10);
            }

            if (d.TotalMilliseconds > frameInterval)
            {
                // 播放快了
                if(d.TotalMilliseconds > frameInterval * 10) // 需要检测一下, 不然seek的时候会出现Sleep过长的问题
                {
                    continue;
                }
                Thread.Sleep(d);
            }
            else if (d < TimeSpan.Zero)
            {
                // 播放慢了
                var lag = (-d).TotalMilliseconds;
                if (lag > frameInterval * 2)
                {
                    continue;
                }
            }

            // 调整显示比例
            var widthRatio = (double)Console.WindowWidth / 2 / item.Width;
            var heightRatio = (double)Console.WindowHeight / item.Height;
            var ratio = Math.Min(widthRatio, heightRatio);

            var maxWidth = (int)(item.Width * ratio);
            maxWidth -= maxWidth % 2;
            var maxHeight = (int)(item.Height * ratio);
            if (maxHeight >= Console.WindowHeight)
            {
                maxHeight = Console.WindowHeight - 1;
            }

            var consoleWidth = Console.WindowWidth;
            var consoleHeight = Console.WindowHeight;

            //Console.Title = $"{Console.WindowWidth}*{Console.WindowHeight} | {maxWidth * 2}*{maxHeight}";
            Console.Title = @$"{waveProvider.GetPositionTimeSpan():mm\:ss}";

            using var image = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(item.Frame.Span, item.Width, item.Height);
            image.Mutate(v => v.Resize(maxWidth, maxHeight)); // 缩放算法比SkiaSharp的好

            // 渲染
            Console.Write($"\e[H"); // 光标移动到左上角
            StringBuilder s = new();
            for (int y = 0; y < maxHeight; y++)
            {
                for (int x = 0; x < maxWidth; x++)
                {
                    if (x != 0 && image[x, y] == image[x - 1, y]) // 少设置一次颜色
                    {
                        s.Append('　');
                        continue;
                    }
                    s.Append($"\e[48;2;{image[x, y].R};{image[x, y].G};{image[x, y].B}m　"); // '　' 全角空格
                }
                Console.Write(s);
                s.Clear();
                Console.Write("\e[0m\e[0K\e[1E"); // 重置颜色, 清除光标到行尾的内容, 换到下一行开头
            }
            // 清除底部内容
            for (int i = maxHeight; i < consoleHeight; i++)
            {
                Console.Write("\e[0m\e[0K\e[1E"); // 重置颜色, 清除光标到行尾的内容, 换到下一行开头
            }

        }
});


await Task.Delay(500);
audioOutputDevice.Play();

while (true)
{
    var key = Console.ReadKey();
    if (key.Key is ConsoleKey.LeftArrow or ConsoleKey.RightArrow)
    {
        double offset = key.Key switch
        {
            ConsoleKey.LeftArrow when key.Modifiers is ConsoleModifiers.Shift => -4,
            ConsoleKey.LeftArrow when key.Modifiers is ConsoleModifiers.Control => -30,
            ConsoleKey.RightArrow when key.Modifiers is ConsoleModifiers.Shift => 4,
            ConsoleKey.RightArrow when key.Modifiers is ConsoleModifiers.Control => 30,
            ConsoleKey.LeftArrow => -10,
            ConsoleKey.RightArrow => 10,
            _ => 0,
        };

        using (await ffmpegLock.WaitScopeAsync())
        {
            audioOutputDevice.Pause();

            var position = waveProvider.GetPositionTimeSpan() + TimeSpan.FromSeconds(offset);
            if (position < TimeSpan.Zero)
                position = TimeSpan.Zero;
            if (position > decoder.Duration)
                position = decoder.Duration;

            audioPipeCancellationTokenSource.Cancel();
            audioPipeCancellationTokenSource = new();

            while (videoChannel.Reader.TryRead(out var videoFrame))
                videoFrame.Dispose();
            while (audioChannel.Reader.TryRead(out var audioFrame))
                audioFrame.Dispose();

            decoder.Seek(position);
            waveProvider.SetPosition(position);

            audioOutputDevice.Play();
        }
    }
    else if (key.Key is ConsoleKey.Spacebar)
    {
        if (audioOutputDevice.PlaybackState == PlaybackState.Playing)
        {
            audioOutputDevice.Pause();
        }
        else
        {
            audioOutputDevice.Play();
        }
    }
}

public class AudioAndTimestampProvider : IWaveProvider, IWavePosition
{
    private readonly IWaveProvider _baseProvider;

    public WaveFormat WaveFormat { get; }
    public WaveFormat OutputWaveFormat { get; }

    private long _position;

    public AudioAndTimestampProvider(IWaveProvider waveProvider)
    {
        _baseProvider = waveProvider;
        WaveFormat = _baseProvider.WaveFormat;
        OutputWaveFormat = _baseProvider.WaveFormat;
    }

    public void SetPosition(TimeSpan newPosition)
    {
        _position = (long)(newPosition.TotalSeconds * (OutputWaveFormat.Channels * (OutputWaveFormat.BitsPerSample / 8) * OutputWaveFormat.SampleRate));
    }

    public long GetPosition()
    {
        return _position;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        try
        {
            var len = _baseProvider.Read(buffer, offset, count);
            _position += len;
            return len;
        }
        catch (Exception)
        {
            Thread.Sleep(10);
            return Read(buffer, offset, count);
        }
    }
}