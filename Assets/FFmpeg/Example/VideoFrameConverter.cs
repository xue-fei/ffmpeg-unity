using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace FFmpeg.AutoGen.Example
{
    public sealed unsafe class VideoFrameConverter : IDisposable
    {
        private readonly IntPtr FrameBufferPtr;
        private readonly Size _destinationSize;
        private readonly byte_ptrArray4 TargetData;
        private readonly int_array4 TargetLinesize;
        private readonly SwsContext* convert;

        public VideoFrameConverter(Size sourceSize, AVPixelFormat sourcePixelFormat,
            Size destinationSize, AVPixelFormat destinationPixelFormat)
        {
            _destinationSize = destinationSize;

            convert = ffmpeg.sws_getContext(sourceSize.Width, sourceSize.Height, sourcePixelFormat,
            destinationSize.Width,
            destinationSize.Height, destinationPixelFormat,
            ffmpeg.SWS_FAST_BILINEAR, null, null, null);
            if (convert == null) throw new ApplicationException("Could not initialize the conversion context.");

            var bufferSize = ffmpeg.av_image_get_buffer_size(destinationPixelFormat, destinationSize.Width, destinationSize.Height, 1);
            FrameBufferPtr = Marshal.AllocHGlobal(bufferSize);
            TargetData = new byte_ptrArray4();
            TargetLinesize = new int_array4();

            ffmpeg.av_image_fill_arrays(ref TargetData, ref TargetLinesize, (byte*)FrameBufferPtr, destinationPixelFormat, destinationSize.Width, destinationSize.Height, 1);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(FrameBufferPtr);
            ffmpeg.sws_freeContext(convert);
        }

        public AVFrame Convert(AVFrame sourceFrame)
        {
            //翻转后颜色不正常
            //sourceFrame.data[0] += sourceFrame.linesize[0] * (sourceFrame.height - 1);
            //sourceFrame.linesize[0] = -sourceFrame.linesize[0];
            ffmpeg.sws_scale(convert, sourceFrame.data, sourceFrame.linesize, 0, sourceFrame.height, TargetData, TargetLinesize);

            var data = new byte_ptrArray8();
            data.UpdateFrom(TargetData);
            var linesize = new int_array8();
            linesize.UpdateFrom(TargetLinesize);

            return new AVFrame
            {
                data = data,
                linesize = linesize,
                width = _destinationSize.Width,
                height = _destinationSize.Height
            };
        }
    }
}