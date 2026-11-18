using System.IO;

namespace PrinterHelper.Documents
{
    /// <summary>Pixel size and channel count read from a JPEG's frame header.</summary>
    public sealed class JpegInfo
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Components { get; private set; }

        /// <summary>
        /// Reads the SOF header. Returns null when the data is not a JPEG we can embed in a PDF as-is
        /// (unknown height, or a color layout other than gray/RGB).
        /// </summary>
        public static JpegInfo TryRead(byte[] b)
        {
            if (b == null || b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                int marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD8)) { i += 2; continue; }
                if (marker == 0xD9 || marker == 0xDA) return null; // reached image data without a frame header
                var length = (b[i + 2] << 8) | b[i + 3];
                if (length < 2) return null;
                if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    var info = new JpegInfo
                    {
                        Height = (b[i + 5] << 8) | b[i + 6],
                        Width = (b[i + 7] << 8) | b[i + 8],
                        Components = b[i + 9],
                    };
                    // Height 0 means it is given later in a DNL marker, which PDF viewers handle badly.
                    if (info.Width == 0 || info.Height == 0) return null;
                    if (info.Components != 1 && info.Components != 3) return null;
                    return info;
                }
                i += 2 + length;
            }
            return null;
        }

        /// <summary>
        /// Copies a JPEG without its metadata, keeping the image data byte for byte. Drops EXIF/XMP
        /// (GPS position, phone model, ...), the other APPn blocks except JFIF (APP0) and Adobe
        /// (APP14, needed to decode colors), comments, and everything after the end marker, where
        /// phones append motion-photo video or HDR gain maps. Returns null if the file is not laid
        /// out as expected.
        /// </summary>
        public static byte[] StripMetadata(byte[] b)
        {
            if (b == null || b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return null;
            var output = new MemoryStream(b.Length);
            output.WriteByte(0xFF);
            output.WriteByte(0xD8);
            var i = 2;
            while (i + 1 < b.Length)
            {
                if (b[i] != 0xFF) return null;
                int marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD9)
                {
                    output.WriteByte(0xFF);
                    output.WriteByte(0xD9);
                    return output.ToArray();
                }
                if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                {
                    output.Write(b, i, 2);
                    i += 2;
                    continue;
                }
                if (i + 3 >= b.Length) return null;
                var length = (b[i + 2] << 8) | b[i + 3];
                if (length < 2 || i + 2 + length > b.Length) return null;
                var isMetadata = (marker >= 0xE1 && marker <= 0xEF && marker != 0xEE) || marker == 0xFE;
                if (!isMetadata) output.Write(b, i, 2 + length);
                i += 2 + length;
                if (marker == 0xDA)
                {
                    // Image data runs until the next marker that is neither a stuffed 0xFF00 nor a restart marker.
                    var start = i;
                    while (i + 1 < b.Length && !(b[i] == 0xFF && b[i + 1] != 0x00 && !(b[i + 1] >= 0xD0 && b[i + 1] <= 0xD7))) i++;
                    output.Write(b, start, i - start);
                }
            }
            return null;
        }
    }
}
