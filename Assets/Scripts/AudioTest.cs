using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Example;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

public class AudioTest : MonoBehaviour
{
    Thread thread = null;
    AudioStreamDecoder asd;
    string filePath;

    // Start is called before the first frame update
    void Start()
    {
        Loom.Initialize();
        filePath = Application.streamingAssetsPath + "/";
        ffmpeg.RootPath = Application.streamingAssetsPath + "/FFmpeg/x86_64";
        Debug.LogWarning($"FFmpeg version info: {ffmpeg.av_version_info()}");
        SetupLogging();
        thread = new Thread(new ThreadStart(DecodeTest));
        thread.IsBackground = true;
        thread.Start();
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

    private void DecodeTest()
    {
        DecodeAllFramesToImages();
    }

    private unsafe void DecodeAllFramesToImages()
    {
        //var url = "rtmp://58.200.131.2:1935/livetv/hunantv";
        var url = Application.streamingAssetsPath + "/test.mp4";
        asd = new AudioStreamDecoder(url);
        while (true)
        {
            if (asd.TryDecodeNextFrame(out var frame))
            {

            }
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
        asd.Dispose();
    }
}