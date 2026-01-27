
public struct Size
{
    private int width;

    private int height;

    public int Width
    {
        get
        {
            return width;
        }
        set
        {
            width = value;
        }
    }

    public int Height
    {
        get
        {
            return height;
        }
        set
        {
            height = value;
        }
    }

    public Size(int width, int height)
    {
        this.width = width;
        this.height = height;
    }
}