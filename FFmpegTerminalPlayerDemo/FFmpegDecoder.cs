using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Sdcb.FFmpeg.Raw;

public class FFmpegDecoder : IDisposable
{
    public string FilePath { get; }
    public bool HandleVideo { get; }
    public bool HandleAudio { get; }

    private bool _isGPU = false;
    private unsafe AVFormatContext* _formatContext;
    private unsafe AVCodecContext* _videoDecoderContext;
    private unsafe AVCodecContext* _audioDecoderContext;
    private unsafe AVStream* _videoStream = null;
    private unsafe AVStream* _audioStream = null;
    private unsafe SwsContext* _swsContext = null;
    private unsafe AVBufferRef* _deviceContext = null;

    private unsafe AVPacket* _packet;
    private unsafe AVFrame* _gpuFrame;
    private unsafe AVFrame* _cpuFrame;
    private unsafe AVFrame* _audioFrame;

    private readonly Channel<FFmpegAudioFrame>? _audioChannel;
    private readonly Channel<FFmpegVideoFrame>? _videoChannel;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int FrameRate { get; set; }
    public int SampleRate { get; set; }
    public AudioFormat AudioFormat { get; set; }
    public int AudioChannel { get; set; }
    public TimeSpan Duration { get; set; }


    private AVCodecContext_get_format? _AVCodecContext_get_format;


    static FFmpegDecoder()
    {
        ffmpeg.avdevice_register_all();
    }

    public FFmpegDecoder(string filePath, bool handleVideo = true, bool handleAudio = true)
    {
        FilePath = filePath;
        HandleVideo = handleVideo;
        HandleAudio = handleAudio;

        if (handleVideo)
        {
            _videoChannel = Channel.CreateBounded<FFmpegVideoFrame>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
            });
        }
        if (handleAudio)
        {
            _audioChannel = Channel.CreateBounded<FFmpegAudioFrame>(new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = true,
            });
        }
    }

    private unsafe bool InitializeHardwareDevice(AVCodecContext* codecContext)
    {
        AVHWDeviceType deviceType = AVHWDeviceType.None;
        if (HandleVideo)
        {
            List<AVHWDeviceType> deviceTypes = new();

            // HWDevice
            while ((deviceType = ffmpeg.av_hwdevice_iterate_types(deviceType)) != AVHWDeviceType.None)
            {
                deviceTypes.Add(deviceType);
            }

            foreach (var item in deviceTypes)
            {
                fixed (AVBufferRef** pDeviceContext = &_deviceContext)
                    if (ffmpeg.av_hwdevice_ctx_create(ref _deviceContext, item, null, null, 0) == 0)
                    {
                        deviceType = item;
                        break;
                    }
            }
        }

        if (_deviceContext != null) // 只有视频才使用硬件加速
        {
            _AVCodecContext_get_format = new AVCodecContext_get_format((codecContext, format) =>
            {
                var deviceFormat = deviceType switch
                {
                    AVHWDeviceType.Cuda => AVPixelFormat.Cuda,
                    AVHWDeviceType.D3d11va => AVPixelFormat.D3d11,
                    AVHWDeviceType.Dxva2 => AVPixelFormat.Dxva2Vld,
                    AVHWDeviceType.Qsv => AVPixelFormat.Qsv,
                    AVHWDeviceType.Vaapi => AVPixelFormat.Vaapi,
                    _ => AVPixelFormat.None,
                };
                while (*format != AVPixelFormat.None)
                {
                    if (*format == deviceFormat)
                    {
                        return *format;
                    }
                    format++;
                }
                return AVPixelFormat.None;
            });
            codecContext->hw_device_ctx = _deviceContext;
            codecContext->get_format = _AVCodecContext_get_format;
            return true;
        }

        return false;
    }

    public unsafe void Initialize()
    {
        if(HandleVideo == false && HandleAudio == false)
        {
            throw new ArgumentException("至少需要处理音频或视频其中之一");
        }

        _formatContext = ffmpeg.avformat_alloc_context();
        fixed (AVFormatContext** pFormatContext = &_formatContext)
        {
            if (ffmpeg.avformat_open_input(pFormatContext, FilePath, null, null) != 0)
            {
                throw new Exception("无法打开输入文件");
            }
        }

        ffmpeg.avformat_find_stream_info(_formatContext, null);
        Duration = TimeSpan.FromSeconds((double)_formatContext->duration / ffmpeg.AV_TIME_BASE);

        if (HandleAudio && ffmpeg.av_find_best_stream(_formatContext, AVMediaType.Audio, -1, -1, null, 0) is int audioStreamIndex and >= 0)
        {
            _audioStream = _formatContext->streams[audioStreamIndex];
        }
        if (HandleVideo && ffmpeg.av_find_best_stream(_formatContext, AVMediaType.Video, -1, -1, null, 0) is int videoStreamIndex and >= 0)
        {
            _videoStream = _formatContext->streams[videoStreamIndex];
        }

        var videoDecoder = !HandleVideo ? null : ffmpeg.avcodec_find_decoder(_videoStream->codecpar->codec_id);
        var audioDecoder = !HandleAudio ? null : ffmpeg.avcodec_find_decoder(_audioStream->codecpar->codec_id);

        _videoDecoderContext = !HandleVideo ? null : ffmpeg.avcodec_alloc_context3(videoDecoder);
        _audioDecoderContext = !HandleAudio ? null : ffmpeg.avcodec_alloc_context3(audioDecoder);

        if (InitializeHardwareDevice(_videoDecoderContext))
        {
            _isGPU = true;
        }

        _packet = ffmpeg.av_packet_alloc();
        if (HandleVideo)
        {
            ffmpeg.avcodec_parameters_to_context(_videoDecoderContext, _videoStream->codecpar);
            ffmpeg.avcodec_open2(_videoDecoderContext, videoDecoder, null);
            Width = _videoStream->codecpar->width;
            Height = _videoStream->codecpar->height;
            FrameRate = (int)Math.Round((double)_videoStream->r_frame_rate.Num / _videoStream->r_frame_rate.Den);

            _gpuFrame = ffmpeg.av_frame_alloc();
            _cpuFrame = ffmpeg.av_frame_alloc();
        }
        if (HandleAudio)
        {
            ffmpeg.avcodec_parameters_to_context(_audioDecoderContext, _audioStream->codecpar);
            ffmpeg.avcodec_open2(_audioDecoderContext, audioDecoder, null);
            SampleRate = _audioStream->codecpar->sample_rate;
            AudioFormat = ((AVSampleFormat)_audioStream->codecpar->format) switch
            {
                AVSampleFormat.Fltp => AudioFormat.Float,
                AVSampleFormat.S16 => AudioFormat.S16,
                _ => throw new NotSupportedException("不支持的音频格式"),
            };
            AudioChannel = _audioStream->codecpar->ch_layout.nb_channels;

            _audioFrame = ffmpeg.av_frame_alloc();
        }
    }

    public unsafe FFmpegFrame? GetNextFrame()
    {
        while (true)
        {
            using var frameNullable = GetNextFrameInternal();
            if (frameNullable == null)
            {
                return null;
            }
            var frame = frameNullable.Value;

            if (HandleAudio && frame.StreamIndex == _audioStream->index)
            {
                AVSampleFormat rawFormat = (AVSampleFormat)frame.Frame->format;
                int channel = frame.Frame->ch_layout.nb_channels;
                int currentSamples = frame.Frame->nb_samples;

                AudioFormat format;
                byte[] audioBuffer;

                if (rawFormat is AVSampleFormat.Fltp)
                {
                    format = AudioFormat.Float;
                    var audioBufferSize = channel * currentSamples * 4;
                    audioBuffer = ArrayPool<byte>.Shared.Rent(audioBufferSize);
                    MemoryStream audioBufferStream = new(audioBuffer, 0, audioBufferSize, true, true);
                    for (int i = 0; i < currentSamples; i++)
                    {
                        for (int channelIndex = 0; channelIndex < channel; channelIndex++)
                        {
                            var span = new ReadOnlySpan<byte>((float*)frame.Frame->data[channelIndex] + i, 4);
                            audioBufferStream.Write(span);
                        }
                    }
                }
                else if(rawFormat is AVSampleFormat.S16)
                {
                    format = AudioFormat.S16;
                    var audioBufferSize = channel * currentSamples * 2;
                    audioBuffer = ArrayPool<byte>.Shared.Rent(audioBufferSize);
                    Marshal.Copy(frame.Frame->data[0], audioBuffer, 0, audioBufferSize);
                }
                else if (rawFormat is AVSampleFormat.S32)
                {
                    format = AudioFormat.S32;
                    var audioBufferSize = channel * currentSamples * 4;
                    audioBuffer = ArrayPool<byte>.Shared.Rent(audioBufferSize);
                    Marshal.Copy(frame.Frame->data[0], audioBuffer, 0, audioBufferSize);
                }
                else
                {
                    throw new NotSupportedException();
                }

                return new FFmpegAudioFrame(audioBuffer, TimeSpan.FromSeconds((double)frame.Pts * _audioStream->time_base.Num / _audioStream->time_base.Den), channel, SampleRate, currentSamples, format);
            }

            if (HandleVideo && frame.StreamIndex == _videoStream->index)
            {
                var rowBytes = frame.Frame->linesize[0];
                var size = rowBytes * frame.Frame->height;
                var array = ArrayPool<byte>.Shared.Rent(size);
                nint src = frame.Frame->data[0];
                Marshal.Copy(src, array, 0, size);

                return new FFmpegVideoFrame(array, TimeSpan.FromSeconds((double)frame.Pts * _videoStream->time_base.Num / _videoStream->time_base.Den), frame.Frame->width, frame.Frame->height, rowBytes);
            }
        }
    }

    private unsafe DecodedFrame? GetNextFrameInternal()
    {
        var lastStreamIndex = -1;
        while (ffmpeg.av_read_frame(_formatContext, _packet) >= 0)
        {
            if (HandleVideo && _packet->stream_index == _videoStream->index)
            {
                ffmpeg.avcodec_send_packet(_videoDecoderContext, _packet);
            }
            else if (HandleAudio && _packet->stream_index == _audioStream->index)
            {
                ffmpeg.avcodec_send_packet(_audioDecoderContext, _packet);
            }

            lastStreamIndex = _packet->stream_index;

            while (HandleAudio && ffmpeg.avcodec_receive_frame(_audioDecoderContext, _audioFrame) >= 0)
            {
                var audioFrame = _audioFrame;
                _audioFrame = ffmpeg.av_frame_alloc();

                return new DecodedFrame()
                {
                    Frame = audioFrame,
                    Pts = audioFrame->pts,
                    StreamIndex = lastStreamIndex,
                };
            }

            var frameToProcess = _isGPU ? _gpuFrame : _cpuFrame;
            while (HandleVideo && ffmpeg.avcodec_receive_frame(_videoDecoderContext, frameToProcess) >= 0)
            {
               var rgbaFrame = ffmpeg.av_frame_alloc();

                if (_isGPU)
                {
                    ffmpeg.av_hwframe_transfer_data(_cpuFrame, _gpuFrame, 0); // 从GPU拷贝到CPU
                }

                // 用来转换到RGBA
                if (_swsContext is null)
                {
                    _swsContext = ffmpeg.sws_getContext(_videoDecoderContext->width, _videoDecoderContext->height, (AVPixelFormat)_cpuFrame->format,
                        _videoDecoderContext->width, _videoDecoderContext->height, AVPixelFormat.Rgba, default, null, null, null);
                }
                ffmpeg.sws_scale_frame(_swsContext, rgbaFrame, _cpuFrame);

                var pts = frameToProcess->pts;

                ffmpeg.av_frame_unref(_gpuFrame);
                ffmpeg.av_frame_unref(_cpuFrame);

                return new DecodedFrame()
                {
                    Frame = rgbaFrame,
                    Pts = pts,
                    StreamIndex = lastStreamIndex,
                };
            }
            ffmpeg.av_packet_unref(_packet);

        }

        return null;
    }

    public unsafe void Seek(TimeSpan position)
    {
        if (_audioDecoderContext != null)
        {
            ffmpeg.avcodec_flush_buffers(_audioDecoderContext);
        }
        if (_videoDecoderContext != null)
        {
            ffmpeg.avcodec_flush_buffers(_videoDecoderContext);
        }

        if (_videoStream != null)
        {
            var pts = (long)(position.TotalSeconds * _videoStream->time_base.Den / _videoStream->time_base.Num);
            ffmpeg.av_seek_frame(_formatContext, _videoStream->index, pts, (int)AVSEEK_FLAG.Backward);
        }
        else if (_audioStream != null)
        {
            var pts = (long)(position.TotalSeconds * _audioStream->time_base.Den / _audioStream->time_base.Num);
            ffmpeg.av_seek_frame(_formatContext, _audioStream->index, pts, (int)AVSEEK_FLAG.Backward);
        }

        ffmpeg.av_packet_unref(_packet);
        ffmpeg.avformat_flush(_formatContext);
    }

    public unsafe void Dispose()
    {
        fixed(AVFrame** pCpuFrame = &_cpuFrame)
        fixed(AVFrame** pGpuFrame = &_gpuFrame)
        fixed(AVFrame** pAudioFrame = &_audioFrame)
        fixed(AVCodecContext** pVideoDecoderContext = &_videoDecoderContext)
        fixed (AVCodecContext** pAudioDecoderContext = &_audioDecoderContext)
        fixed (AVBufferRef** pDeviceContext = &_deviceContext)
        {
            ffmpeg.av_frame_free(pCpuFrame);
            ffmpeg.av_frame_free(pGpuFrame);
            ffmpeg.av_frame_free(pAudioFrame);
            ffmpeg.avcodec_free_context(pVideoDecoderContext);
            ffmpeg.avcodec_free_context(pAudioDecoderContext);
            ffmpeg.av_buffer_unref(pDeviceContext);
        }
        ffmpeg.avformat_free_context(_formatContext);
    }

    private unsafe struct DecodedFrame : IDisposable
    {
        public required int StreamIndex;
        public required AVFrame* Frame;
        public required long Pts;

        public void Dispose()
        {
            fixed(AVFrame** pFrame = &Frame)
            {
                ffmpeg.av_frame_free(pFrame);
            }
        }
    }
}
