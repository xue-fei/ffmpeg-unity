using NAudio.Wave;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

namespace FFmpeg.AutoGen.Example
{
    public sealed unsafe class AudioStreamDecoder : IDisposable
    {
        private readonly AVCodecContext* _pAudioContext;
        private readonly AVFormatContext* _pFormatContext;
        private readonly int _audioStreamIndex;
        private readonly AVPacket* _pPacket;
        private readonly AVFrame* _pFrame;
        AVSampleFormat inFormat;
        AVSampleFormat outFormat;
        SwrContext* swrContext;

        int inSampleRate;
        int outSampleRate;
        ulong in_ch_layout;
        int out_ch_layout;
        int outChannelCount;

        WaveOut waveOut;            //播放器
        BufferedWaveProvider bufferedWaveProvider;       //5s缓存区

        public AudioStreamDecoder(string url)
        {
            waveOut = new WaveOut();
            WaveFormat wf = new WaveFormat(44100, 2);
            bufferedWaveProvider = new BufferedWaveProvider(wf);
            waveOut.Init(bufferedWaveProvider);
            waveOut.Play(); 

            _pFormatContext = ffmpeg.avformat_alloc_context();
            var pFormatContext = _pFormatContext;
            ffmpeg.avformat_open_input(&pFormatContext, url, null, null).ThrowExceptionIfError();
            ffmpeg.avformat_find_stream_info(_pFormatContext, null).ThrowExceptionIfError();
            AVCodec* audioCodec = null;
            _audioStreamIndex = ffmpeg.av_find_best_stream(_pFormatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &audioCodec, 0).ThrowExceptionIfError();
            UnityEngine.Debug.LogWarning("_audioStreamIndex:" + _audioStreamIndex);
            _pAudioContext = ffmpeg.avcodec_alloc_context3(audioCodec);
            // 拿到对应音频流的参数
            AVCodecParameters* avCodecParameters = _pFormatContext->streams[_audioStreamIndex]->codecpar;
            // 将新的API中的 codecpar 转成 AVCodecContext
            ffmpeg.avcodec_parameters_to_context(_pAudioContext, avCodecParameters);
            ffmpeg.avcodec_open2(_pAudioContext, audioCodec, null).ThrowExceptionIfError();

            //压缩数据包
            _pPacket = ffmpeg.av_packet_alloc();
            //解压缩后存放的数据帧的对象
            _pFrame = ffmpeg.av_frame_alloc();
            //frame->16bit 44100 PCM 统一音频采样格式与采样率
            //创建swrcontext上下文件
            swrContext = ffmpeg.swr_alloc();
            //音频格式  输入的采样设置参数
            inFormat = _pAudioContext->sample_fmt;
            // 出入的采样格式
            outFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;
            // 输入采样率
            inSampleRate = _pAudioContext->sample_rate;
            // 输出采样率
            outSampleRate = 44100;
            // 输入声道布局
            in_ch_layout = _pAudioContext->channel_layout;
            //输出声道布局
            out_ch_layout = ffmpeg.AV_CH_LAYOUT_STEREO;
            //给Swrcontext 分配空间，设置公共参数
            ffmpeg.swr_alloc_set_opts(swrContext, out_ch_layout, outFormat, outSampleRate,
                    (long)in_ch_layout, inFormat, inSampleRate, 0, null
                    );
            // 初始化
            ffmpeg.swr_init(swrContext);
            // 获取声道数量
            outChannelCount = ffmpeg.av_get_channel_layout_nb_channels((ulong)out_ch_layout);
            Debug.LogWarning("声道数量%d " + outChannelCount);
        }

        int currentIndex = 0;

        public bool TryDecodeNextFrame(out byte[] frame)
        {
            // 设置音频缓冲区间 16bit   44100  PCM数据, 双声道
            byte* out_buffer = (byte*)Marshal.AllocHGlobal(2 * 44100);
            //开始读取源文件，进行解码
            if (ffmpeg.av_read_frame(_pFormatContext, _pPacket) >= 0)
            {
                if (_pPacket->stream_index == _audioStreamIndex)
                {
                    ffmpeg.avcodec_send_packet(_pAudioContext, _pPacket);
                    //解码
                    int ret = ffmpeg.avcodec_receive_frame(_pAudioContext, _pFrame);
                    if (ret == 0)
                    {
                        //将每一帧数据转换成pcm
                        ret = ffmpeg.swr_convert(swrContext, &out_buffer, 2 * 44100,
                                    (byte**)&_pFrame->data, _pFrame->nb_samples);
                        //获取实际的缓存大小
                        int out_buffer_size = ffmpeg.av_samples_get_buffer_size(null, outChannelCount, _pFrame->nb_samples, outFormat, 1);
                        byte[] write = new byte[out_buffer_size];
                        Marshal.Copy((IntPtr)out_buffer, write, 0, write.Length);
                        bufferedWaveProvider.AddSamples(write, 0, out_buffer_size);
                        frame = write;
                        Debug.LogWarning("正在解码%d" + currentIndex++);
                        Thread.Sleep(22);
                        return true;
                    }
                    else
                    {
                        frame = null;
                        return false;
                    } 
                }
                else
                {
                    frame = null;
                    return false;
                }
            }
            else
            {
                frame = null;
                return false;
            }
        }

        public void Dispose()
        {
            waveOut.Stop();
            waveOut.Dispose();
            ffmpeg.av_frame_unref(_pFrame);
            ffmpeg.av_free(_pFrame);

            ffmpeg.av_packet_unref(_pPacket);
            ffmpeg.av_free(_pPacket);

            ffmpeg.avcodec_close(_pAudioContext);

            var pFormatContext = _pFormatContext;
            ffmpeg.avformat_close_input(&pFormatContext);
        }
    }
}