using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Debug = UnityEngine.Debug;

namespace FFmpeg.AutoGen
{
    internal static class FFmpegHelper
    {
        public static void Init()
        {
            ffmpeg.RootPath = UnityEngine.Application.streamingAssetsPath + "/FFmpeg/x86_64";
            SetupLogging();
        }

        private static unsafe void SetupLogging()
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
                UnityEngine.Debug.LogWarning(line);
            };
            ffmpeg.av_log_set_callback(logCallback);
        }

        public static unsafe string av_strerror(int error)
        {
            var bufferSize = 1024;
            var buffer = stackalloc byte[bufferSize];
            ffmpeg.av_strerror(error, buffer, (ulong)bufferSize);
            var message = Marshal.PtrToStringAnsi((IntPtr)buffer);
            return message;
        }

        public static int ThrowExceptionIfError(this int error)
        {
            if (error < 0) throw new ApplicationException(av_strerror(error));
            return error;
        } 

        public static AVHWDeviceType GetHWDecoder()
        {
            AVHWDeviceType deviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
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
                return deviceType;
            }
            int decoderNumber = availableHWDecoders.SingleOrDefault(t => t.Value == AVHWDeviceType.AV_HWDEVICE_TYPE_DXVA2).Key;
            if (decoderNumber == 0)
            {
                decoderNumber = availableHWDecoders.First().Key;
            }
            Debug.LogWarning($"Selected [{decoderNumber}]");
            availableHWDecoders.TryGetValue(decoderNumber, out deviceType);
            return deviceType;
        }
    }
}