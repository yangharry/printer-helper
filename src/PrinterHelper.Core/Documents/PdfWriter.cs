using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PrinterHelper.Documents
{
    /// <summary>
    /// Writes JPEG pages into a PDF without re-compressing them. One image per page,
    /// page size = image size at its scan resolution, rotation stored as /Rotate.
    /// </summary>
    public static class PdfWriter
    {
        /// <summary>Writes to a temporary file first so a failed save never leaves a broken PDF behind.</summary>
        public static void Save(string path, IReadOnlyList<PageImage> pages)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, "~" + Path.GetFileNameWithoutExtension(fullPath) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                    Write(stream, pages, DateTime.Now);
                if (File.Exists(fullPath))
                    File.Replace(temp, fullPath, null);
                else
                    File.Move(temp, fullPath);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }

        public static void Write(Stream stream, IReadOnlyList<PageImage> pages, DateTime created)
        {
            if (pages.Count == 0) throw new ArgumentException("저장할 페이지가 없습니다.", nameof(pages));

            var output = new Output(stream);
            var objectCount = 3 + 3 * pages.Count;     // catalog, pages, info, then page/content/image per page
            var offsets = new long[objectCount + 1];

            output.Text("%PDF-1.4\n");
            output.Bytes(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });

            var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(p => $"{4 + 3 * p} 0 R"));
            offsets[1] = output.Position;
            output.Text("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            offsets[2] = output.Position;
            output.Text($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages.Count} >>\nendobj\n");
            offsets[3] = output.Position;
            output.Text($"3 0 obj\n<< /Producer (PrinterHelper) /CreationDate (D:{created.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}) >>\nendobj\n");

            for (var p = 0; p < pages.Count; p++)
            {
                var page = pages[p];
                var pageObj = 4 + 3 * p;
                var contentObj = pageObj + 1;
                var imageObj = pageObj + 2;
                var w = Number(page.PixelWidth * 72.0 / page.Dpi);
                var h = Number(page.PixelHeight * 72.0 / page.Dpi);
                var colorSpace = page.Components == 1 ? "/DeviceGray" : "/DeviceRGB";
                var rotate = page.Rotation != 0 ? $" /Rotate {page.Rotation}" : "";

                offsets[pageObj] = output.Position;
                output.Text($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}]{rotate} /Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

                var content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q";
                offsets[contentObj] = output.Position;
                output.Text($"{contentObj} 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

                offsets[imageObj] = output.Position;
                output.Text($"{imageObj} 0 obj\n<< /Type /XObject /Subtype /Image /Width {page.PixelWidth} /Height {page.PixelHeight} /ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /DCTDecode /Length {page.Jpeg.Length} >>\nstream\n");
                output.Bytes(page.Jpeg);
                output.Text("\nendstream\nendobj\n");
            }

            var xref = output.Position;
            var sb = new StringBuilder();
            sb.Append("xref\n0 ").Append(objectCount + 1).Append('\n');
            sb.Append("0000000000 65535 f \n");
            for (var n = 1; n <= objectCount; n++)
                sb.Append(offsets[n].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R /Info 3 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            output.Text(sb.ToString());
            stream.Flush();
        }

        static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        sealed class Output
        {
            readonly Stream stream;

            public Output(Stream stream) => this.stream = stream;

            public long Position { get; private set; }

            public void Text(string text) => Bytes(Encoding.ASCII.GetBytes(text));

            public void Bytes(byte[] bytes)
            {
                stream.Write(bytes, 0, bytes.Length);
                Position += bytes.Length;
            }
        }
    }
}
