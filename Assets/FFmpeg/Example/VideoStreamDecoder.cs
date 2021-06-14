using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace FFmpeg.AutoGen.Example
{
    public sealed unsafe class VideoStreamDecoder : IDisposable
    {
        private readonly AVCodecContext* _pVideoContext;
        private readonly AVCodecContext* _pAudioContext;
        private readonly AVFormatContext* _pFormatContext;
        private readonly int _videoStreamIndex;
        private readonly int _audioStreamIndex;
        private readonly int* _got_frame_ptr;
        private readonly AVFrame* _pFrame;
        private readonly AVFrame* _receivedFrame;
        private readonly AVPacket* _pPacket;

        public VideoStreamDecoder(string url, AVHWDeviceType HWDeviceType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
        {
            _pFormatContext = ffmpeg.avformat_alloc_context();
            _receivedFrame = ffmpeg.av_frame_alloc();
            var pFormatContext = _pFormatContext;
            ffmpeg.avformat_open_input(&pFormatContext, url, null, null).ThrowExceptionIfError();
            ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();
            AVCodec* videoCodec = null;
            _videoStreamIndex = ffmpeg.av_find_best_stream(_pFormatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &videoCodec, 0).ThrowExceptionIfError();
            AVCodec* audioCodec = null;
            _audioStreamIndex = ffmpeg.av_find_best_stream(_pFormatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &audioCodec, 0).ThrowExceptionIfError();
            UnityEngine.Debug.LogWarning("_videoStreamIndex:" + _videoStreamIndex);
            UnityEngine.Debug.LogWarning("_audioStreamIndex:" + _audioStreamIndex);

            _pVideoContext = ffmpeg.avcodec_alloc_context3(videoCodec);
            _pAudioContext = ffmpeg.avcodec_alloc_context3(audioCodec);
            if (HWDeviceType != AVHWDeviceType.AV_HWDEVICE_TYPE_NONE)
            {
                ffmpeg.av_hwdevice_ctx_create(&_pVideoContext->hw_device_ctx, HWDeviceType, null, null, 0).ThrowExceptionIfError();
            }
            ffmpeg.avcodec_parameters_to_context(_pVideoContext, _pFormatContext->streams[_videoStreamIndex]->codecpar).ThrowExceptionIfError();
            ffmpeg.avcodec_parameters_to_context(_pAudioContext, _pFormatContext->streams[_audioStreamIndex]->codecpar).ThrowExceptionIfError();
            ffmpeg.avcodec_open2(_pVideoContext, videoCodec, null).ThrowExceptionIfError();
            ffmpeg.avcodec_open2(_pAudioContext, audioCodec, null).ThrowExceptionIfError();

            VideoCodecName = ffmpeg.avcodec_get_name(videoCodec->id);
            AudioCodecName = ffmpeg.avcodec_get_name(audioCodec->id);
            FrameSize = new Size(_pVideoContext->width, _pVideoContext->height);
            PixelFormat = _pVideoContext->pix_fmt;
            Sample_fmt = _pVideoContext->sample_fmt;
            _pPacket = ffmpeg.av_packet_alloc();
            _pFrame = ffmpeg.av_frame_alloc();
        }

        public string VideoCodecName { get; }
        public string AudioCodecName { get; }
        public Size FrameSize { get; }
        public AVPixelFormat PixelFormat { get; }
        public AVSampleFormat Sample_fmt { get; }

        public void Dispose()
        {
            ffmpeg.av_frame_unref(_pFrame);
            ffmpeg.av_free(_pFrame);

            ffmpeg.av_packet_unref(_pPacket);
            ffmpeg.av_free(_pPacket);

            ffmpeg.avcodec_close(_pVideoContext);
            ffmpeg.avcodec_close(_pAudioContext);

            var pFormatContext = _pFormatContext;
            ffmpeg.avformat_close_input(&pFormatContext);
        }

        public bool TryDecodeNextFrame(out AVFrame frame)
        {
            ffmpeg.av_frame_unref(_pFrame);
            ffmpeg.av_frame_unref(_receivedFrame);
            int error;
            do
            {
                try
                {
                    //do
                    //{
                    error = ffmpeg.av_read_frame(_pFormatContext, _pPacket);
                    if (error == ffmpeg.AVERROR_EOF)
                    {
                        frame = *_pFrame;
                        return false;
                    }
                    error.ThrowExceptionIfError();
                    //} while (_pPacket->stream_index != _videoStreamIndex);

                    if (_pPacket->stream_index == _videoStreamIndex)
                    {
                        ffmpeg.avcodec_send_packet(_pVideoContext, _pPacket).ThrowExceptionIfError();
                    }
                    if (_pPacket->stream_index == _audioStreamIndex)
                    {
                        //ffmpeg.avcodec_send_packet(_pAudioContext, _pPacket).ThrowExceptionIfError();
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(_pPacket);
                }
                error = ffmpeg.avcodec_receive_frame(_pVideoContext, _pFrame);
                //error = ffmpeg.avcodec_receive_frame(_pAudioContext, _pFrame);
            }
            while (error == ffmpeg.AVERROR(ffmpeg.EAGAIN));
            error.ThrowExceptionIfError();
            if (_pVideoContext->hw_device_ctx != null)
            {
                ffmpeg.av_hwframe_transfer_data(_receivedFrame, _pFrame, 0).ThrowExceptionIfError();
                frame = *_receivedFrame;
            }

            else if (_pAudioContext->hw_device_ctx != null)
            {
                ffmpeg.av_hwframe_transfer_data(_receivedFrame, _pFrame, 0).ThrowExceptionIfError();
                frame = *_receivedFrame;
            }
            else
            {
                frame = *_pFrame;
            }
            return true;
        }

        public IReadOnlyDictionary<string, string> GetContextInfo()
        {
            AVDictionaryEntry* tag = null;
            var result = new Dictionary<string, string>();
            while ((tag = ffmpeg.av_dict_get(_pFormatContext->metadata, "", tag, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
            {
                var key = Marshal.PtrToStringAnsi((IntPtr)tag->key);
                var value = Marshal.PtrToStringAnsi((IntPtr)tag->value);
                result.Add(key, value);
            }
            return result;
        }
    }
}