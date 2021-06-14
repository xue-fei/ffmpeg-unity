using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

public class Test : MonoBehaviour
{
    Thread thread = null;
    AVHWDeviceType deviceType;
    string filePath;
    Texture2D texture2D;
    Queue<byte[]> imgQueue = new Queue<byte[]>();
    public RawImage rawImage;

    // Start is called before the first frame update
    void Start()
    {
        Loom.Initialize();
        filePath = Application.streamingAssetsPath + "/";
        ffmpeg.RootPath = Application.streamingAssetsPath + "/FFmpeg/x86_64";
        Debug.LogWarning($"FFmpeg version info: {ffmpeg.av_version_info()}");
        SetupLogging();
        ConfigureHWDecoder(out var deviceType);
        thread = new Thread(new ThreadStart(DecodeTest));
        thread.IsBackground = true;
        thread.Start();
    }

    byte[] data;
    private void FixedUpdate()
    {
        if (imgQueue.Count > 0)
        {
            data = imgQueue.Dequeue();
            if (texture2D != null)
            {
                texture2D.LoadRawTextureData(data);
                texture2D.Apply();
            }
        }
    }

    private static unsafe void SetupLogging()
    {
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_VERBOSE);
        // do not convert to local function
        av_log_set_callback_callback logCallback = (p0, level, format, vl) =>
        {
            if (level > ffmpeg.av_log_get_level()) return;

            var lineSize = 1024;
            var lineBuffer = stackalloc byte[lineSize];
            var printPrefix = 1;
            ffmpeg.av_log_format_line(p0, level, format, vl, lineBuffer, lineSize, &printPrefix);
            var line = Marshal.PtrToStringAnsi((IntPtr)lineBuffer);
            Debug.LogWarning(line);
        };

        ffmpeg.av_log_set_callback(logCallback);
    }

    private static void ConfigureHWDecoder(out AVHWDeviceType HWtype)
    {
        HWtype = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        var availableHWDecoders = new Dictionary<int, AVHWDeviceType>();

        var type = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        var number = 0;
        while ((type = ffmpeg.av_hwdevice_iterate_types(type)) != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            Debug.LogWarning($"{++number}. {type}");
            availableHWDecoders.Add(number, type);
        }
        if (availableHWDecoders.Count == 0)
        {
            Debug.LogWarning("Your system have no hardware decoders.");
            HWtype = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
            return;
        }
        int decoderNumber = availableHWDecoders.SingleOrDefault(t => t.Value == AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2).Key;
        if (decoderNumber == 0)
        {
            decoderNumber = availableHWDecoders.First().Key;
        }
        Debug.LogWarning($"Selected [{decoderNumber}]");
        int.TryParse(Console.ReadLine(), out var inputDecoderNumber);
        availableHWDecoders.TryGetValue(inputDecoderNumber == 0 ? decoderNumber : inputDecoderNumber, out HWtype);
    }

    private void DecodeTest()
    {
        DecodeAllFramesToImages(deviceType);
    }

    private unsafe void DecodeAllFramesToImages(AVHWDeviceType HWDevice)
    {
        //var url = "rtmp://58.200.131.2:1935/livetv/hunantv";
        var url = Application.streamingAssetsPath + "/test.mp4";
        using (var vsd = new VideoStreamDecoder(url, HWDevice))
        {
            Debug.LogWarning($"VideoCodecName: {vsd.VideoCodecName}");
            Debug.LogWarning($"AudioCodecName: {vsd.AudioCodecName}");
            var info = vsd.GetContextInfo();
            info.ToList().ForEach(x => Console.WriteLine($"{x.Key} = {x.Value}"));

            var sourceSize = vsd.FrameSize;
            var sourcePixelFormat = HWDevice == AVHWDeviceType.AV_HWDEVICE_TYPE_NONE ? vsd.PixelFormat : GetHWPixelFormat(HWDevice);
            var destinationSize = sourceSize;
            var destinationPixelFormat = AVPixelFormat.AV_PIX_FMT_RGB24;
            int dataLen = sourceSize.Width * sourceSize.Height * 3;
            Loom.QueueOnMainThread(() =>
            {
                texture2D = new Texture2D(sourceSize.Width, sourceSize.Height, TextureFormat.RGB24, false);
                texture2D.Apply();
                rawImage.texture = texture2D;
            });
            byte[] tempData = new byte[dataLen];
            using (var vfc = new VideoFrameConverter(sourceSize, sourcePixelFormat, destinationSize, destinationPixelFormat))
            {
                var frameNumber = 0;
                while (vsd.TryDecodeNextFrame(out var frame))
                {
                    AVFrame convertedFrame = vfc.Convert(frame);
                    IntPtr imgPtr = (IntPtr)convertedFrame.data[0];
                    Marshal.Copy((IntPtr)convertedFrame.data[0], tempData, 0, tempData.Length);
                    imgQueue.Enqueue(tempData);
                    int bufferSize = ffmpeg.av_samples_get_buffer_size(null, frame.channels, frame.nb_samples, vsd.Sample_fmt, 1);
                    //frame.
                    frameNumber++;
                }
            }
        }
    }

    // Update is called once per frame
    void Update()
    {

    }

    private static AVPixelFormat GetHWPixelFormat(AVHWDeviceType hWDevice)
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

    private void OnApplicationQuit()
    {
        if (thread != null)
        {
            if (thread.IsAlive)
            {
                thread.Abort();
            }
        }
    }
}