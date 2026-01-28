using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
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
        private Action<int, int, float> _onVideoSize;

        // 同步相关字段
        private double _audioClock;
        private double _videoClock;
        private double _frameTimer;
        private double _frameDelay;
        private bool _useAudioClock = true;
        private readonly object _clockLock = new object();

        // 帧队列优化
        private ConcurrentQueue<AVFrameHolder> _videoFrameQueue = new ConcurrentQueue<AVFrameHolder>();
        private const int MAX_VIDEO_QUEUE_SIZE = 15;
        private ManualResetEvent _frameReadyEvent = new ManualResetEvent(false);

        // 音频相关
        private double _audioPts;
        private double _audioStartPts = 0;
        private bool _audioStarted = false;

        // 视频帧率相关
        private float _videoFrameRate;
        private double _frameDuration;
        private double _lastVideoPts = -1;

        // 线程控制 - 添加停止标志
        Thread thread = null;
        Thread renderThread;
        private volatile bool _isRunning = true;
        private volatile bool _isDisposed = false;

        // 对象池，复用byte数组以减少GC压力
        private ObjectPool<byte[]> _audioBufferPool;
        private ObjectPool<byte[]> _videoBufferPool;

        // 性能监控
        private Stopwatch _renderStopwatch = new Stopwatch();
        private int _droppedFrames = 0;
        private int _renderedFrames = 0;

        public FFPlayer(string url, Action<int, int, float> onVideoSize, Action<byte[]> onVideoData, Action<byte[]> onAudioData)
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

            Debug.Log($"视频流索引: {_videoStreamIndex}, 音频流索引: {_audioStreamIndex}");

            _pVideoContext = ffmpeg.avcodec_alloc_context3(videoCodec);
            Debug.Log($"硬件解码器类型: {deviceType}");

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

            // 使用av_guess_frame_rate获取准确的视频帧率
            AVStream* videoStream = _pFormatContext->streams[_videoStreamIndex];
            AVRational frameRate = ffmpeg.av_guess_frame_rate(_pFormatContext, videoStream, null);

            if (frameRate.num != 0 && frameRate.den != 0)
            {
                _videoFrameRate = frameRate.num / frameRate.den;
                _frameDuration = 1.0 / _videoFrameRate;
                _frameDelay = _frameDuration;
                Debug.Log($"视频帧率: {_videoFrameRate:F2} FPS, 帧间隔: {_frameDuration:F3}秒");
            }
            else
            {
                _videoFrameRate = 25.0f;
                _frameDuration = 0.04;
                _frameDelay = 0.04;
                Debug.LogWarning("无法获取帧率，使用默认值: 25 FPS");
            }

            _frameSize = new Size(_pVideoContext->width, _pVideoContext->height);
            if (_onVideoSize != null)
            {
                _onVideoSize(_frameSize.Width, _frameSize.Height, _videoFrameRate);
            }

            _pixelFormat = _pVideoContext->pix_fmt;
            _sample_fmt = _pVideoContext->sample_fmt;

            // 初始化音频重采样上下文
            InitializeAudioContext();

            // 初始化对象池
            int videoBufferSize = _frameSize.Width * _frameSize.Height * 3;
            _videoBufferPool = new ObjectPool<byte[]>(() => new byte[videoBufferSize], 5);
            _audioBufferPool = new ObjectPool<byte[]>(() => new byte[2 * 44100], 5);

            _packet = ffmpeg.av_packet_alloc();
            _audioFrame = ffmpeg.av_frame_alloc();
            _videoFrame = ffmpeg.av_frame_alloc();
            _g2cFrame = ffmpeg.av_frame_alloc();

            // 初始化同步变量
            _audioClock = 0;
            _videoClock = 0;
            _frameTimer = 0;

            // 启动解码线程
            thread = new Thread(new ThreadStart(DecodeThread));
            thread.IsBackground = true;
            thread.Priority = System.Threading.ThreadPriority.BelowNormal;
            thread.Start();

            // 启动视频渲染线程
            renderThread = new Thread(new ThreadStart(RenderThread));
            renderThread.IsBackground = true;
            renderThread.Priority = System.Threading.ThreadPriority.Normal;
            renderThread.Start();

            _renderStopwatch.Start();
        }

        private void InitializeAudioContext()
        {
            // 创建swrcontext上下文
            _audioSwrContext = ffmpeg.swr_alloc();

            AVSampleFormat inFormat = _pAudioContext->sample_fmt;
            outFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;
            int inSampleRate = _pAudioContext->sample_rate;
            int outSampleRate = 44100;
            ulong in_ch_layout = _pAudioContext->channel_layout;
            int out_ch_layout = ffmpeg.AV_CH_LAYOUT_STEREO;

            ffmpeg.swr_alloc_set_opts(_audioSwrContext, out_ch_layout, outFormat, outSampleRate,
                    (long)in_ch_layout, inFormat, inSampleRate, 0, null);
            ffmpeg.swr_init(_audioSwrContext);

            outChannelCount = ffmpeg.av_get_channel_layout_nb_channels((ulong)out_ch_layout);
        }

        private void Init()
        {
            ffmpeg.RootPath = Application.streamingAssetsPath + "/FFmpeg/x86_64";
            Debug.Log($"FFmpeg版本信息: {ffmpeg.av_version_info()}");
            SetupLogging();
            ConfigureHWDecoder();
        }

        private void DecodeThread()
        {
            try
            {
                while (_isRunning && !_isDisposed)
                {
                    // 队列满时暂停读取，减少内存占用
                    if (_videoFrameQueue.Count >= MAX_VIDEO_QUEUE_SIZE)
                    {
                        Thread.Sleep(5);
                        continue;
                    }

                    // 读取数据包
                    error = ffmpeg.av_read_frame(_pFormatContext, _packet);
                    if (error == ffmpeg.AVERROR_EOF)
                    {
                        Debug.Log("文件播放结束");
                        break;
                    }

                    if (error < 0)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    try
                    {
                        // 处理音频包
                        if (_packet->stream_index == _audioStreamIndex)
                        {
                            ProcessAudioPacket();
                        }
                        // 处理视频包
                        else if (_packet->stream_index == _videoStreamIndex)
                        {
                            ProcessVideoPacket();
                        }
                    }
                    finally
                    {
                        // 确保每次都释放packet
                        ffmpeg.av_packet_unref(_packet);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"解码线程异常: {ex}");
            }
        }

        private void ProcessAudioPacket()
        {
            error = ffmpeg.avcodec_send_packet(_pAudioContext, _packet);
            if (error < 0) return;

            while (ffmpeg.avcodec_receive_frame(_pAudioContext, _audioFrame) >= 0)
            {
                try
                {
                    // 计算音频PTS
                    double pts;
                    if (_audioFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        pts = _audioFrame->pts *
                              ffmpeg.av_q2d(_pFormatContext->streams[_audioStreamIndex]->time_base);
                    }
                    else if (_audioFrame->pkt_dts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        pts = _audioFrame->pkt_dts *
                              ffmpeg.av_q2d(_pFormatContext->streams[_audioStreamIndex]->time_base);
                    }
                    else
                    {
                        pts = 0;
                    }

                    // 记录第一帧音频的PTS作为基准
                    if (!_audioStarted && pts > 0)
                    {
                        _audioStartPts = pts;
                        _audioStarted = true;
                        Debug.Log($"音频基准PTS: {_audioStartPts:F3}");
                    }

                    // 计算相对于基准的时间
                    double currentAudioTime = pts - _audioStartPts;

                    // 更新音频时钟
                    lock (_clockLock)
                    {
                        _audioClock = currentAudioTime;
                    }

                    // 使用栈缓冲区代替HeapAlloc，减少GC压力
                    int maxOutputSize = 2 * 44100;
                    byte* out_buffer = stackalloc byte[maxOutputSize];

                    int convertedSamples = ffmpeg.swr_convert(_audioSwrContext, &out_buffer, maxOutputSize,
                                      (byte**)&_audioFrame->data, _audioFrame->nb_samples);

                    int out_buffer_size = ffmpeg.av_samples_get_buffer_size(null, outChannelCount,
                                                                          convertedSamples, outFormat, 1);
                    //Debug.LogWarning("out_buffer_size:" + out_buffer_size);
                    if (out_buffer_size > 0 && out_buffer_size <= maxOutputSize)
                    {
                        // 从对象池获取缓冲区
                        byte[] data = _audioBufferPool.Get();
                        if (data == null || data.Length < out_buffer_size)
                        {
                            data = new byte[out_buffer_size];
                        }

                        Marshal.Copy((IntPtr)out_buffer, data, 0, out_buffer_size);
                        _onAudioData?.Invoke(data);

                        // 归还到对象池
                        _audioBufferPool.Return(data);
                    }
                }
                finally
                {
                    // 释放帧资源
                    ffmpeg.av_frame_unref(_audioFrame);
                }
            }
        }

        private void ProcessVideoPacket()
        {
            error = ffmpeg.avcodec_send_packet(_pVideoContext, _packet);
            if (error < 0) return;

            while (ffmpeg.avcodec_receive_frame(_pVideoContext, _videoFrame) >= 0)
            {
                try
                {
                    // 计算视频PTS
                    double pts;
                    if (_videoFrame->pts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        pts = _videoFrame->pts *
                              ffmpeg.av_q2d(_pFormatContext->streams[_videoStreamIndex]->time_base);
                    }
                    else if (_videoFrame->pkt_dts != ffmpeg.AV_NOPTS_VALUE)
                    {
                        pts = _videoFrame->pkt_dts *
                              ffmpeg.av_q2d(_pFormatContext->streams[_videoStreamIndex]->time_base);
                    }
                    else
                    {
                        // 如果没有有效的PTS，基于上一帧PTS和帧率估算
                        pts = _lastVideoPts + _frameDuration;
                    }

                    // 减去音频基准时间，对齐到相同的基准
                    double currentVideoTime = pts - _audioStartPts;

                    // 更新视频时钟
                    lock (_clockLock)
                    {
                        _videoClock = currentVideoTime;
                    }

                    // 将帧加入队列
                    if (_videoFrameQueue.Count < MAX_VIDEO_QUEUE_SIZE)
                    {
                        var frameHolder = new AVFrameHolder(_videoFrame, currentVideoTime, _frameSize, _pixelFormat);
                        _videoFrameQueue.Enqueue(frameHolder);
                        _frameReadyEvent.Set();
                        _lastVideoPts = pts;
                    }
                    else
                    {
                        // 队列满时丢弃最旧的一帧，加入新帧
                        if (_videoFrameQueue.TryDequeue(out AVFrameHolder oldFrame))
                        {
                            oldFrame.Dispose();
                            _droppedFrames++;

                            var frameHolder = new AVFrameHolder(_videoFrame, currentVideoTime, _frameSize, _pixelFormat);
                            _videoFrameQueue.Enqueue(frameHolder);
                            _frameReadyEvent.Set();
                            _lastVideoPts = pts;

                            // 每丢弃100帧输出一次警告
                            if (_droppedFrames % 100 == 0)
                            {
                                Debug.LogWarning($"已丢弃 {_droppedFrames} 帧视频");
                            }
                        }
                        else
                        {
                            ffmpeg.av_frame_unref(_videoFrame);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"处理视频包异常: {ex}");
                    ffmpeg.av_frame_unref(_videoFrame);
                }
            }
        }

        private void RenderThread()
        {
            try
            {
                double lastRenderTime = GetCurrentTime();

                while (_isRunning && !_isDisposed)
                {
                    // 等待帧可用，使用超时避免线程无限等待
                    if (!_frameReadyEvent.WaitOne(50))
                    {
                        continue;
                    }

                    if (_videoFrameQueue.TryDequeue(out AVFrameHolder frameHolder))
                    {
                        try
                        {
                            // 计算显示延迟
                            double delay = CalculateFrameDelay(frameHolder.Pts);

                            // 等待到正确显示时间
                            if (delay > 0)
                            {
                                // 使用更精确的等待
                                int waitMs = (int)(delay * 1000);
                                if (waitMs > 0)
                                {
                                    Thread.Sleep(Math.Min(waitMs, 500)); // 最多等待500ms
                                }
                            }

                            // 转换并显示帧
                            DisplayVideoFrame(frameHolder);
                            _renderedFrames++;

                            // 记录渲染时间
                            double currentTime = GetCurrentTime();
                            double actualFrameTime = currentTime - lastRenderTime;
                            lastRenderTime = currentTime;

                            // 每渲染100帧输出一次调试信息
                            if (_renderedFrames % 100 == 0)
                            {
                                double audioTime = GetAudioClock();
                                Debug.Log($"已渲染 {_renderedFrames} 帧, 音频时间: {audioTime:F3}s, " +
                                         $"视频PTS: {frameHolder.Pts:F3}s, 实际帧间隔: {actualFrameTime:F3}s");
                            }

                            // 如果没有更多帧，重置事件
                            if (_videoFrameQueue.IsEmpty)
                            {
                                _frameReadyEvent.Reset();
                            }
                        }
                        finally
                        {
                            // 确保帧资源被释放
                            frameHolder.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"渲染线程异常: {ex}");
            }
        }

        private double CalculateFrameDelay(double videoPts)
        {
            double audioTime = GetAudioClock();
            double diff = videoPts - audioTime;

            // 基础延迟为帧间隔
            double baseDelay = _frameDuration;

            // 根据音视频差异调整延迟
            const double MAX_SYNC_DIFF = 0.1;       // 100ms
            const double SYNC_THRESHOLD = 0.02;     // 20ms 

            if (!_audioStarted || Math.Abs(diff) < SYNC_THRESHOLD)
            {
                // 音频未开始或差异很小，使用标准帧间隔
                return baseDelay;
            }
            else if (diff > MAX_SYNC_DIFF)
            {
                // 视频超前太多，等待音频追赶
                return baseDelay + MAX_SYNC_DIFF * 0.5;
            }
            else if (diff < -MAX_SYNC_DIFF)
            {
                // 视频落后太多，尽快显示
                return Math.Max(0, baseDelay * 0.5);
            }
            else if (diff > 0)
            {
                // 视频稍快，稍微减慢
                return baseDelay - (diff * 1);
            }
            else
            {
                // 视频稍慢，加快显示
                return Math.Max(0, baseDelay + diff);
            }
        }

        private double GetAudioClock()
        {
            lock (_clockLock)
            {
                if (_audioStarted)
                {
                    return _audioClock;
                }
                else
                {
                    // 音频未开始，使用系统时间
                    return GetCurrentTime();
                }
            }
        }

        private double GetCurrentTime()
        {
            return _renderStopwatch.Elapsed.TotalSeconds;
        }

        private unsafe void DisplayVideoFrame(AVFrameHolder frameHolder)
        {
            try
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

                    // 从对象池获取缓冲区
                    byte[] data = _videoBufferPool.Get();
                    if (data == null || data.Length < dataLen)
                    {
                        data = new byte[dataLen];
                    }

                    Marshal.Copy((IntPtr)convertedFrame.data[0], data, 0, dataLen);
                    _onVideoData?.Invoke(data);

                    // 归还到对象池
                    _videoBufferPool.Return(data);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"显示视频帧异常: {ex}");
            }
        }

        private unsafe void SetupLogging()
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
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

            // 在Unity中，通常不使用控制台输入，因此简化硬件解码器选择
            // 优先尝试DXVA2（Windows）或VideoToolbox（macOS）
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            deviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2;
            Debug.Log("Windows平台，尝试使用DXVA2硬件解码");
#elif UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
            deviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX;
            Debug.Log("macOS平台，尝试使用VideoToolbox硬件解码");
#endif

            var type = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            var number = 0;
            Debug.Log("可用的硬件解码器:");
            while ((type = ffmpeg.av_hwdevice_iterate_types(type)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                Debug.Log($"{++number}. {type}");
            }
        }

        private AVPixelFormat GetHWPixelFormat(AVHWDeviceType hWDevice)
        {
            return hWDevice switch
            {
                AVHWDeviceType.AV_HWDEVICE_TYPE_NONE => AVPixelFormat.AV_PIX_FMT_NONE,
                AVHWDeviceType.AV_HWDEVICE_TYPE_VDPAU => AVPixelFormat.AV_PIX_FMT_VDPAU,
                AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => AVPixelFormat.AV_PIX_FMT_CUDA,
                AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI => AVPixelFormat.AV_PIX_FMT_VAAPI,
                AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2 => AVPixelFormat.AV_PIX_FMT_NV12,
                AVHWDeviceType.AV_HWDEVICE_TYPE_QSV => AVPixelFormat.AV_PIX_FMT_QSV,
                AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX => AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX,
                AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => AVPixelFormat.AV_PIX_FMT_NV12,
                AVHWDeviceType.AV_HWDEVICE_TYPE_DRM => AVPixelFormat.AV_PIX_FMT_DRM_PRIME,
                AVHWDeviceType.AV_HWDEVICE_TYPE_OPENCL => AVPixelFormat.AV_PIX_FMT_OPENCL,
                AVHWDeviceType.AV_HWDEVICE_TYPE_MEDIACODEC => AVPixelFormat.AV_PIX_FMT_MEDIACODEC,
                _ => AVPixelFormat.AV_PIX_FMT_NONE,
            };
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            _isRunning = false;

            // 设置事件以唤醒等待的线程
            _frameReadyEvent?.Set();

            // 等待线程结束
            if (thread != null && thread.IsAlive)
            {
                thread.Join(3000);
                if (thread.IsAlive)
                {
                    thread.Abort();
                }
                thread = null;
            }

            if (renderThread != null && renderThread.IsAlive)
            {
                renderThread.Join(3000);
                if (renderThread.IsAlive)
                {
                    renderThread.Abort();
                }
                renderThread = null;
            }

            // 清空帧队列并释放资源
            while (_videoFrameQueue.TryDequeue(out AVFrameHolder frameHolder))
            {
                frameHolder?.Dispose();
            }

            _frameReadyEvent?.Close();
            _frameReadyEvent = null;

            _audioBufferPool?.Dispose();
            _videoBufferPool?.Dispose();

            // 关闭编解码器
            if (_pVideoContext != null)
            {
                ffmpeg.avcodec_close(_pVideoContext);
            }

            if (_pAudioContext != null)
            {
                ffmpeg.avcodec_close(_pAudioContext);
            }

            // 关闭格式上下文
            if (_pFormatContext != null)
            {
                var pFormatContext = _pFormatContext;
                ffmpeg.avformat_close_input(&pFormatContext);
            }

            // 释放音频重采样上下文
            if (_audioSwrContext != null)
            {
                fixed (SwrContext** ptr = &_audioSwrContext)
                {
                    ffmpeg.swr_free(ptr);
                }
            }

            // 释放frames
            if (_audioFrame != null)
            {
                fixed (AVFrame** ptr = &_audioFrame)
                {
                    ffmpeg.av_frame_free(ptr);
                }
            }

            if (_videoFrame != null)
            {
                fixed (AVFrame** ptr = &_videoFrame)
                {
                    ffmpeg.av_frame_free(ptr);
                }
            }

            if (_g2cFrame != null)
            {
                fixed (AVFrame** ptr = &_g2cFrame)
                {
                    ffmpeg.av_frame_free(ptr);
                }
            }

            if (_packet != null)
            {
                fixed (AVPacket** ptr = &_packet)
                {
                    ffmpeg.av_packet_free(ptr);
                }
            }

            _renderStopwatch.Stop();

            Debug.Log($"FFPlayer已释放，渲染了 {_renderedFrames} 帧，丢弃了 {_droppedFrames} 帧");
        }
    }

    /// <summary>
    /// AVFrame包装器，用于存储帧及其时间戳
    /// </summary>
    public unsafe class AVFrameHolder : IDisposable
    {
        public AVFrame* Frame { get; private set; }
        public double Pts { get; private set; }
        public Size FrameSize { get; private set; }
        public AVPixelFormat PixelFormat { get; private set; }

        public AVFrameHolder(AVFrame* frame, double pts, Size frameSize, AVPixelFormat pixelFormat)
        {
            Frame = ffmpeg.av_frame_alloc();
            if (Frame != null && frame != null)
            {
                ffmpeg.av_frame_ref(Frame, frame);
            }
            Pts = pts;
            FrameSize = frameSize;
            PixelFormat = pixelFormat;
        }

        public void Dispose()
        {
            if (Frame != null)
            {
                ffmpeg.av_frame_unref(Frame);
                //fixed (AVFrame** ptr = &Frame)
                //{
                //    ffmpeg.av_frame_free(ptr);
                //}
                Frame = null;
            }
        }
    }

    /// <summary>
    /// 简单的对象池实现，复用对象以减少GC压力
    /// </summary>
    public class ObjectPool<T> : IDisposable where T : class
    {
        private readonly ConcurrentBag<T> _objects;
        private readonly Func<T> _objectGenerator;
        private readonly int _maxSize;

        public ObjectPool(Func<T> objectGenerator, int maxSize = 5)
        {
            _objectGenerator = objectGenerator ?? throw new ArgumentNullException(nameof(objectGenerator));
            _objects = new ConcurrentBag<T>();
            _maxSize = maxSize;
        }

        public T Get()
        {
            return _objects.TryTake(out T item) ? item : null;
        }

        public void Return(T item)
        {
            if (item != null && _objects.Count < _maxSize)
            {
                _objects.Add(item);
            }
        }

        public void Dispose()
        {
            while (_objects.TryTake(out T item))
            {
                (item as IDisposable)?.Dispose();
            }
        }
    }
}