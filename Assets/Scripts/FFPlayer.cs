using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace UnityFFmpeg
{
    /// <summary>
    /// FFPlayer
    /// </summary>
    public sealed unsafe class FFPlayer : IDisposable
    {
        private string _url;
        int error;
        AVHWDeviceType deviceType;
        private Size _frameSize;
        private AVPixelFormat _pixelFormat;
        private AVSampleFormat _sample_fmt;

        private readonly AVCodecContext* _pVideoContext;
        private readonly AVCodecContext* _pAudioContext;
        private readonly AVFormatContext* _pFormatContext;
        private readonly int _videoStreamIndex;
        private readonly int _audioStreamIndex;

        private readonly AVFrame* _audioFrame;
        private readonly AVFrame* _videoFrame;
        private AVFrame* _g2cFrame;
        private AVFrame _tempFrame;
        private readonly AVPacket* _packet;

        SwrContext* _audioSwrContext;
        int outChannelCount;
        AVSampleFormat outFormat;
        AVPixelFormat destinationPixelFormat = AVPixelFormat.AV_PIX_FMT_RGB24;

        private Action<byte[]> _onVideoData;
        private Action<byte[]> _onAudioData;
        private Action<int, int> _onVideoSize;

        // 新增同步相关字段
        private double _audioClock;              // 音频主时钟（秒）
        private double _videoClock;               // 视频时钟（秒）
        private double _frameTimer;               // 上一帧显示时间
        private double _frameDelay;               // 帧间延迟（基于帧率）
        private bool _useAudioClock = true;       // 是否使用音频时钟作为主时钟
        private readonly object _clockLock = new object();

        // 帧队列
        private ConcurrentQueue<AVFrameHolder> _videoFrameQueue = new ConcurrentQueue<AVFrameHolder>();
        private const int MAX_VIDEO_QUEUE_SIZE = 15; // 视频帧队列最大长度
        private ManualResetEvent _frameReadyEvent = new ManualResetEvent(false);

        // 音频相关
        private double _audioPts;                 // 当前音频帧的PTS
        private double _audioStartTime;            // 音频开始时间

        Thread thread = null;
        Thread renderThread;

        public FFPlayer(string url, Action<int, int> onVideoSize, Action<byte[]> onVideoData, Action<byte[]> onAudioData)
        {
            _url = url;
            _onVideoSize += onVideoSize;
            _onVideoData += onVideoData;
            _onAudioData += onAudioData;
            Init();
            _pFormatContext = ffmpeg.avformat_alloc_context();
            var pFormatContext = _pFormatContext;
            ffmpeg.avformat_open_input(&pFormatContext, url, null, null).ThrowExceptionIfError();
            ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();
            AVCodec* videoCodec = null;
            _videoStreamIndex = ffmpeg.av_find_best_stream(_pFormatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &videoCodec, 0).ThrowExceptionIfError();
            AVCodec* audioCodec = null;
            _audioStreamIndex = ffmpeg.av_find_best_stream(_pFormatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &audioCodec, 0).ThrowExceptionIfError();
            Debug.LogWarning("_videoStreamIndex:" + _videoStreamIndex);
            Debug.LogWarning("_audioStreamIndex:" + _audioStreamIndex);

            _pVideoContext = ffmpeg.avcodec_alloc_context3(videoCodec);
            Debug.LogWarning("deviceType:" + deviceType);
            if (deviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                ffmpeg.av_hwdevice_ctx_create(&_pVideoContext->hw_device_ctx, deviceType, null, null, 0).ThrowExceptionIfError();
            }
            AVCodecParameters* vavcp = _pFormatContext->streams[_videoStreamIndex]->codecpar;
            ffmpeg.avcodec_parameters_to_context(_pVideoContext, vavcp).ThrowExceptionIfError();

            _pAudioContext = ffmpeg.avcodec_alloc_context3(audioCodec);
            AVCodecParameters* aavcp = _pFormatContext->streams[_audioStreamIndex]->codecpar;
            ffmpeg.avcodec_parameters_to_context(_pAudioContext, aavcp).ThrowExceptionIfError();

            ffmpeg.avcodec_open2(_pVideoContext, videoCodec, null).ThrowExceptionIfError();
            ffmpeg.avcodec_open2(_pAudioContext, audioCodec, null).ThrowExceptionIfError();

            _frameSize = new Size(_pVideoContext->width, _pVideoContext->height);
            if (_onVideoSize != null)
            {
                _onVideoSize(_frameSize.Width, _frameSize.Height);
            }
            _pixelFormat = _pVideoContext->pix_fmt;
            _sample_fmt = _pVideoContext->sample_fmt;

            //frame->16bit 44100 PCM 统一音频采样格式与采样率
            //创建swrcontext上下文件
            _audioSwrContext = ffmpeg.swr_alloc();
            //音频格式  输入的采样设置参数
            AVSampleFormat inFormat = _pAudioContext->sample_fmt;
            // 出入的采样格式
            outFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;
            // 输入采样率
            int inSampleRate = _pAudioContext->sample_rate;
            // 输出采样率
            int outSampleRate = 44100;
            // 输入声道布局
            ulong in_ch_layout = _pAudioContext->channel_layout;
            //输出声道布局
            int out_ch_layout = ffmpeg.AV_CH_LAYOUT_STEREO;
            //给Swrcontext 分配空间，设置公共参数
            ffmpeg.swr_alloc_set_opts(_audioSwrContext, out_ch_layout, outFormat, outSampleRate,
                    (long)in_ch_layout, inFormat, inSampleRate, 0, null
                    );
            // 初始化
            ffmpeg.swr_init(_audioSwrContext);
            // 获取声道数量
            outChannelCount = ffmpeg.av_get_channel_layout_nb_channels((ulong)out_ch_layout);

            _packet = ffmpeg.av_packet_alloc();
            _audioFrame = ffmpeg.av_frame_alloc();
            _videoFrame = ffmpeg.av_frame_alloc();
            _g2cFrame = ffmpeg.av_frame_alloc();

            // 初始化同步变量
            _audioClock = 0;
            _videoClock = 0;
            _frameTimer = 0;
            _frameDelay = 1.0 / 30; // 默认30fps

            thread = new Thread(new ThreadStart(DecodeThread));
            thread.IsBackground = true;
            thread.Start();

            // 启动视频渲染线程
            renderThread = new Thread(new ThreadStart(RenderThread));
            renderThread.IsBackground = true;
            renderThread.Start();
        }

        private void Init()
        {
            ffmpeg.RootPath = Application.streamingAssetsPath + "/FFmpeg/x86_64";
            Debug.LogWarning($"FFmpeg version info: {ffmpeg.av_version_info()}");
            SetupLogging();
            ConfigureHWDecoder();
        }

        bool isVideo;

        private void DecodeThread()
        {
            while (true)
            {
                if(_videoFrameQueue.Count >= MAX_VIDEO_QUEUE_SIZE)
                {
                    Thread.Sleep(10);
                }
                // 1. 读取数据包
                error = ffmpeg.av_read_frame(_pFormatContext, _packet);
                if (error == ffmpeg.AVERROR_EOF)
                {
                    Debug.LogWarning("End of file reached");
                    break;
                }

                // 2. 处理音频包
                if (_packet->stream_index == _audioStreamIndex)
                {
                    ProcessAudioPacket();
                }
                // 3. 处理视频包
                else if (_packet->stream_index == _videoStreamIndex)
                {
                    ProcessVideoPacket();
                }

                ffmpeg.av_packet_unref(_packet);
            }
        }

        private void ProcessAudioPacket()
        {
            // 发送数据包到解码器
            error = ffmpeg.avcodec_send_packet(_pAudioContext, _packet);
            if (error < 0) return;

            // 接收解码后的帧
            while (ffmpeg.avcodec_receive_frame(_pAudioContext, _audioFrame) >= 0)
            {
                // 计算音频PTS（秒）
                double pts = _audioFrame->best_effort_timestamp *
                            ffmpeg.av_q2d(_pFormatContext->streams[_audioStreamIndex]->time_base);

                // 更新音频时钟
                lock (_clockLock)
                {
                    _audioClock = pts;

                    // 如果是第一帧，设置音频开始时间
                    if (_audioStartTime == 0)
                    {
                        _audioStartTime = pts;
                    }
                }

                // 音频重采样
                byte* out_buffer = (byte*)Marshal.AllocHGlobal(2 * 44100);
                ffmpeg.swr_convert(_audioSwrContext, &out_buffer, 2 * 44100,
                                  (byte**)&_audioFrame->data, _audioFrame->nb_samples);

                int out_buffer_size = ffmpeg.av_samples_get_buffer_size(null, outChannelCount,
                                                                      _audioFrame->nb_samples, outFormat, 1);
                if (out_buffer_size > 0)
                {
                    byte[] data = new byte[out_buffer_size];
                    Marshal.Copy((IntPtr)out_buffer, data, 0, out_buffer_size);
                    _onAudioData?.Invoke(data);
                }

                Marshal.FreeHGlobal((IntPtr)out_buffer);
            }
        }

        private void ProcessVideoPacket()
        {
            // 发送数据包到解码器
            error = ffmpeg.avcodec_send_packet(_pVideoContext, _packet);
            if (error < 0) return;

            // 接收解码后的帧
            while (ffmpeg.avcodec_receive_frame(_pVideoContext, _videoFrame) >= 0)
            {
                // 计算视频PTS（秒）
                double pts = _videoFrame->best_effort_timestamp *
                            ffmpeg.av_q2d(_pFormatContext->streams[_videoStreamIndex]->time_base);

                // 更新视频时钟
                lock (_clockLock)
                {
                    _videoClock = pts;
                }

                // 将帧加入队列
                if (_videoFrameQueue.Count < MAX_VIDEO_QUEUE_SIZE)
                {
                    var frameHolder = new AVFrameHolder(_videoFrame, pts, _frameSize, _pixelFormat);
                    _videoFrameQueue.Enqueue(frameHolder);
                    _frameReadyEvent.Set();
                }
                else
                {
                    // 队列满时丢弃最旧帧
                    Debug.LogWarning("Video queue full, dropping frame");
                }
            }
        }

        // 新增：视频渲染线程
        private void RenderThread()
        {
            while (true)
            {
                // 等待帧可用
                _frameReadyEvent.WaitOne();

                if (_videoFrameQueue.TryDequeue(out AVFrameHolder frameHolder))
                {
                    // 计算显示延迟
                    double delay = CalculateFrameDelay(frameHolder.Pts);

                    // 等待到正确显示时间
                    if (delay > 0)
                    {
                        Thread.Sleep((int)(delay * 1000));
                    }

                    // 转换并显示帧
                    DisplayVideoFrame(frameHolder);

                    // 释放帧资源
                    frameHolder.Dispose();

                    // 如果没有更多帧，重置事件
                    if (_videoFrameQueue.IsEmpty)
                    {
                        _frameReadyEvent.Reset();
                    }
                }
            }
        }

        // 计算帧显示延迟
        private double CalculateFrameDelay(double pts)
        {
            double actualDelay, delay;

            // 计算帧间延迟（基于帧率）
            double timeSinceLastFrame = GetCurrentTime() - _frameTimer;
            delay = _frameDelay - timeSinceLastFrame;

            // 计算参考时间（音频时钟或系统时钟）
            double refClock = _useAudioClock ?
                GetAudioClock() :
                GetCurrentTime() - _audioStartTime;

            // 计算当前帧应显示的时间差
            double diff = pts - refClock;

            // 同步阈值（50ms）
            const double syncThreshold = 0.05;

            // 调整延迟以同步
            if (Math.Abs(diff) < syncThreshold)
            {
                // 在阈值内，使用计算的延迟
                actualDelay = delay;
            }
            else if (diff > 0)
            {
                // 视频落后，减少延迟
                actualDelay = Math.Max(0, delay + diff);
            }
            else
            {
                // 视频超前，增加延迟
                actualDelay = delay + diff;
            }

            // 更新帧计时器
            _frameTimer = GetCurrentTime();

            // 限制延迟在合理范围内
            actualDelay = Math.Max(0, Math.Min(actualDelay, 0.5));

            return actualDelay;
        }

        // 获取当前音频时钟
        private double GetAudioClock()
        {
            lock (_clockLock)
            {
                return _audioClock;
            }
        }

        // 获取当前时间（秒）
        private double GetCurrentTime()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        }

        // 显示视频帧
        private unsafe void DisplayVideoFrame(AVFrameHolder frameHolder)
        {
            AVFrame* frame = frameHolder.Frame;

            if (_pVideoContext->hw_device_ctx != null)
            {
                ffmpeg.av_hwframe_transfer_data(_g2cFrame, frame, 0).ThrowExceptionIfError();
                _tempFrame = *_g2cFrame;
            }
            else
            {
                _tempFrame = *frame;
            }

            var sourcePixelFormat = GetHWPixelFormat(deviceType);
            using (var vfc = new VideoFrameConverter(frameHolder.FrameSize, sourcePixelFormat,
                                                   frameHolder.FrameSize, destinationPixelFormat))
            {
                AVFrame convertedFrame = vfc.Convert(_tempFrame);
                int dataLen = frameHolder.FrameSize.Width * frameHolder.FrameSize.Height * 3;
                byte[] data = new byte[dataLen];
                Marshal.Copy((IntPtr)convertedFrame.data[0], data, 0, dataLen);
                _onVideoData?.Invoke(data);
            }
        }

        private unsafe void SetupLogging()
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_VERBOSE);
            // do not convert to local function
            av_log_set_callback_callback logCallback = (p0, level, format, vl) =>
            {
                if (level > ffmpeg.av_log_get_level())
                {
                    return;
                }
                var lineSize = 1024;
                var lineBuffer = stackalloc byte[lineSize];
                var printPrefix = 1;
                ffmpeg.av_log_format_line(p0, level, format, vl, lineBuffer, lineSize, &printPrefix);
                var line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer);
                Debug.LogWarning(line);
            };
            ffmpeg.av_log_set_callback(logCallback);
        }

        private void ConfigureHWDecoder()
        {
            deviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            var availableHWDecoders = new Dictionary<int, AVHWDeviceType>();

            var type = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            var number = 0;
            while ((type = ffmpeg.av_hwdevice_iterate_types(type)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                Debug.Log($"{++number}. {type}");
                availableHWDecoders.Add(number, type);
            }
            if (availableHWDecoders.Count == 0)
            {
                Debug.Log("Your system have no hardware decoders.");
                deviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
                return;
            }
            int decoderNumber = availableHWDecoders.SingleOrDefault(t => t.Value == AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2).Key;
            if (decoderNumber == 0)
            {
                decoderNumber = availableHWDecoders.First().Key;
            }
            Debug.LogWarning($"Selected [{decoderNumber}]");
            int.TryParse(Console.ReadLine(), out var inputDecoderNumber);
            availableHWDecoders.TryGetValue(inputDecoderNumber == 0 ? decoderNumber : inputDecoderNumber, out deviceType);
        }

        private AVPixelFormat GetHWPixelFormat(AVHWDeviceType hWDevice)
        {
            switch (hWDevice)
            {
                case AVHWDeviceType.AV_HWDEVICE_TYPE_NONE:
                    return AVPixelFormat.AV_PIX_FMT_NONE;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU:
                    return AVPixelFormat.AV_PIX_FMT_VDPAU;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA:
                    return AVPixelFormat.AV_PIX_FMT_CUDA;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI:
                    return AVPixelFormat.AV_PIX_FMT_VAAPI;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2:
                    return AVPixelFormat.AV_PIX_FMT_NV12;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_QSV:
                    return AVPixelFormat.AV_PIX_FMT_QSV;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX:
                    return AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA:
                    return AVPixelFormat.AV_PIX_FMT_NV12;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_DRM:
                    return AVPixelFormat.AV_PIX_FMT_DRM_PRIME;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_OPENCL:
                    return AVPixelFormat.AV_PIX_FMT_OPENCL;
                case AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC:
                    return AVPixelFormat.AV_PIX_FMT_MEDIACODEC;
                default:
                    return AVPixelFormat.AV_PIX_FMT_NONE;
            }
        }

        public void Dispose()
        {
            if (thread != null)
            {
                if (thread.IsAlive)
                {
                    thread.Abort();
                }

                // 清理帧队列
                while (_videoFrameQueue.TryDequeue(out AVFrameHolder frameHolder))
                {
                    frameHolder.Dispose();
                }

                if (renderThread != null)
                {
                    if (renderThread.IsAlive)
                    {
                        renderThread.Abort();
                    }
                }
            }

            ffmpeg.avcodec_close(_pVideoContext);
            ffmpeg.avcodec_close(_pAudioContext);

            var pFormatContext = _pFormatContext;
            ffmpeg.avformat_close_input(&pFormatContext);

            ffmpeg.av_frame_unref(_audioFrame);
            ffmpeg.av_free(_audioFrame);

            ffmpeg.av_frame_unref(_videoFrame);
            ffmpeg.av_free(_videoFrame);

            ffmpeg.av_frame_unref(_g2cFrame);
            ffmpeg.av_free(_g2cFrame);

            ffmpeg.av_packet_unref(_packet);
            ffmpeg.av_free(_packet);
        }
    }

    // 新增：AVFrame包装器，用于存储帧及其时间戳
    public unsafe class AVFrameHolder : IDisposable
    {
        public AVFrame* Frame { get; private set; }
        public double Pts { get; private set; }
        public Size FrameSize { get; private set; }
        public AVPixelFormat PixelFormat { get; private set; }

        public AVFrameHolder(AVFrame* frame, double pts, Size frameSize, AVPixelFormat pixelFormat)
        {
            Frame = ffmpeg.av_frame_alloc();
            ffmpeg.av_frame_ref(Frame, frame);
            Pts = pts;
            FrameSize = frameSize;
            PixelFormat = pixelFormat;
        }

        public void Dispose()
        {
            if (Frame != null)
            {
                ffmpeg.av_frame_unref(Frame);
                //ffmpeg.av_frame_free(Frame);
                Frame = null;
            }
        }
    }
}