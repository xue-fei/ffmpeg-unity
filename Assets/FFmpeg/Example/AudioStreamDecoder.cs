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

        WaveOut waveOut;            //������
        BufferedWaveProvider bufferedWaveProvider;       //5s������

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
            // �õ���Ӧ��Ƶ���Ĳ���
            AVCodecParameters* avCodecParameters = _pFormatContext->streams[_audioStreamIndex]->codecpar;
            // ���µ�API�е� codecpar ת�� AVCodecContext
            ffmpeg.avcodec_parameters_to_context(_pAudioContext, avCodecParameters);
            ffmpeg.avcodec_open2(_pAudioContext, audioCodec, null).ThrowExceptionIfError();

            //ѹ�����ݰ�
            _pPacket = ffmpeg.av_packet_alloc();
            //��ѹ�����ŵ�����֡�Ķ���
            _pFrame = ffmpeg.av_frame_alloc();
            //frame->16bit 44100 PCM ͳһ��Ƶ������ʽ�������
            //����swrcontext�����ļ�
            swrContext = ffmpeg.swr_alloc();
            //��Ƶ��ʽ  ����Ĳ������ò���
            inFormat = _pAudioContext->sample_fmt;
            // ����Ĳ�����ʽ
            outFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;
            // ���������
            inSampleRate = _pAudioContext->sample_rate;
            // ���������
            outSampleRate = 44100;
            // ������������
            in_ch_layout = _pAudioContext->channel_layout;
            //�����������
            out_ch_layout = ffmpeg.AV_CH_LAYOUT_STEREO;
            //��Swrcontext ����ռ䣬���ù�������
            ffmpeg.swr_alloc_set_opts(swrContext, out_ch_layout, outFormat, outSampleRate,
                    (long)in_ch_layout, inFormat, inSampleRate, 0, null
                    );
            // ��ʼ��
            ffmpeg.swr_init(swrContext);
            // ��ȡ��������
            outChannelCount = ffmpeg.av_get_channel_layout_nb_channels((ulong)out_ch_layout);
            Debug.LogWarning("��������%d " + outChannelCount);
        }

        int currentIndex = 0;

        public bool TryDecodeNextFrame(out byte[] frame)
        {
            // ������Ƶ�������� 16bit   44100  PCM����, ˫����
            byte* out_buffer = (byte*)Marshal.AllocHGlobal(2 * 44100);
            //��ʼ��ȡԴ�ļ������н���
            if (ffmpeg.av_read_frame(_pFormatContext, _pPacket) >= 0)
            {
                if (_pPacket->stream_index == _audioStreamIndex)
                {
                    ffmpeg.avcodec_send_packet(_pAudioContext, _pPacket);
                    //����
                    int ret = ffmpeg.avcodec_receive_frame(_pAudioContext, _pFrame);
                    if (ret == 0)
                    {
                        //��ÿһ֡����ת����pcm
                        ret = ffmpeg.swr_convert(swrContext, &out_buffer, 2 * 44100,
                                    (byte**)&_pFrame->data, _pFrame->nb_samples);
                        //��ȡʵ�ʵĻ����С
                        int out_buffer_size = ffmpeg.av_samples_get_buffer_size(null, outChannelCount, _pFrame->nb_samples, outFormat, 1);
                        byte[] write = new byte[out_buffer_size];
                        Marshal.Copy((IntPtr)out_buffer, write, 0, write.Length);
                        bufferedWaveProvider.AddSamples(write, 0, out_buffer_size);
                        frame = write;
                        Debug.LogWarning("���ڽ���%d" + currentIndex++);
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