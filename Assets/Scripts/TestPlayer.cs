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

    float frameRate = 0.033f;
    float nextFrame = 0.0f;

    // Start is called before the first frame update
    void Start()
    {
        //var url = "rtmp://58.200.131.2:1935/livetv/hunantv";
        var url = Application.streamingAssetsPath + "/test.mp4";
        ffPlayer = new FFPlayer(url, OnVideoSize, OnVideoData, OAudioData);
    }

    byte[] data;
    // Update is called once per frame
    void Update()
    {
        if(Time.time > nextFrame)
        {
            nextFrame = Time.time + frameRate;
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
    }

    private void OnVideoSize(int width, int height)
    {
        texture2D = new Texture2D(width, height, TextureFormat.RGB24, false);
        texture2D.Apply();
        rawImage.texture = texture2D;
    }

    private void OnVideoData(byte[] data)
    {
        imgQueue.Enqueue(data);
    }

    private void OAudioData(byte[] data)
    {
        audioQueue.Enqueue(data);
    }

    byte[] bsBuffer;

    private void OnAudioFilterRead(float[] data, int channels)
    {
        if (audioQueue.Count > 0)
        {
            bsBuffer = audioQueue.Dequeue();
            float[] _buffer = ByteArrayToFloatArray(bsBuffer, bsBuffer.Length);
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = _buffer[i];
            }
        }
    }

    public static float[] ByteArrayToFloatArray(byte[] byteArray, int length)
    {
        float[] resultFloatArray = new float[length / 2];
        if (resultFloatArray == null || resultFloatArray.Length != (length / 2))
        {
            resultFloatArray = new float[length / 2];
        }
        int arrIdx = 0;
        for (int i = 0; i < length; i += 2)
        {
            resultFloatArray[arrIdx++] = BytesToFloat(byteArray[i], byteArray[i + 1]);
        }
        return resultFloatArray;
    }

    static float BytesToFloat(byte firstByte, byte secondByte)
    {
        return (float)((short)((int)secondByte << 8 | (int)firstByte)) / 32768f;
    }

    private void OnApplicationQuit()
    {
        ffPlayer.Dispose();
    }
}