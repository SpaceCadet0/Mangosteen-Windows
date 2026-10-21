namespace Mangosteen.Core.Imaging;

/// <summary>Rotation by quarter turns and mirroring, as shown on screen: the image is turned first, then mirrored.</summary>
public readonly record struct ImageTransform(int QuarterTurns = 0, bool FlipHorizontal = false, bool FlipVertical = false)
{
    public bool IsIdentity => QuarterTurns == 0 && !FlipHorizontal && !FlipVertical;

    /// <summary>The size after the turn: a quarter turn swaps width and height.</summary>
    public (int Width, int Height) Size(int width, int height) => QuarterTurns % 2 == 1 ? (height, width) : (width, height);

    /// <summary>Turns what is on screen by 90°. A mirror made before the turn now runs along the other axis.</summary>
    public ImageTransform Rotate(bool clockwise) =>
        new((QuarterTurns + (clockwise ? 1 : 3)) % 4, FlipVertical, FlipHorizontal);

    public ImageTransform Flip(bool horizontal) =>
        horizontal ? this with { FlipHorizontal = !FlipHorizontal } : this with { FlipVertical = !FlipVertical };
}
