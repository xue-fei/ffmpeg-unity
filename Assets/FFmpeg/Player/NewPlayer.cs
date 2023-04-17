using FFmpeg.AutoGen;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using NAudio.Wave;
using System.IO;
using UnityEditor;

public class NewPlayer : MonoBehaviour
{
    VideoDecoder videoDecoder = new VideoDecoder();
    AudioDecoder audioDecoder = new AudioDecoder();
    //播放器
    WaveOut waveOut;
    //缓存区
    BufferedWaveProvider bufferedWaveProvider;
    Task PlayTask;
    CancellationTokenSource cts = new CancellationTokenSource();
    CancellationToken ct;
    public RawImage image;
    Texture2D texture2D;
    public Slider slider;
    public Text text;

    //string url = "http://39.134.115.163:8080/PLTV/88888910/224/3221225632/index.m3u8";
    string url = Application.streamingAssetsPath + "/test.mp4";

    // Start is called before the first frame update
    void Start()
    {
        if (!File.Exists(url))
        {
            Debug.LogError(url + " 文件不存在");
#if UNITY_EDITOR
            EditorApplication.isPlaying = false;
#endif
            return;
        }
        Application.targetFrameRate = 30;
        ct = cts.Token;
        Loom.Initialize();
        FFmpegHelper.Init();
        waveOut = new WaveOut();
        WaveFormat wf = new WaveFormat(44100, 2);
        bufferedWaveProvider = new BufferedWaveProvider(wf);
        waveOut.Init(bufferedWaveProvider);
        waveOut.Play();
        Loom.RunAsync(() =>
        {
            Play();
        });

        UnityAction<BaseEventData> drag = new UnityAction<BaseEventData>(OnDrag);
        EventTrigger.Entry myDrag = new EventTrigger.Entry();
        myDrag.eventID = EventTriggerType.Drag;
        myDrag.callback.AddListener(drag);
        EventTrigger eventTrigger = slider.gameObject.AddComponent<EventTrigger>();
        eventTrigger.triggers.Add(myDrag);
    }

    unsafe void Play()
    {  
        audioDecoder.InitDecodecAudio(url);
        audioDecoder.Play();

        //初始化解码视频
        videoDecoder.InitDecodecVideo(url);
        videoDecoder.Play();

        PlayTask = new Task(() =>
        {
            while (true)
            {
                //Thread.Sleep(1);
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (audioDecoder.IsPlaying)
                {
                    //获取下一帧音频
                    if (audioDecoder.TryReadNextFrame(out var frame))
                    {
                        var bytes = audioDecoder.FrameConvertBytes(&frame);
                        if (bytes != null)
                        {
                            //if (bufferedWaveProvider.BufferLength <= bufferedWaveProvider.BufferedBytes + bytes.Length)
                            //{
                            //    bufferedWaveProvider.ClearBuffer();
                            //}
                            bufferedWaveProvider.AddSamples(bytes, 0, bytes.Length);//向缓存中添加音频样本 
                        }
                    }
                }
                //播放中
                if (videoDecoder.IsPlaying)
                {
                    //获取下一帧视频
                    if (videoDecoder.TryReadNextFrame(out var frame))
                    {
                        var vdata = videoDecoder.FrameConvertBytes(&frame);
                        Loom.QueueOnMainThread(() =>
                        {
                            if (texture2D == null)
                            {
                                texture2D = new Texture2D(videoDecoder.FrameWidth, videoDecoder.FrameHeight, TextureFormat.BGRA32, false);
                                texture2D.Apply();
                                image.texture = texture2D;
                                image.GetComponent<AspectRatioFitter>().aspectRatio = (float)videoDecoder.FrameWidth / (float)videoDecoder.FrameHeight;
                            }
                            texture2D.LoadRawTextureData(vdata);
                            texture2D.Apply();
                            //image.sprite = Sprite.Create(texture2D, new Rect(0, 0, texture2D.width, texture2D.height), new Vector2(0.5f, 0.5f));
                            text.text = videoDecoder.Position.ToString();
                            slider.value = (float)(videoDecoder.Position.TotalSeconds / videoDecoder.Duration.TotalSeconds);
                        });
                    }
                }
            }
        });
        PlayTask.Start();
        videoDecoder.MediaCompleted += (s) =>
        {
            videoDecoder.Stop();
            audioDecoder.Stop();
        };
    }

    private void OnApplicationQuit()
    {
        if (videoDecoder.IsPlaying)
        {
            videoDecoder.Stop();
        }
        if (audioDecoder.IsPlaying)
        {
            audioDecoder.Stop();
        }
        cts.Cancel();
        if (waveOut != null)
        {
            waveOut.Stop();
            waveOut.Dispose();
        }
    }

    void OnDrag(BaseEventData data)
    {
        videoDecoder.SeekProgress((int)(slider.value * videoDecoder.Duration.TotalSeconds));
        audioDecoder.SeekProgress((int)(slider.value * audioDecoder.Duration.TotalSeconds));
    }
}