using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using PrinterHelper.Documents;

namespace PrinterHelper.Printing
{
    /// <summary>
    /// Writes the PDF that CUPS prints: every PDF page is exactly the chosen sheet, portrait, with the
    /// scan placed by <see cref="PrintLayout"/>. The JPEGs are embedded unchanged and turned with the
    /// image matrix, so nothing is re-compressed and CUPS has nothing left to scale or rotate.
    /// </summary>
    internal static class LayoutPdfWriter
    {
        /// <summary>0.17 inch in points: what most printers cannot reach anyway.</summary>
        public const double MarginPoints = PrintLayout.DefaultMargin * 72;

        /// <summary>Sheet size in PDF points (1/72 inch), portrait.</summary>
        public static (double Width, double Height) SheetPoints(SheetSize sheet) => sheet switch
        {
            SheetSize.Letter => (612, 792),
            SheetSize.Legal => (612, 1008),
            _ => (595.276, 841.89),
        };

        /// <summary>The media name lp understands.</summary>
        public static string MediaName(SheetSize sheet) => sheet switch
        {
            SheetSize.Letter => "Letter",
            SheetSize.Legal => "Legal",
            _ => "A4",
        };

        /// <summary>Where each page goes, in PDF points (y up from the bottom of the sheet), and its final rotation.</summary>
        public static (int Rotation, double X, double Y, double Width, double Height) PlaceOnSheet(PageImage page, SheetSize sheet)
        {
            var (sheetWidth, sheetHeight) = SheetPoints(sheet);
            var printable = PrintLayout.PrintableArea(sheetWidth / 72, sheetHeight / 72, PrintLayout.DefaultMargin);
            var placement = PrintLayout.Place(page, printable);
            var box = placement.Destination;
            return (placement.Rotation, box.X * 72, sheetHeight - box.Bottom * 72, box.Width * 72, box.Height * 72);
        }

        /// <summary>
        /// The cm operands that draw the image (the unit square, its top row at y = 1) into the box
        /// (x, y, w, h), turned clockwise by <paramref name="rotation"/>. The box is the one after rotation.
        /// </summary>
        public static double[] ImageMatrix(int rotation, double x, double y, double w, double h) => rotation switch
        {
            // The image's top edge becomes the right edge.
            90 => new[] { 0, -h, w, 0, x, y + h },
            180 => new[] { -w, 0, 0, -h, x + w, y + h },
            // The image's top edge becomes the left edge.
            270 => new[] { 0, h, -w, 0, x + w, y },
            _ => new[] { w, 0, 0, h, x, y },
        };

        public static void Write(Stream stream, IReadOnlyList<PageImage> pages, SheetSize sheet, DateTime created)
        {
            if (pages == null || pages.Count == 0) throw new ArgumentException("인쇄할 페이지가 없습니다.", nameof(pages));

            var (sheetWidth, sheetHeight) = SheetPoints(sheet);
            var mediaBox = $"[0 0 {Number(sheetWidth)} {Number(sheetHeight)}]";
            var output = new Output(stream);
            var objectCount = 3 + 3 * pages.Count;     // catalog, pages, info, then page/content/image per page
            var offsets = new long[objectCount + 1];

            output.Text("%PDF-1.4\n");
            output.Bytes(new byte[] { 0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A });

            var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(p => $"{4 + 3 * p} 0 R"));
            offsets[1] = output.Position;
            output.Text("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
            offsets[2] = output.Position;
            output.Text($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pages.Count} /MediaBox {mediaBox} >>\nendobj\n");
            offsets[3] = output.Position;
            output.Text($"3 0 obj\n<< /Producer (PrinterHelper) /CreationDate (D:{created.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}) >>\nendobj\n");

            for (var p = 0; p < pages.Count; p++)
            {
                var page = pages[p];
                var pageObj = 4 + 3 * p;
                var contentObj = pageObj + 1;
                var imageObj = pageObj + 2;
                var colorSpace = page.Components == 1 ? "/DeviceGray" : "/DeviceRGB";
                var (rotation, x, y, w, h) = PlaceOnSheet(page, sheet);

                offsets[pageObj] = output.Position;
                output.Text($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} /Resources << /XObject << /Im0 {imageObj} 0 R >> >> /Contents {contentObj} 0 R >>\nendobj\n");

                var content = $"q {string.Join(" ", ImageMatrix(rotation, x, y, w, h).Select(Number))} cm /Im0 Do Q";
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

        static string Number(double value)
        {
            var text = value.ToString("0.###", CultureInfo.InvariantCulture);
            return text == "-0" ? "0" : text;
        }

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
