using System;

public interface IMedia
{
    public delegate void MediaHandler(TimeSpan duration);
    public event MediaHandler MediaCompleted;
}