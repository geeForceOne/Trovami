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
