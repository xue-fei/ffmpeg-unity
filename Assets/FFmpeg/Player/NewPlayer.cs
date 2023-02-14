using FFmpeg.AutoGen;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class NewPlayer : MonoBehaviour
{
    VideoDecoder video = new VideoDecoder();
    Task PlayTask;
    CancellationTokenSource cts = new CancellationTokenSource();
    CancellationToken ct;
    public RawImage image;
    Texture2D texture2D;
    public Slider slider;
    public Text text;

    // Start is called before the first frame update
    void Start()
    {
        Application.targetFrameRate = 30;
        ct = cts.Token;
        Loom.Initialize();
        FFmpegHelper.Init();
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
        var url = "http://39.134.115.163:8080/PLTV/88888910/224/3221225632/index.m3u8";
        //var url = Application.streamingAssetsPath + "/test.mp4";
        //初始化解码视频
        video.InitDecodecVideo(url);
        video.Play();
        PlayTask = new Task(() =>
        {
            while (true)
            {
                Thread.Sleep(1);
                if (ct.IsCancellationRequested)
                {
                    break;
                }
                //播放中
                if (video.IsPlaying)
                {
                    //获取下一帧视频
                    if (video.TryReadNextFrame(out var frame))
                    {
                        var bytes = video.FrameConvertBytes(&frame);
                        Loom.QueueOnMainThread(() =>
                        {
                            if (texture2D == null)
                            {
                                texture2D = new Texture2D(video.FrameWidth, video.FrameHeight, TextureFormat.BGRA32, false);
                                texture2D.Apply();
                                image.texture = texture2D;
                                image.GetComponent<AspectRatioFitter>().aspectRatio = (float)video.FrameWidth / (float)video.FrameHeight;
                            }
                            texture2D.LoadRawTextureData(bytes);
                            texture2D.Apply(); 
                            //image.sprite = Sprite.Create(texture2D, new Rect(0, 0, texture2D.width, texture2D.height), new Vector2(0.5f, 0.5f));
                            text.text = video.Position.ToString();
                            slider.value = (float)(video.Position.TotalSeconds / video.Duration.TotalSeconds);
                        });
                    }
                }
            }
        });
        PlayTask.Start();
        video.MediaCompleted += (s) =>
        {
            video.Stop();
        };
    }

    private void OnApplicationQuit()
    {
        if (video.IsPlaying)
        {
            video.Stop();
        }
        cts.Cancel();
    }

    void OnDrag(BaseEventData data)
    {
        video.SeekProgress((int)(slider.value * video.Duration.TotalSeconds));
    }
}