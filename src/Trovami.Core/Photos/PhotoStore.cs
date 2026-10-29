using System.Security.Cryptography;
using SkiaSharp;
using Trovami.Core.Backup;

namespace Trovami.Core.Photos;

public enum PhotoSize { Full, Thumb }

// Photos of items and locations, as files: photos/<household>/<id>.jpg plus a small <id>.thumb.jpg
// for grids. Every upload is turned upright (EXIF orientation), shrunk to MaxSide and saved as JPEG,
// which keeps backups manageable and strips the original's metadata (such as GPS position).
public sealed class PhotoStore(StoragePaths paths)
{
    public const int MaxSide = 1600;
    public const int ThumbSide = 400;
    public const int MaxUploadBytes = 20 * 1024 * 1024;
    private const int Quality = 82;

    // Saves a photo for the item or location `id`, replacing an earlier one. Returns the new
    // version (see Item.PhotoVersion). Throws InputException when it isn't an image.
    public async Task<string> SaveAsync(Guid householdId, Guid id, Stream upload, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await upload.CopyToAsync(buffer, ct);
        if (buffer.Length > MaxUploadBytes)
            throw new InputException($"The photo is too large; keep it under {MaxUploadBytes / 1024 / 1024} MB.");
        buffer.Position = 0;

        var (full, thumb) = Process(buffer);
        var dir = Path.Combine(paths.PhotosPath, householdId.ToString("N"));
        Directory.CreateDirectory(dir);
        await WriteAtomicAsync(Path.Combine(dir, FileName(id, PhotoSize.Full)), full, ct);
        await WriteAtomicAsync(Path.Combine(dir, FileName(id, PhotoSize.Thumb)), thumb, ct);
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
    }

    // The file of a photo, or null when there's none.
    public string? PathOf(Guid householdId, Guid id, PhotoSize size)
    {
        var path = Path.Combine(paths.PhotosPath, householdId.ToString("N"), FileName(id, size));
        return File.Exists(path) ? path : null;
    }

    public void Delete(Guid householdId, Guid id)
    {
        foreach (var size in new[] { PhotoSize.Full, PhotoSize.Thumb })
        {
            var path = Path.Combine(paths.PhotosPath, householdId.ToString("N"), FileName(id, size));
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    public void DeleteHousehold(Guid householdId)
    {
        var dir = Path.Combine(paths.PhotosPath, householdId.ToString("N"));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }

    // The JPEG bytes, for the AI.
    public async Task<byte[]?> ReadAsync(Guid householdId, Guid id, CancellationToken ct = default) =>
        PathOf(householdId, id, PhotoSize.Full) is { } path ? await File.ReadAllBytesAsync(path, ct) : null;

    private static string FileName(Guid id, PhotoSize size) =>
        id.ToString("N") + (size == PhotoSize.Thumb ? ".thumb.jpg" : ".jpg");

    internal static (byte[] Full, byte[] Thumb) Process(Stream input)
    {
        using var codec = SKCodec.Create(input) ?? throw new InputException("That file isn't a photo Trovami can read. Use a JPEG, PNG or WebP image.");
        using var decoded = SKBitmap.Decode(codec) ?? throw new InputException("That photo couldn't be read.");
        using var upright = Orient(decoded, codec.EncodedOrigin);
        return (Encode(upright, MaxSide), Encode(upright, ThumbSide));
    }

    private static byte[] Encode(SKBitmap source, int maxSide)
    {
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        // A transparent PNG gets a white background rather than JPEG's black.
        canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, Quality);
        return data.ToArray();
    }

    // Phones store most photos sideways with an EXIF note on how to turn them.
    private static SKBitmap Orient(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
            return source.Copy();

        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var result = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        // Each call applies before the ones above it: a source point is rotated first, then moved.
        float w = source.Width, h = source.Height;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: canvas.Translate(w, 0); canvas.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: canvas.Translate(w, h); canvas.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: canvas.Translate(0, h); canvas.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: canvas.Scale(-1, 1); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightTop: canvas.Translate(h, 0); canvas.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: canvas.Translate(h, w); canvas.Scale(-1, 1); canvas.RotateDegrees(-90); break;
            case SKEncodedOrigin.LeftBottom: canvas.Translate(0, w); canvas.RotateDegrees(-90); break;
        }
        using var image = SKImage.FromBitmap(source);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return result;
    }

    private static async Task WriteAtomicAsync(string path, byte[] content, CancellationToken ct)
    {
        var temp = path + ".tmp";
        await File.WriteAllBytesAsync(temp, content, ct);
        File.Move(temp, path, overwrite: true);
    }
}
