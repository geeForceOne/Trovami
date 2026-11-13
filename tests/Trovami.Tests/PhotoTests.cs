using SkiaSharp;
using Trovami.Core;
using Trovami.Core.Ai;
using Trovami.Core.Photos;

namespace Trovami.Tests;

public class PhotoTests
{
    public static byte[] Jpeg(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.SteelBlue);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray();
    }

    [Fact]
    public void RotatingTurnsThePhotoAQuarterClockwise()
    {
        // Red in the top left corner ends up in the top right.
        using var bitmap = new SKBitmap(800, 600);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.SteelBlue);
            canvas.DrawRect(0, 0, 100, 100, new SKPaint { Color = SKColors.Red });
        }
        using var image = SKImage.FromBitmap(bitmap);

        var (full, thumb) = PhotoStore.Rotate(image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray());

        using var turned = SKBitmap.Decode(full);
        Assert.Equal((600, 800), (turned.Width, turned.Height));
        Assert.True(turned.GetPixel(550, 50).Red > 200);
        Assert.True(turned.GetPixel(50, 50).Red < 100);
        using var small = SKBitmap.Decode(thumb);
        Assert.Equal((300, 400), (small.Width, small.Height));
    }

    [Fact]
    public void ShrinksLargePhotosAndMakesAThumbnail()
    {
        var (full, thumb) = PhotoStore.Process(new MemoryStream(Jpeg(3200, 2400)));
        using var f = SKBitmap.Decode(full);
        using var t = SKBitmap.Decode(thumb);
        Assert.Equal((PhotoStore.MaxSide, 1200), (f.Width, f.Height));
        Assert.Equal((PhotoStore.ThumbSide, 300), (t.Width, t.Height));
    }

    [Fact]
    public void KeepsSmallPhotosAtTheirSize()
    {
        var (full, _) = PhotoStore.Process(new MemoryStream(Jpeg(800, 600)));
        using var f = SKBitmap.Decode(full);
        Assert.Equal((800, 600), (f.Width, f.Height));
    }

    [Fact]
    public void RefusesWhatIsntAPhoto() =>
        Assert.Throws<InputException>(() => PhotoStore.Process(new MemoryStream("not a photo"u8.ToArray())));

    [Theory]
    [InlineData("Cordless drill", "Cordless drill")]
    [InlineData("\"winter boots.\"\n", "Winter boots")]
    [InlineData("UNKNOWN", null)]
    [InlineData("  ", null)]
    public void CleansTheAiReply(string reply, string? name) => Assert.Equal(name, AiNamer.CleanReply(reply));
}
