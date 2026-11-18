using System;
using PrinterHelper.Documents;
using SkiaSharp;

namespace PrinterHelper.Tests
{
    /// <summary>Pictures made on the fly with Skia.</summary>
    static class TestImages
    {
        /// <summary>A gradient with stripes; with alpha, the left half is fully transparent.</summary>
        public static unsafe SKBitmap Bitmap(int width, int height, bool color, bool alpha = false)
        {
            var info = color || alpha
                ? new SKImageInfo(width, height, SKColorType.Bgra8888, alpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque)
                : new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
            var bitmap = new SKBitmap(info);
            var p = (byte*)bitmap.GetPixels();
            var bpp = info.BytesPerPixel;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var v = (byte)((x * 255 / Math.Max(1, width - 1)) ^ (y % 32 < 16 ? 0 : 0x40));
                    var px = p + y * bitmap.RowBytes + x * bpp;
                    for (var c = 0; c < bpp; c++) px[c] = v;
                    if (bpp == 4) px[3] = alpha ? (byte)(x < width / 2 ? 0 : 255) : (byte)255;
                }
            return bitmap;
        }

        public static byte[] Jpeg(int width, int height, bool color)
        {
            using (var bitmap = Bitmap(width, height, color))
                return Imaging.EncodeJpeg(bitmap);
        }

        public static byte[] Encode(SKBitmap bitmap, SKEncodedImageFormat format)
        {
            using (var image = SKImage.FromBitmap(bitmap))
            using (var data = image.Encode(format, 100))
                return data.ToArray();
        }

        public static PageImage Page(int width, int height, bool color, double dpi, int rotation = 0) =>
            PageImage.FromJpeg(Jpeg(width, height, color), dpi, "test", rotation);

        /// <summary>Decodes a JPEG and reads one pixel as (B, G, R).</summary>
        public static (byte B, byte G, byte R) Pixel(byte[] jpeg, int x, int y)
        {
            using (var bitmap = SKBitmap.Decode(jpeg))
            {
                var c = bitmap.GetPixel(x, y);
                return (c.Blue, c.Green, c.Red);
            }
        }
    }
}
