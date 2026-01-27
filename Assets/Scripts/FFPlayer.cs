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
        private double _audioStartTime;

        // 线程控制 - 添加停止标志
        Thread thread = null;
        Thread renderThread;
        private volatile bool _isRunning = true;
        private volatile bool _isDisposed = false;

        // 对象池，复用byte数组以减少GC压力
        private ObjectPool<byte[]> _audioBufferPool;
        private ObjectPool<byte[]> _videoBufferPool;

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


            float _videoFrameRate=0;
            AVStream* videoStream = _pFormatContext->streams[_videoStreamIndex];
            AVRational frameRate = videoStream->r_frame_rate;
            if (frameRate.den != 0)
            {
                _videoFrameRate = frameRate.num / frameRate.den;
            }

            // 方法2：如果r_frame_rate无效，尝试从avg_frame_rate
            if (_videoFrameRate <= 0 && videoStream->avg_frame_rate.den != 0)
            {
                _videoFrameRate = videoStream->avg_frame_rate.num / videoStream->avg_frame_rate.den;
            }

            // 方法3：如果还是无效，从时间基准估算
            if (_videoFrameRate <= 0 && videoStream->time_base.num != 0)
            {
                _videoFrameRate = videoStream->time_base.den / videoStream->time_base.num;
            }

            // 默认值：如果仍然无效则使用30fps
            if (_videoFrameRate <= 0)
            {
                _videoFrameRate = 30.0f;
                Debug.LogWarning($"无法获取帧率，使用默认值: 30 FPS");
            }
            Debug.LogWarning("_videoFrameRate:" + _videoFrameRate);
             
            _frameSize = new Size(_pVideoContext->width, _pVideoContext->height);
            if (_onVideoSize != null)
            {
                _onVideoSize(_frameSize.Width, _frameSize.Height);
            }

            _pixelFormat = _pVideoContext->pix_fmt;
            _sample_fmt = _pVideoContext->sample_fmt;

            // 初始化音频重采样上下文
            InitializeAudioContext();

            // 初始化对象池
            int videoBufferSize = _frameSize.Width * _frameSize.Height * 3;
            _videoBufferPool = new ObjectPool<byte[]>(() => new byte[videoBufferSize], 3);
            _audioBufferPool = new ObjectPool<byte[]>(() => new byte[2 * 44100], 3);

            _packet = ffmpeg.av_packet_alloc();
            _audioFrame = ffmpeg.av_frame_alloc();
            _videoFrame = ffmpeg.av_frame_alloc();
            _g2cFrame = ffmpeg.av_frame_alloc();

            // 初始化同步变量
            _audioClock = 0;
            _videoClock = 0;
            _frameTimer = 0;
            _frameDelay = 1.0 / 30;

            // 启动解码线程
            thread = new Thread(new ThreadStart(DecodeThread));
            thread.IsBackground = true;
            thread.Start();

            // 启动视频渲染线程
            renderThread = new Thread(new ThreadStart(RenderThread));
            renderThread.IsBackground = true;
            renderThread.Start();
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
            Debug.LogWarning($"FFmpeg version info: {ffmpeg.av_version_info()}");
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
                        Thread.Sleep(10);
                        continue;
                    }

                    // 读取数据包
                    error = ffmpeg.av_read_frame(_pFormatContext, _packet);
                    if (error == ffmpeg.AVERROR_EOF)
                    {
                        Debug.LogWarning("End of file reached");
                        break;
                    }

                    if (error < 0)
                    {
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
                Debug.LogError($"DecodeThread Exception: {ex}");
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
                    double pts = _audioFrame->best_effort_timestamp *
                                ffmpeg.av_q2d(_pFormatContext->streams[_audioStreamIndex]->time_base);

                    lock (_clockLock)
                    {
                        _audioClock = pts;
                        if (_audioStartTime == 0)
                        {
                            _audioStartTime = pts;
                        }
                    }

                    // 使用栈缓冲区代替HeapAlloc，减少GC压力
                    int maxOutputSize = 2 * 44100;
                    byte* out_buffer = stackalloc byte[maxOutputSize];

                    ffmpeg.swr_convert(_audioSwrContext, &out_buffer, 2 * 44100,
                                      (byte**)&_audioFrame->data, _audioFrame->nb_samples);

                    int out_buffer_size = ffmpeg.av_samples_get_buffer_size(null, outChannelCount,
                                                                          _audioFrame->nb_samples, outFormat, 1);

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
                    double pts = _videoFrame->best_effort_timestamp *
                                ffmpeg.av_q2d(_pFormatContext->streams[_videoStreamIndex]->time_base);

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
                        Debug.LogWarning("Video queue full, dropping frame");
                        ffmpeg.av_frame_unref(_videoFrame);
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"ProcessVideoPacket Exception: {ex}");
                    ffmpeg.av_frame_unref(_videoFrame);
                }
            }
        }

        private void RenderThread()
        {
            try
            {
                while (_isRunning && !_isDisposed)
                {
                    // 等待帧可用，使用超时避免线程无限等待
                    if (!_frameReadyEvent.WaitOne(100))
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
                                Thread.Sleep((int)(delay * 1000));
                            }

                            // 转换并显示帧
                            DisplayVideoFrame(frameHolder);

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
                Debug.LogError($"RenderThread Exception: {ex}");
            }
        }

        private double CalculateFrameDelay(double pts)
        {
            double actualDelay, delay;

            double timeSinceLastFrame = GetCurrentTime() - _frameTimer;
            delay = _frameDelay - timeSinceLastFrame;

            double refClock = _useAudioClock ?
                GetAudioClock() :
                GetCurrentTime() - _audioStartTime;

            double diff = pts - refClock;

            const double syncThreshold = 0.05;

            if (Math.Abs(diff) < syncThreshold)
            {
                actualDelay = delay;
            }
            else if (diff > 0)
            {
                actualDelay = Math.Max(0, delay + diff);
            }
            else
            {
                actualDelay = delay + diff;
            }

            _frameTimer = GetCurrentTime();
            actualDelay = Math.Max(0, Math.Min(actualDelay, 0.5));

            return actualDelay;
        }

        private double GetAudioClock()
        {
            lock (_clockLock)
            {
                return _audioClock;
            }
        }

        private double GetCurrentTime()
        {
            return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
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
                Debug.LogError($"DisplayVideoFrame Exception: {ex}");
            }
        }

        private unsafe void SetupLogging()
        {
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_VERBOSE);
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

            // 等待线程结束
            if (thread != null && thread.IsAlive)
            {
                thread.Join(5000); // 最多等待5秒
                if (thread.IsAlive)
                {
                    thread.Abort();
                }
            }

            if (renderThread != null && renderThread.IsAlive)
            {
                renderThread.Join(5000);
                if (renderThread.IsAlive)
                {
                    renderThread.Abort();
                }
            }

            // 清空帧队列并释放资源
            while (_videoFrameQueue.TryDequeue(out AVFrameHolder frameHolder))
            {
                frameHolder?.Dispose();
            }

            _frameReadyEvent?.Dispose();
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
                ffmpeg.av_frame_unref(_audioFrame);
                fixed (AVFrame** ptr = &_audioFrame)
                {
                    ffmpeg.av_frame_free(ptr);
                }
            }

            if (_videoFrame != null)
            {
                ffmpeg.av_frame_unref(_videoFrame);
                fixed (AVFrame** ptr = &_videoFrame)
                {
                    ffmpeg.av_frame_free(ptr);
                }
            }

            if (_g2cFrame != null)
            {
                ffmpeg.av_frame_unref(_g2cFrame);
                fixed (AVFrame** ptr = &_g2cFrame)
                {
                    ffmpeg.av_frame_free(ptr);
                }
            }

            if (_packet != null)
            {
                ffmpeg.av_packet_unref(_packet);
                fixed (AVPacket** ptr = &_packet)
                {
                    ffmpeg.av_packet_free(ptr);
                }
            }

            Debug.LogWarning("FFPlayer disposed successfully");
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
            if (Frame != null)
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