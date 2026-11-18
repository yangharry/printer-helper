using System;
using System.Text;

namespace PrinterHelper.Documents
{
    /// <summary>
    /// Reads the resolution a picture file states about itself. Skia decodes the pixels but does not
    /// report resolution, and without it a 300 dpi scan or a small picture would change its printed size.
    /// </summary>
    public static class ImageDensity
    {
        /// <summary>Horizontal and vertical dots per inch, or null when the file does not say.</summary>
        public static (double X, double Y)? Read(byte[] b)
        {
            if (b == null || b.Length < 16) return null;
            if (b[0] == 0xFF && b[1] == 0xD8) return Jpeg(b);
            if (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G') return Png(b);
            if (b[0] == 'B' && b[1] == 'M') return Bmp(b);
            return null;
        }

        /// <summary>The EXIF resolution tags when present (1.0.1 read them first too), otherwise the JFIF density.</summary>
        static (double, double)? Jpeg(byte[] b)
        {
            (double, double)? exif = null, jfif = null;
            var i = 2;
            while (i + 4 <= b.Length)
            {
                if (b[i] != 0xFF) break;
                int marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD9 || marker == 0xDA) break;
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                var length = (b[i + 2] << 8) | b[i + 3];
                if (length < 2 || i + 2 + length > b.Length) break;
                if (marker == 0xE0 && length >= 16 && Ascii(b, i + 4, 5) == "JFIF\0")
                {
                    // Units: 1 = dots per inch, 2 = dots per cm, 0 = an aspect ratio only (no resolution).
                    int units = b[i + 11];
                    double x = (b[i + 12] << 8) | b[i + 13], y = (b[i + 14] << 8) | b[i + 15];
                    if (x > 0 && y > 0 && units == 1) jfif = (x, y);
                    else if (x > 0 && y > 0 && units == 2) jfif = (x * 2.54, y * 2.54);
                }
                else if (marker == 0xE1 && exif == null && length >= 16 && Ascii(b, i + 4, 6) == "Exif\0\0")
                {
                    exif = Exif(b, i + 10, i + 2 + length);
                }
                i += 2 + length;
            }
            return exif ?? jfif;
        }

        /// <summary>IFD0 XResolution (0x011A), YResolution (0x011B) and ResolutionUnit (0x0128: 2 = inch, 3 = cm).</summary>
        static (double, double)? Exif(byte[] b, int tiff, int end)
        {
            try
            {
                if (tiff + 8 > end) return null;
                bool little = b[tiff] == 'I' && b[tiff + 1] == 'I';
                if (!little && !(b[tiff] == 'M' && b[tiff + 1] == 'M')) return null;
                int U16(int at) => little ? b[at] | (b[at + 1] << 8) : (b[at] << 8) | b[at + 1];
                long U32(int at) => little
                    ? (uint)(b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24))
                    : (uint)((b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]);
                var ifd = tiff + (int)U32(tiff + 4);
                if (ifd + 2 > end) return null;
                int count = U16(ifd);
                double? x = null, y = null;
                var unit = 2;
                for (var n = 0; n < count; n++)
                {
                    var entry = ifd + 2 + 12 * n;
                    if (entry + 12 > end) break;
                    int tag = U16(entry), type = U16(entry + 2);
                    if ((tag == 0x011A || tag == 0x011B) && type == 5)
                    {
                        var at = tiff + (int)U32(entry + 8);
                        if (at + 8 > end) continue;
                        double num = U32(at), den = U32(at + 4);
                        if (den == 0) continue;
                        if (tag == 0x011A) x = num / den; else y = num / den;
                    }
                    else if (tag == 0x0128 && type == 3)
                    {
                        unit = U16(entry + 8);
                    }
                }
                if (x == null || y == null || x <= 0 || y <= 0 || unit == 1) return null;
                return unit == 3 ? (x.Value * 2.54, y.Value * 2.54) : (x.Value, y.Value);
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>The pHYs chunk, when its unit is the meter.</summary>
        static (double, double)? Png(byte[] b)
        {
            var i = 8;
            while (i + 12 <= b.Length)
            {
                var length = (int)(((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3]);
                if (length < 0 || i + 12 + length > b.Length) return null;
                var type = Ascii(b, i + 4, 4);
                if (type == "pHYs" && length >= 9)
                {
                    var d = i + 8;
                    double x = ((uint)b[d] << 24) | ((uint)b[d + 1] << 16) | ((uint)b[d + 2] << 8) | b[d + 3];
                    double y = ((uint)b[d + 4] << 24) | ((uint)b[d + 5] << 16) | ((uint)b[d + 6] << 8) | b[d + 7];
                    return b[d + 8] == 1 && x > 0 && y > 0 ? (x * 0.0254, y * 0.0254) : null;
                }
                if (type == "IDAT" || type == "IEND") return null;
                i += 12 + length;
            }
            return null;
        }

        /// <summary>biXPelsPerMeter / biYPelsPerMeter of the BITMAPINFOHEADER (or a later version of it).</summary>
        static (double, double)? Bmp(byte[] b)
        {
            if (b.Length < 46) return null;
            var headerSize = BitConverter.ToInt32(b, 14);
            if (headerSize < 40) return null; // the old OS/2 header has no resolution
            double x = BitConverter.ToInt32(b, 38), y = BitConverter.ToInt32(b, 42);
            return x > 0 && y > 0 ? (x * 0.0254, y * 0.0254) : null;
        }

        static string Ascii(byte[] b, int at, int count) =>
            at + count <= b.Length ? Encoding.ASCII.GetString(b, at, count) : "";
    }
}
