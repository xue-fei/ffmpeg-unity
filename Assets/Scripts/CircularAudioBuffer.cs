using System;


/// <summary>
/// 循环音频缓冲区 - 用于高效的流式音频播放
/// </summary>
public class CircularAudioBuffer : IDisposable
{
    private float[] buffer;
    private int writePos = 0;
    private int readPos = 0;
    private int count = 0;
    private readonly int capacity;
    private readonly object lockObj = new object();

    public int Count
    {
        get
        {
            lock (lockObj)
            {
                return count;
            }
        }
    }

    public CircularAudioBuffer(int capacity)
    {
        this.capacity = capacity;
        this.buffer = new float[capacity];
    }

    /// <summary>
    /// 写入音频数据
    /// </summary>
    public void Write(float[] data)
    {
        if (data == null || data.Length == 0)
            return;

        lock (lockObj)
        {
            for (int i = 0; i < data.Length; i++)
            {
                buffer[writePos] = data[i];
                writePos = (writePos + 1) % capacity;

                if (count < capacity)
                {
                    count++;
                }
                else
                {
                    // 缓冲满，覆盖最旧的数据（丢弃最旧的音频）
                    readPos = (readPos + 1) % capacity;
                }
            }
        }
    }

    /// <summary>
    /// 读取音频数据 - 供OnAudioFilterRead调用
    /// </summary>
    public void Read(float[] data, int length)
    {
        if (data == null || length == 0)
            return;

        lock (lockObj)
        {
            for (int i = 0; i < length; i++)
            {
                if (count > 0)
                {
                    data[i] = buffer[readPos];
                    readPos = (readPos + 1) % capacity;
                    count--;
                }
                else
                {
                    // 缓冲空了，填充静音
                    data[i] = 0f;
                }
            }
        }
    }

    public void Dispose()
    {
        buffer = null;
    }
}