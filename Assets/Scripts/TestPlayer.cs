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
    FFPlayer ffPlayer;

    float frameRate = 0.04f; // 25fps
    float nextFrame = 0.0f;
    bool textureReady = false;

    // 音频相关
    public AudioSource audioSource;
    private AudioClip audioClip;
    private int audioSampleRate = 44100;
    private int audioChannels = 2;
    private float[] audioBuffer;
    private int audioBufferSize = 4096;

    // Start is called before the first frame update
    void Start()
    {
        Loom.Initialize();
        // 初始化音频系统 
        audioSource.spatialBlend = 0; // 2D音效
        audioBuffer = new float[audioBufferSize];

        // 测试URL
        //var url = "http://devimages.apple.com.edgekey.net/streaming/examples/bipbop_4x3/gear2/prog_index.m3u8";
        var url = "http://demo-videos.qnsdk.com/bbk-H265-50fps.mp4";
        //var url = "http://commondatastorage.googleapis.com/gtv-videos-bucket/sample/BigBuckBunny.mp4";
        //var url = Application.streamingAssetsPath + "/test.mp4";

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
            nextFrame = Time.time + frameRate;

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

    private void OnVideoSize(int width, int height)
    {
        Loom.QueueOnMainThread(() =>
        {
            Debug.Log($"视频尺寸: {width}x{height}");
            if (width > 0 && height > 0)
            {
                texture2D = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture2D.Apply();
                rawImage.texture = texture2D;

                // 调整RawImage的尺寸
                RectTransform rt = rawImage.GetComponent<RectTransform>();
                float aspectRatio = (float)width / height;
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, rt.sizeDelta.x / aspectRatio);

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
        if (data != null)
        {
            if (audioQueue.Count < 20) // 限制音频队列大小
            {
                audioQueue.Enqueue(data);
            }

            Loom.QueueOnMainThread(() =>
            {
                // 如果还没有创建AudioClip，创建它
                if (audioClip == null)
                {
                    // 创建一个足够大的AudioClip来存储音频数据
                    audioClip = AudioClip.Create("StreamingAudio", audioSampleRate * 10, audioChannels, audioSampleRate, false);
                    audioSource.clip = audioClip;
                    audioSource.loop = false;
                    audioSource.Play();
                }
            });
        }
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (audioQueue.Count > 0 && channels == audioChannels)
        {
            byte[] audioBytes = audioQueue.Dequeue();
            if (audioBytes != null && audioBytes.Length > 0)
            {
                // 将字节数据转换为浮点数
                int floatCount = audioBytes.Length / 2;
                if (audioBuffer == null || audioBuffer.Length < floatCount)
                {
                    audioBuffer = new float[floatCount];
                }

                ByteArrayToFloatArray(audioBytes, audioBuffer, floatCount);

                // 填充音频数据
                int copyLength = Mathf.Min(floatCount, data.Length);
                for (int i = 0; i < copyLength; i++)
                {
                    data[i] = audioBuffer[i];
                }

                // 如果还有剩余空间，填充0
                for (int i = copyLength; i < data.Length; i++)
                {
                    data[i] = 0;
                }
            }
        }
        else
        {
            // 没有音频数据，填充0
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = 0;
            }
        }
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