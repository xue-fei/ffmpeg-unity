using FFmpeg.AutoGen;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static IMedia;
using Debug = UnityEngine.Debug;

public unsafe class VideoDecoder : IMedia
{
    //媒体格式上下文（媒体容器）
    AVFormatContext* format;
    //编解码上下文
    AVCodecContext* codecContext;
    //媒体数据包
    AVPacket* packet;
    //媒体帧数据
    AVFrame* frame;
    //图像转换器
    SwsContext* convert;
    AVHWDeviceType deviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
    //视频流
    AVStream* videoStream;
    // 视频流在媒体容器上流的索引
    int videoStreamIndex;
    TimeSpan OffsetClock;
    //帧，数据指针
    IntPtr FrameBufferPtr;
    byte_ptrArray4 TargetData;
    int_array4 TargetLinesize;
    object SyncLock = new object();
    //时钟
    Stopwatch clock = new Stopwatch();
    //播放上一帧的时间
    TimeSpan lastTime;
    bool isNextFrame = true;
    public event MediaHandler MediaCompleted;
    public event MediaHandler MediaPlay;
    public event MediaHandler MediaPause;
    #region
    //视频时长
    public TimeSpan Duration { get; protected set; }
    //编解码器名字
    public string CodecName { get; protected set; }
    public string CodecId { get; protected set; }
    //比特率
    public int Bitrate { get; protected set; }
    //帧率
    public double FrameRate { get; protected set; }
    //图像的高和款
    public int FrameWidth { get; protected set; }
    public int FrameHeight { get; protected set; }

    //是否是正在播放中
    public bool IsPlaying { get; protected set; }
    public MediaState State { get; protected set; }
    public TimeSpan Position { get => clock.Elapsed + OffsetClock; }

    //一帧显示时长
    public TimeSpan frameDuration { get; private set; }
    #endregion
    /// <summary>
    /// 初始化解码视频
    /// </summary>
    /// <param name="path"></param>
    public void InitDecodecVideo(string path)
    {
        int error = 0;
        //创建一个 媒体格式上下文
        format = ffmpeg.avformat_alloc_context();
        if (format == null)
        {
            Debug.LogError("创建媒体格式（容器）失败");
            return;
        }
        var tempFormat = format;
        //打开视频
        error = ffmpeg.avformat_open_input(&tempFormat, path, null, null);
        if (error < 0)
        {
            Debug.LogError("打开视频失败");
            return;
        }
        //嗅探媒体信息
        ffmpeg.avformat_find_stream_info(format, null);
        //编解码器类型
        AVCodec* codec = null;
        //获取视频流索引
        videoStreamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &codec, 0);
        if (videoStreamIndex < 0)
        {
            Debug.LogError("没有找到视频流");
            return;
        }
        //根据流索引找到视频流
        videoStream = format->streams[videoStreamIndex];
        //创建解码器上下文
        codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (deviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            ffmpeg.av_hwdevice_ctx_create(&codecContext->hw_device_ctx, deviceType, null, null, 0).ThrowExceptionIfError();
        }
        //将视频流里面的解码器参数设置到 解码器上下文中
        error = ffmpeg.avcodec_parameters_to_context(codecContext, videoStream->codecpar);
        if (error < 0)
        {
            Debug.LogError("设置解码器参数失败");
            return;
        }
        //打开解码器
        error = ffmpeg.avcodec_open2(codecContext, codec, null);
        if (error < 0)
        {
            Debug.LogError("打开解码器失败");
            return;
        }
        //视频时长等视频信息
        //Duration = TimeSpan.FromMilliseconds(videoStream->duration / ffmpeg.av_q2d(videoStream->time_base));
        Duration = TimeSpan.FromMilliseconds(format->duration / 1000);
        CodecId = videoStream->codecpar->codec_id.ToString();
        CodecName = ffmpeg.avcodec_get_name(videoStream->codecpar->codec_id);
        Bitrate = (int)videoStream->codecpar->bit_rate;
        FrameRate = ffmpeg.av_q2d(videoStream->r_frame_rate);
        FrameWidth = codecContext->width;
        FrameHeight = codecContext->height;
        Debug.LogWarning(FrameWidth + " " + FrameHeight);
        frameDuration = TimeSpan.FromMilliseconds(1000 / FrameRate);
        //初始化转换器，将图片从源格式 转换成 BGR0 （8:8:8）格式 
        var result = InitConvert(FrameWidth, FrameHeight, codecContext->pix_fmt, FrameWidth, FrameHeight, AVPixelFormat.AV_PIX_FMT_BGR0);
        //所有内容都初始化成功了开启时钟，用来记录时间
        if (result)
        {
            //从内存中分配控件给 packet 和frame
            packet = ffmpeg.av_packet_alloc();
            frame = ffmpeg.av_frame_alloc();
        }
        bytes = new byte[FrameWidth * FrameHeight * 4];
    }

    byte_ptrArray8 bpa;
    int_array8 linesize;
    /// <summary>
    /// 初始化转换器
    /// </summary>
    /// <param name="sourceWidth">源宽度</param>
    /// <param name="sourceHeight">源高度</param>
    /// <param name="sourceFormat">源格式</param>
    /// <param name="targetWidth">目标高度</param>
    /// <param name="targetHeight">目标宽度</param>
    /// <param name="targetFormat">目标格式</param>
    /// <returns></returns>
    bool InitConvert(int sourceWidth, int sourceHeight, AVPixelFormat sourceFormat, int targetWidth, int targetHeight, AVPixelFormat targetFormat)
    {
        //根据输入参数和输出参数初始化转换器
        convert = ffmpeg.sws_getContext(sourceWidth, sourceHeight, sourceFormat,
            targetWidth, targetHeight, targetFormat,
            ffmpeg.SWS_FAST_BILINEAR, null, null, null);
        if (convert == null)
        {
            Debug.LogError("创建转换器失败");
            return false;
        }
        //获取转换后图像的 缓冲区大小
        var bufferSize = ffmpeg.av_image_get_buffer_size(targetFormat, targetWidth, targetHeight, 1);
        //创建一个指针
        FrameBufferPtr = Marshal.AllocHGlobal(bufferSize);
        TargetData = new byte_ptrArray4();
        TargetLinesize = new int_array4();
        bpa = new byte_ptrArray8();
        linesize = new int_array8();
        ffmpeg.av_image_fill_arrays(ref TargetData, ref TargetLinesize, (byte*)FrameBufferPtr, targetFormat, targetWidth, targetHeight, 1);
        return true;
    }
    byte[] bytes;
    public byte[] FrameConvertBytes(AVFrame* sourceFrame)
    {
        // 利用转换器将yuv 图像数据转换成指定的格式数据
        ffmpeg.sws_scale(convert, sourceFrame->data,
            sourceFrame->linesize, 0, sourceFrame->height,
            TargetData, TargetLinesize);
        
        bpa.UpdateFrom(TargetData); 
        linesize.UpdateFrom(TargetLinesize);
        //创建一个字节数据，将转换后的数据从内存中读取成字节数组 
        //byte[] bytes = new byte[FrameWidth * FrameHeight * 4];
        Marshal.Copy((IntPtr)bpa[0], bytes, 0, bytes.Length);
        return bytes;
    }
    public bool TryReadNextFrame(out AVFrame outFrame)
    {
        if (lastTime == TimeSpan.Zero)
        {
            lastTime = Position;
            isNextFrame = true;
        }
        else
        {
            if (Position - lastTime >= frameDuration)
            {
                lastTime = Position;
                isNextFrame = true;
            }
            else
            {
                outFrame = *frame;
                return false;
            }
        }
        if (isNextFrame)
        {
            lock (SyncLock)
            {
                int result = -1; 
                while (true)
                {
                    //清理上一帧的数据
                    ffmpeg.av_frame_unref(frame);
                    //清理上一帧的数据包
                    ffmpeg.av_packet_unref(packet);
                    //读取下一帧，返回一个int 查看读取数据包的状态
                    result = ffmpeg.av_read_frame(format, packet);
                    //读取了最后一帧了，没有数据了，退出读取帧
                    if (result == ffmpeg.AVERROR_EOF || result < 0)
                    {
                        outFrame = *frame;
                        StopPlay();
                        return false;
                    }
                    //判断读取的帧数据是否是视频数据，不是则继续读取
                    if (packet->stream_index != videoStreamIndex)
                        continue;

                    //将包数据发送给解码器解码
                    ffmpeg.avcodec_send_packet(codecContext, packet);
                    //从解码器中接收解码后的帧
                    result = ffmpeg.avcodec_receive_frame(codecContext, frame);
                    if (result < 0)
                        continue;
                    outFrame = *frame;
                    return true;
                }
            }
        }
        else
        {
            outFrame = *frame;
            return false;
        }
    }
    void StopPlay()
    {
        lock (SyncLock)
        {
            if (State == MediaState.None) return;
            IsPlaying = false;
            OffsetClock = TimeSpan.FromSeconds(0);
            clock.Reset();
            clock.Stop();
            var tempFormat = format;
            ffmpeg.avformat_free_context(tempFormat);
            format = null;
            var tempCodecContext = codecContext;
            ffmpeg.avcodec_free_context(&tempCodecContext);
            var tempPacket = packet;
            ffmpeg.av_packet_free(&tempPacket);
            var tempFrame = frame;
            ffmpeg.av_frame_free(&tempFrame);
            var tempConvert = convert;
            ffmpeg.sws_freeContext(convert);
            videoStream = null;
            videoStreamIndex = -1;
            //视频时长
            Duration = TimeSpan.FromMilliseconds(0);
            //编解码器名字
            CodecName = String.Empty;
            CodecId = String.Empty;
            //比特率
            Bitrate = 0;
            //帧率
            FrameRate = 0;
            //图像的高和款
            FrameWidth = 0;
            FrameHeight = 0;
            State = MediaState.None;
            Marshal.FreeHGlobal(FrameBufferPtr);
            lastTime = TimeSpan.Zero;
            MediaCompleted?.Invoke(Duration);
        }
    }
    /// <summary>
    /// 更改进度
    /// </summary>
    /// <param name="seekTime">更改到的位置（秒）</param>
    public void SeekProgress(int seekTime)
    {
        if (format == null || videoStream == null)
            return;
        lock (SyncLock)
        {
            IsPlaying = false;//将视频暂停播放
            clock.Stop();
            //将秒数转换成视频的时间戳
            var timestamp = seekTime / ffmpeg.av_q2d(videoStream->time_base);
            //将媒体容器里面的指定流（视频）的时间戳设置到指定的位置，并指定跳转的方法；
            ffmpeg.av_seek_frame(format, videoStreamIndex, (long)timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD | ffmpeg.AVSEEK_FLAG_FRAME);
            ffmpeg.av_frame_unref(frame);//清除上一帧的数据
            ffmpeg.av_packet_unref(packet); //清除上一帧的数据包
            int error = 0;
            //循环获取帧数据，判断获取的帧时间戳已经大于给定的时间戳则说明已经到达了指定的位置则退出循环
            while (packet->pts < timestamp)
            {
                do
                {
                    do
                    {
                        ffmpeg.av_packet_unref(packet);//清除上一帧数据包
                        error = ffmpeg.av_read_frame(format, packet);//读取数据
                        if (error == ffmpeg.AVERROR_EOF)//是否是到达了视频的结束位置
                            return;
                    } while (packet->stream_index != videoStreamIndex);//判断当前获取的数据是否是视频数据
                    ffmpeg.avcodec_send_packet(codecContext, packet);//将数据包发送给解码器解码
                    error = ffmpeg.avcodec_receive_frame(codecContext, frame);//从解码器获取解码后的帧数据
                } while (error == ffmpeg.AVERROR(ffmpeg.EAGAIN));
            }
            OffsetClock = TimeSpan.FromSeconds(seekTime);//设置时间偏移
            clock.Restart();//时钟从新开始
            IsPlaying = true;//视频开始播放
            lastTime = TimeSpan.Zero;
        }
    }
    public void Play()
    {
        if (State == MediaState.Play)
            return;
        clock.Start();
        IsPlaying = true;
        State = MediaState.Play;

    }
    public void Pause()
    {
        if (State != MediaState.Play)
            return;
        IsPlaying = false;
        OffsetClock = clock.Elapsed;
        clock.Stop();
        clock.Reset();

        State = MediaState.Pause;
    }
    public void Stop()
    {
        if (State == MediaState.None)
            return;
        StopPlay();
    }
}