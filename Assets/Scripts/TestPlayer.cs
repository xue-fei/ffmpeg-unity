using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityFFmpeg;

public class TestPlayer : MonoBehaviour
{
    Texture2D texture2D;
    Queue<byte[]> imgQueue = new Queue<byte[]>();
    Queue<byte[]> audioQueue = new Queue<byte[]>();
    public RawImage rawImage;
    public AspectRatioFitter aspectRatioFitter;
    FFPlayer ffPlayer;

    float frameDely = 0.04f; // 25fps
    float nextFrame = 0.0f;
    bool textureReady = false;

    // 音频相关
    public AudioSource audioSource;
    private CircularAudioBuffer audioBuffer;
    private AudioClip audioClip;
    private int audioSampleRate = 44100;
    private int audioChannels = 2;  

    // Start is called before the first frame update
    void Start()
    {
        Loom.Initialize();
        // 初始化音频系统 
        audioSource.spatialBlend = 0; // 2D音效 
        audioBuffer = new CircularAudioBuffer(audioSampleRate * audioChannels * 5);

        // 测试URL
        //var url = "http://devimages.apple.com.edgekey.net/streaming/examples/bipbop_4x3/gear2/prog_index.m3u8";
        var url = "http://demo-videos.qnsdk.com/bbk-H265-50fps.mp4";
        //var url = "http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/BigBuckBunny.mp4";
        //var url = Application.streamingAssetsPath + "/08.mp4";

        Debug.Log($"开始播放: {url}");
        Loom.RunAsync(() =>
        {
            ffPlayer = new FFPlayer(url, OnVideoSize, OnVideoData, OnAudioData);
        });
    }

    byte[] videoData;
    // Update is called once per frame
    void Update()
    {
        if (Time.time >= nextFrame)
        {
            nextFrame = Time.time + frameDely;

            if (imgQueue.Count > 0 && textureReady)
            {
                videoData = imgQueue.Dequeue();
                if (texture2D != null && videoData != null)
                {
                    texture2D.LoadRawTextureData(videoData);
                    texture2D.Apply();
                }
            }

            // 限制队列长度，防止内存占用过高
            if (imgQueue.Count > 10)
            {
                while (imgQueue.Count > 5)
                {
                    imgQueue.Dequeue();
                }
            }
        }
    }

    private void OnVideoSize(int width, int height, float frameRate)
    {
        Loom.QueueOnMainThread(() =>
        {
            Debug.Log($"视频尺寸: {width}x{height}");
            frameDely = 1f / frameRate;
            if (width > 0 && height > 0)
            {
                texture2D = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture2D.Apply();
                rawImage.texture = texture2D;

                if (aspectRatioFitter != null)
                {
                    aspectRatioFitter.aspectRatio = (float)width / height;
                }
                textureReady = true;
            }
        });
    }

    private void OnVideoData(byte[] data)
    {
        Loom.QueueOnMainThread(() =>
        {
            if (textureReady && data != null)
            {
                if (imgQueue.Count < 10) // 限制队列大小
                {
                    imgQueue.Enqueue(data);
                }
            }
        });
    }

    private void OnAudioData(byte[] data)
    {
        if (data == null || data.Length == 0)
        {
            return;
        }

        // ✅ 转换字节数组为浮点数组
        int floatCount = data.Length / 2;
        float[] floatArray = new float[floatCount];
        ByteArrayToFloatArray(data, floatArray, floatCount);

        // ✅ 写入循环缓冲区
        audioBuffer.Write(floatArray);
        // ✅ 确保AudioClip已创建并播放
        Loom.QueueOnMainThread(() =>
        {
            if (audioSource.clip == null && audioBuffer.Count > audioSampleRate)
            {
                // 创建流式AudioClip（使用OnAudioFilterRead回调）
                audioSource.clip = AudioClip.Create(
                    "StreamingAudio",
                    audioSampleRate * 5 * 2,  // 10秒缓冲
                    audioChannels,
                    audioSampleRate,
                    true,  // ✅ 启用流式播放
                    OnAudioFilterRead  // ✅ 使用回调读取
                );

                audioSource.loop = true;
                audioSource.Play();
                Debug.Log("✅ 流式AudioClip已创建并开始播放");
            }
        }); 
    }

    /// <summary>
    /// 音频过滤读取回调 - Unity会自动调用此方法
    /// 直接从循环缓冲区读取音频数据
    /// </summary>
    private void OnAudioFilterRead(float[] data)
    {
        audioBuffer.Read(data, data.Length);
    }

    private void ByteArrayToFloatArray(byte[] byteArray, float[] floatArray, int length)
    {
        int byteIndex = 0;
        int floatIndex = 0;

        while (byteIndex < byteArray.Length && floatIndex < length)
        {
            short sample = (short)((byteArray[byteIndex] & 0xFF) | (byteArray[byteIndex + 1] << 8));
            floatArray[floatIndex] = sample / 32768f;

            byteIndex += 2;
            floatIndex++;
        }
    }

    private void OnApplicationQuit()
    {
        if (ffPlayer != null)
        {
            ffPlayer.Dispose();
        }

        if (audioClip != null)
        {
            Destroy(audioClip);
        }

        if (texture2D != null)
        {
            Destroy(texture2D);
        }
    }
}