using SkiaSharp;

namespace VbForum.Data;

/// <summary>Bildverarbeitung (Thumbnails, Avatare, Logo) mit SkiaSharp (MIT-Lizenz).</summary>
public static class ImageService
{
    public sealed record ImageInfo(int Width, int Height, string Extension);

    public static ImageInfo? Inspect(byte[] data)
    {
        using var codec = SKCodec.Create(new MemoryStream(data));
        if (codec == null) return null;
        var ext = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "jpg",
            SKEncodedImageFormat.Png => "png",
            SKEncodedImageFormat.Gif => "gif",
            SKEncodedImageFormat.Webp => "webp",
            SKEncodedImageFormat.Bmp => "bmp",
            _ => null
        };
        return ext == null ? null : new ImageInfo(codec.Info.Width, codec.Info.Height, ext);
    }

    /// <summary>Verkleinert auf maximal <paramref name="max"/> px (längste Seite). Ergebnis JPEG bzw. PNG bei Transparenz.</summary>
    public static (byte[] Data, int Width, int Height, string Extension)? Resize(byte[] data, int max, bool square = false, int quality = 85)
    {
        using var bmp = SKBitmap.Decode(data);
        if (bmp == null) return null;
        int w = bmp.Width, h = bmp.Height;
        SKRectI src = new(0, 0, w, h);
        if (square)
        {
            var side = Math.Min(w, h);
            src = new SKRectI((w - side) / 2, (h - side) / 2, (w - side) / 2 + side, (h - side) / 2 + side);
            w = h = side;
        }
        double scale = Math.Min(1.0, (double)max / Math.Max(w, h));
        int nw = Math.Max(1, (int)Math.Round(w * scale)), nh = Math.Max(1, (int)Math.Round(h * scale));
        bool alpha = bmp.AlphaType != SKAlphaType.Opaque;
        using var surface = SKSurface.Create(new SKImageInfo(nw, nh, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(alpha ? SKColors.Transparent : SKColors.White);
        using var img = SKImage.FromBitmap(bmp);
        canvas.DrawImage(img, new SKRect(src.Left, src.Top, src.Right, src.Bottom), new SKRect(0, 0, nw, nh),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        using var snap = surface.Snapshot();
        using var enc = snap.Encode(alpha ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, quality);
        return (enc.ToArray(), nw, nh, alpha ? "png" : "jpg");
    }

    public static string ContentType(string ext) => ext.ToLowerInvariant() switch
    {
        "jpg" or "jpeg" or "jpe" => "image/jpeg",
        "png" => "image/png",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "bmp" => "image/bmp",
        "svg" => "image/svg+xml",
        "pdf" => "application/pdf",
        "txt" => "text/plain; charset=utf-8",
        "zip" => "application/zip",
        _ => "application/octet-stream"
    };
}
