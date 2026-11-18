using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using PrinterHelper.Documents;
using PrinterHelper.Printing;
using SkiaSharp;
using static PrinterHelper.Tests.Assert;

namespace PrinterHelper.Tests
{
    static class PrintingTests
    {
        const double Tolerance = 1e-9;

        static bool Near(double a, double b, double tolerance = 0.01) => Math.Abs(a - b) <= tolerance;

        static void CheckCentred(PrintRect box, PrintRect area, string what)
        {
            Check(Near(box.X - area.X, area.Right - box.Right, Tolerance), what + ": centred horizontally");
            Check(Near(box.Y - area.Y, area.Bottom - box.Bottom, Tolerance), what + ": centred vertically");
            Check(box.X >= area.X - Tolerance && box.Right <= area.Right + Tolerance, what + ": inside horizontally");
            Check(box.Y >= area.Y - Tolerance && box.Bottom <= area.Bottom + Tolerance, what + ": inside vertically");
        }

        /// <summary>Where a point of the stored image (fractions, y down) ends up in the box after turning clockwise.</summary>
        static (double X, double Y) TurnClockwise(double fx, double fy, int rotation)
        {
            for (var r = 0; r < rotation; r += 90) (fx, fy) = (1 - fy, fx);
            return (fx, fy);
        }

        // ───────────────────────────── layout ─────────────────────────────

        static readonly (double W, double H) A4Inches = (210 / 25.4, 297 / 25.4);

        [Test("Print layout: an A4 scan at 200 dpi shrinks slightly to fit A4 inside 0.17 in margins")]
        static void LayoutA4Scan()
        {
            var printable = PrintLayout.PrintableArea(A4Inches.W, A4Inches.H, PrintLayout.DefaultMargin);
            Check(Near(printable.X, 0.17, Tolerance) && Near(printable.Width, A4Inches.W - 0.34, Tolerance), "printable area");
            var placement = PrintLayout.Place(1654 / 200.0, 2339 / 200.0, 0, printable);
            Check(placement.Rotation == 0, "not turned");
            Check(placement.Scale > 0.95 && placement.Scale < 0.97, $"shrunk a little (scale {placement.Scale:0.0000})");
            Check(Near(placement.Destination.Width, printable.Width, Tolerance), "fills the width");
            Check(Near(placement.Destination.Height / placement.Destination.Width, 2339.0 / 1654, 1e-6), "aspect ratio kept");
            CheckCentred(placement.Destination, printable, "A4");
        }

        [Test("Print layout: landscape pages are turned sideways, matching ones are not")]
        static void LayoutTurnsLandscape()
        {
            var letter = PrintLayout.PrintableArea(8.5, 11, 0.17);
            var turned = PrintLayout.Place(11, 8.5, 0, letter);
            Check(turned.Rotation == 90, "landscape on portrait paper: a quarter turn clockwise");
            Check(turned.Destination.Height > turned.Destination.Width, "the turned page is portrait");
            Check(Near(turned.Destination.Height / turned.Destination.Width, 11 / 8.5, 1e-6), "aspect ratio kept");
            CheckCentred(turned.Destination, letter, "turned");

            var rotatedByUser = PrintLayout.Place(8.5, 11, 90, letter);
            Check(rotatedByUser.Rotation == 180, "a portrait scan the user turned to landscape is turned once more");

            var portraitAgain = PrintLayout.Place(11, 8.5, 270, letter);
            Check(portraitAgain.Rotation == 270, "a landscape scan the user turned to portrait stays as the user turned it");

            var landscapeSheet = PrintLayout.PrintableArea(11, 8.5, 0.17);
            Check(PrintLayout.Place(10, 7, 0, landscapeSheet).Rotation == 0, "landscape on landscape paper");
            Check(PrintLayout.Place(5, 7, 0, landscapeSheet).Rotation == 90, "portrait on landscape paper");

            Check(PrintLayout.Place(4 + 0.5 / 96, 4, 0, letter).Rotation == 0, "nearly square: left alone");
            Check(PrintLayout.Place(4 + 2.0 / 96, 4, 0, letter).Rotation == 90, "a little wider than tall: turned");
            Check(PrintLayout.Place(4, 4, 450, letter).Rotation == 90, "rotation normalised");
        }

        [Test("Print layout: a 2x2 inch page prints at real size in the middle of the sheet")]
        static void LayoutRealSize()
        {
            var letter = PrintLayout.PrintableArea(8.5, 11, 0.17);
            var placement = PrintLayout.Place(TestImages.Page(200, 200, false, 100), letter);
            var box = placement.Destination;
            Check(placement.Scale == 1 && placement.Rotation == 0, "never enlarged, not turned");
            Check(Near(box.Width, 2, Tolerance) && Near(box.Height, 2, Tolerance), $"2 x 2 in (got {box})");
            Check(Near(box.X, 3.25, Tolerance) && Near(box.Y, 4.5, Tolerance), $"centred on the sheet (got {box})");

            var page = TestImages.Page(300, 600, false, 150, rotation: 90);
            var size = PrintLayout.PageSize(page);
            Check(Near(size.Width, 4, Tolerance) && Near(size.Height, 2, Tolerance), "page size follows the page's rotation");
        }

        [Test("Print layout: one margin from the printer's four")]
        static void LayoutUniformMargin()
        {
            Check(PrintLayout.UniformMargin(0.17, 0.25, 0.17, 0.5) == 0.5, "largest margin");
            Check(PrintLayout.UniformMargin(0, 0, 0, 0) == 0.17, "borderless printer: default margin");
            Check(PrintLayout.UniformMargin(1.5, 0.2, -1, 0) == 0.2, "values outside 0..1 inch are ignored");
            Check(PrintLayout.UniformMargin(1, 1, 1, 1) == 0.17, "1 inch is not a hard margin");
            Check(PrintLayout.UniformMargin(double.NaN, 0.1, 0, 0) == 0.1, "NaN ignored");
            var tiny = PrintLayout.PrintableArea(1, 1, 0.6);
            Check(tiny.Width > 0 && tiny.Height > 0, "printable area never collapses");
        }

        // ───────────────────────────── layout PDF (CUPS) ─────────────────────────────

        [Test("Layout PDF: sheet-sized pages, image matrices and unchanged JPEGs")]
        static void LayoutPdfStructure()
        {
            var pages = new List<PageImage>
            {
                TestImages.Page(1654, 2339, false, 200),               // A4 portrait, gray: shrinks, rotation 0
                TestImages.Page(1100, 850, true, 100),                 // landscape letter, color: turned to 90
                TestImages.Page(800, 1000, false, 150, rotation: 90),  // turned by the user to landscape: 180
                TestImages.Page(1000, 800, true, 150, rotation: 270),  // turned by the user to portrait: 270
            };
            var expectedRotations = new[] { 0, 90, 180, 270 };
            byte[] bytes;
            using (var stream = new MemoryStream())
            {
                LayoutPdfWriter.Write(stream, pages, SheetSize.A4, new DateTime(2026, 11, 18, 12, 0, 0));
                bytes = stream.ToArray();
            }
            File.WriteAllBytes(Path.Combine(Program.OutDir, "print-layout-a4.pdf"), bytes);
            var text = Encoding.Latin1.GetString(bytes);

            Check(text.StartsWith("%PDF-1.4\n"), "header");
            Check(text.TrimEnd().EndsWith("%%EOF"), "trailer");
            var startxref = int.Parse(Regex.Match(text, @"startxref\n(\d+)\n%%EOF\s*$").Groups[1].Value);
            Check(text.Substring(startxref, 4) == "xref", "startxref points at xref");
            var xref = Regex.Match(text.Substring(startxref), @"xref\n0 (\d+)\n((?:\d{10} \d{5} [nf] \n)+)");
            var count = int.Parse(xref.Groups[1].Value);
            Check(count == 3 + 3 * pages.Count + 1, "object count");
            var entries = xref.Groups[2].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Check(entries.Length == count, "xref entry count");
            for (var n = 1; n < count; n++)
            {
                var offset = int.Parse(entries[n].Substring(0, 10));
                Check(text.Substring(offset).StartsWith($"{n} 0 obj\n"), $"xref offset of object {n}");
            }

            var (sheetW, sheetH) = (595.276, 841.89);
            var margin = 0.17 * 72;
            for (var p = 0; p < pages.Count; p++)
            {
                var page = pages[p];
                var pageObj = 4 + 3 * p;
                var pageText = Regex.Match(text, $@"\n{pageObj} 0 obj\n(<<.*?>>)\nendobj").Groups[1].Value;
                Check(pageText.Contains("/Type /Page ") && pageText.Contains("/MediaBox [0 0 595.276 841.89]"), $"page {p}: A4 MediaBox");
                Check(!pageText.Contains("/Rotate"), $"page {p}: portrait, no /Rotate");

                var content = Regex.Match(text, $@"\n{pageObj + 1} 0 obj\n<< /Length (\d+) >>\nstream\n(.*?)\nendstream").Groups;
                Check(int.Parse(content[1].Value) == content[2].Value.Length, $"page {p}: content length");
                var cm = Regex.Match(content[2].Value, @"^q (\S+) (\S+) (\S+) (\S+) (\S+) (\S+) cm /Im0 Do Q$");
                Check(cm.Success, $"page {p}: one image drawn with a cm matrix");
                var m = Enumerable.Range(1, 6).Select(i => double.Parse(cm.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
                (double X, double Y) Map(double u, double v) => (m[0] * u + m[2] * v + m[4], m[1] * u + m[3] * v + m[5]);

                // The unit square lands on an axis-aligned box inside the margins, centred on the sheet.
                var corners = new[] { Map(0, 0), Map(1, 0), Map(0, 1), Map(1, 1) };
                double x0 = corners.Min(c => c.X), x1 = corners.Max(c => c.X), y0 = corners.Min(c => c.Y), y1 = corners.Max(c => c.Y);
                Check(x0 >= margin - 0.01 && x1 <= sheetW - margin + 0.01 && y0 >= margin - 0.01 && y1 <= sheetH - margin + 0.01, $"page {p}: inside the margins");
                Check(Near(x0, sheetW - x1, 0.01) && Near(y0, sheetH - y1, 0.01), $"page {p}: centred");
                var rotation = expectedRotations[p];
                var turned = rotation == 90 || rotation == 270;
                Check(Near((x1 - x0) / (y1 - y0), turned ? (double)page.PixelHeight / page.PixelWidth : (double)page.PixelWidth / page.PixelHeight, 0.001), $"page {p}: aspect ratio after rotation {rotation}");
                if (p == 0) Check(Near(x1 - x0, sheetW - 2 * margin, 0.01), "A4 scan fills the width");

                // Orientation: a point of the image (x right, y down) must be where a clockwise turn puts it.
                foreach (var (fx, fy) in new[] { (0.0, 0.0), (0.1, 0.2), (0.8, 0.3), (1.0, 1.0) })
                {
                    var (px, py) = Map(fx, 1 - fy); // PDF image space: v = 1 is the top row
                    var actual = ((px - x0) / (x1 - x0), (y1 - py) / (y1 - y0));
                    var expected = TurnClockwise(fx, fy, rotation);
                    Check(Near(actual.Item1, expected.X, 0.001) && Near(actual.Item2, expected.Y, 0.001),
                        $"page {p}: image point ({fx}, {fy}) turned {rotation} lands at ({expected.X:0.###}, {expected.Y:0.###}), got ({actual.Item1:0.###}, {actual.Item2:0.###})");
                }

                // The JPEG goes in byte for byte.
                var imageObj = pageObj + 2;
                var header = Regex.Match(text, $@"\n{imageObj} 0 obj\n<< /Type /XObject /Subtype /Image /Width (\d+) /Height (\d+) /ColorSpace /(\w+) /BitsPerComponent 8 /Filter /DCTDecode /Length (\d+) >>\nstream\n");
                Check(header.Success, $"page {p}: image header");
                Check(int.Parse(header.Groups[1].Value) == page.PixelWidth && int.Parse(header.Groups[2].Value) == page.PixelHeight, $"page {p}: image size");
                Check(header.Groups[3].Value == (page.Components == 1 ? "DeviceGray" : "DeviceRGB"), $"page {p}: color space");
                var length = int.Parse(header.Groups[4].Value);
                var start = header.Index + header.Length;
                Check(length == page.Jpeg.Length && bytes.AsSpan(start, length).SequenceEqual(page.Jpeg), $"page {p}: JPEG unchanged");
                Check(text.Substring(start + length, 10) == "\nendstream", $"page {p}: stream ends after the JPEG");
            }
        }

        [Test("Layout PDF: Letter and Legal sheets")]
        static void LayoutPdfSheets()
        {
            foreach (var (sheet, box, media) in new[] { (SheetSize.Letter, "[0 0 612 792]", "Letter"), (SheetSize.Legal, "[0 0 612 1008]", "Legal"), (SheetSize.A4, "[0 0 595.276 841.89]", "A4") })
            {
                using (var stream = new MemoryStream())
                {
                    LayoutPdfWriter.Write(stream, new[] { TestImages.Page(200, 200, false, 100) }, sheet, DateTime.Now);
                    var text = Encoding.Latin1.GetString(stream.ToArray());
                    Check(text.Contains("/MediaBox " + box), sheet + " MediaBox");
                }
                Check(LayoutPdfWriter.MediaName(sheet) == media, sheet + " media name");
            }

            // A 2 inch square on Letter: 144 pt, centred, real size.
            var (rotation, x, y, w, h) = LayoutPdfWriter.PlaceOnSheet(TestImages.Page(200, 200, false, 100), SheetSize.Letter);
            Check(rotation == 0 && Near(w, 144) && Near(h, 144) && Near(x, (612 - 144) / 2.0) && Near(y, (792 - 144) / 2.0), "2 in square at real size");
            Check(Near(LayoutPdfWriter.MarginPoints, 12.24, Tolerance), "0.17 in margin");
            Assert.Throws<ArgumentException>(() => LayoutPdfWriter.Write(new MemoryStream(), new PageImage[0], SheetSize.A4, DateTime.Now), "no pages");
        }

        [Test("Layout PDF: image matrices for each rotation")]
        static void LayoutPdfMatrices()
        {
            // (x, y, w, h) is the box after rotation, PDF y up.
            Check(LayoutPdfWriter.ImageMatrix(0, 10, 20, 30, 40).SequenceEqual(new double[] { 30, 0, 0, 40, 10, 20 }), "0");
            Check(LayoutPdfWriter.ImageMatrix(90, 10, 20, 30, 40).SequenceEqual(new double[] { 0, -40, 30, 0, 10, 60 }), "90");
            Check(LayoutPdfWriter.ImageMatrix(180, 10, 20, 30, 40).SequenceEqual(new double[] { -30, 0, 0, -40, 40, 60 }), "180");
            Check(LayoutPdfWriter.ImageMatrix(270, 10, 20, 30, 40).SequenceEqual(new double[] { 0, 40, -30, 0, 40, 20 }), "270");
        }

        // ───────────────────────────── CUPS ─────────────────────────────

        [Test("CUPS: queue names, default queue and lpoptions values")]
        static void CupsParsing()
        {
            var names = CupsPrinting.ParseQueueNames("Samsung_M2080\nOffice_Laser\n\n  Brother_HL/duplex \n");
            Check(names.SequenceEqual(new[] { "Samsung_M2080", "Office_Laser", "Brother_HL/duplex" }), "lpstat -e names");
            Check(CupsPrinting.ParseQueueNames("").Count == 0 && CupsPrinting.ParseQueueNames(null).Count == 0, "no queues");

            Check(CupsPrinting.ParseDefault("system default destination: Office_Laser\n", names) == "Office_Laser", "English lpstat -d");
            Check(CupsPrinting.ParseDefault("시스템 기본 대상: Samsung_M2080\n", names) == "Samsung_M2080", "Korean lpstat -d");
            Check(CupsPrinting.ParseDefault("no system default destination\n", names) == null, "no default");
            Check(CupsPrinting.ParseDefault("", names) == null, "empty output");

            var output = "copies=1 device-uri=ipp://192.168.0.48/ipp/print finishings=3 job-sheets=none,none " +
                         "printer-info='삼성 M2080 프린터 (사무실)' printer-is-accepting-jobs=true printer-location " +
                         "printer-make-and-model='Samsung M2080 Series' marker-names='Black\\'s \\\\toner' " +
                         "note=\"double quoted\" empty='' printer-state=3\n";
            var options = CupsPrinting.ParseOptions(output);
            Check(options["device-uri"] == "ipp://192.168.0.48/ipp/print", "plain value");
            Check(options["printer-info"] == "삼성 M2080 프린터 (사무실)", "quoted Korean value with spaces");
            Check(options["printer-make-and-model"] == "Samsung M2080 Series", "quoted value");
            Check(options["marker-names"] == "Black's \\toner", "escaped quote and backslash");
            Check(options["note"] == "double quoted", "double quotes");
            Check(options["printer-location"] == "" && options["empty"] == "", "options without a value");
            Check(options["job-sheets"] == "none,none" && options["printer-state"] == "3", "other values");

            var queue = CupsPrinting.MakeQueue("Samsung_M2080", options, "Samsung_M2080");
            Check(queue.Name == "Samsung_M2080" && queue.IsDefault, "name and default");
            Check(queue.Info == "삼성 M2080 프린터 (사무실)" && queue.ToString() == queue.Info, "shown by printer-info");
            Check(queue.MakeAndModel == "Samsung M2080 Series" && queue.Host == "192.168.0.48", "model and host");
            var bare = CupsPrinting.MakeQueue("Office_Laser", new Dictionary<string, string>(), "Samsung_M2080");
            Check(!bare.IsDefault && bare.Info == null && bare.Host == null && bare.ToString() == "Office_Laser", "queue without details");
        }

        [Test("CUPS: printer host from the device URI")]
        static void CupsDeviceUriHost()
        {
            var cases = new (string Uri, string Host)[]
            {
                ("ipp://192.168.0.48/ipp/print", "192.168.0.48"),
                ("ipp://192.168.0.48:631/ipp/print", "192.168.0.48"),
                ("ipps://printer.local:443/ipp/print", "printer.local"),
                ("socket://192.168.0.48:9100", "192.168.0.48"),
                ("socket://192.168.0.48", "192.168.0.48"),
                ("lpd://192.168.0.48/queue", "192.168.0.48"),
                ("http://10.0.0.5:631/ipp", "10.0.0.5"),
                ("https://user:secret@10.0.0.6/ipp", "10.0.0.6"),
                ("IPP://192.168.0.7/", "192.168.0.7"),
                ("ipp://[fe80::1%25en0]:631/ipp/print", "fe80::1%en0"),
                ("socket://[2001:db8::5]", "2001:db8::5"),
                ("dnssd://Samsung%20M2080%20Series._ipp._tcp.local./?uuid=1234", null),
                ("usb://Samsung/M2080%20Series?serial=ZDB1", null),
                ("smb://server/printer", null),
                ("ipp:///nohost", null),
                ("not a uri", null),
                ("", null),
                (null, null),
            };
            foreach (var (uri, host) in cases)
                Check(CupsPrinting.HostFromDeviceUri(uri) == host, $"{uri ?? "null"} → {host ?? "null"} (got {CupsPrinting.HostFromDeviceUri(uri) ?? "null"})");

            Check(CupsPrinting.StripPort("192.168.0.48:8080") == "192.168.0.48", "scanner host with port");
            Check(CupsPrinting.StripPort("[fe80::2]:80") == "fe80::2", "bracketed IPv6 with port");
            Check(CupsPrinting.StripPort("fe80::2") == "fe80::2", "bare IPv6");
        }

        [Test("CUPS: the queue for the scanner, by address then by model")]
        static void CupsFindQueue()
        {
            var bonjour = new CupsQueue { Name = "Samsung_M2080_Series", Info = "Samsung M2080 Series (SEC30CD)", MakeAndModel = "Samsung M2080 Series", DeviceUri = "dnssd://x" };
            var byAddress = new CupsQueue { Name = "Office", Info = "사무실 프린터", MakeAndModel = "Generic PCL Laser", Host = "192.168.0.48" };
            var similar = new CupsQueue { Name = "Samsung_M2080W", Info = "Samsung M2080W Series", MakeAndModel = "Samsung M2080W Series", Host = "192.168.0.60" };
            var queues = new[] { similar, bonjour, byAddress };

            Check(CupsPrinting.FindQueueForScanner(queues, "192.168.0.48", "Samsung M2080 Series") == byAddress, "address wins");
            Check(CupsPrinting.FindQueueForScanner(queues, "192.168.0.48:8080", null) == byAddress, "scanner port ignored");
            Check(CupsPrinting.FindQueueForScanner(queues, "192.168.0.99", "Samsung M2080 Series") == bonjour, "model match, not the M2080W");
            Check(CupsPrinting.FindQueueForScanner(queues, null, "SAMSUNG M2080") == bonjour, "case and the word Series ignored");
            Check(CupsPrinting.FindQueueForScanner(new[] { byAddress }, "192.168.0.99", "사무실 프린터") == byAddress, "matched by printer-info");
            Check(CupsPrinting.FindQueueForScanner(queues, "192.168.0.99", "HP LaserJet") == null, "no match");
            Check(CupsPrinting.FindQueueForScanner(queues, "192.168.0.99", "Series") == null, "a model of only 'Series' matches nothing");
            Check(CupsPrinting.FindQueueForScanner(new CupsQueue[0], "192.168.0.48", "Samsung") == null, "no queues");
            Check(CupsPrinting.FindQueueForScanner(null, "192.168.0.48", "Samsung") == null, "null list");

            var twoMatches = new[] { new CupsQueue { Name = "A", MakeAndModel = "Samsung M2080 Series" }, new CupsQueue { Name = "B", MakeAndModel = "Samsung M2080 Series", IsDefault = true } };
            Check(CupsPrinting.FindQueueForScanner(twoMatches, null, "Samsung M2080 Series").Name == "B", "the default queue among equal matches");
        }

        [Test("CUPS: lp arguments")]
        static void CupsLpArguments()
        {
            var args = CupsPrinting.LpArguments("Samsung_M2080", 2, SheetSize.A4, "프린터 도우미 (3쪽)", "/tmp/PrinterHelper-print-1.pdf");
            Check(args.SequenceEqual(new[]
            {
                "-d", "Samsung_M2080", "-n", "2", "-t", "프린터 도우미 (3쪽)",
                "-o", "media=A4", "-o", "print-scaling=none", "-o", "collate=true", "/tmp/PrinterHelper-print-1.pdf",
            }), "A4, two copies: " + string.Join(" ", args));

            args = CupsPrinting.LpArguments("Office", 1, SheetSize.Letter, "Scan", "/tmp/a b.pdf");
            Check(args.SequenceEqual(new[] { "-d", "Office", "-n", "1", "-t", "Scan", "-o", "media=Letter", "-o", "print-scaling=none", "/tmp/a b.pdf" }), "Letter, one copy");

            args = CupsPrinting.LpArguments(null, 0, SheetSize.Legal, null, "/tmp/x.pdf");
            Check(args.SequenceEqual(new[] { "-n", "1", "-o", "media=Legal", "-o", "print-scaling=none", "/tmp/x.pdf" }), "default queue, no title, copies at least 1");
        }

        [Test("CUPS: listing queues never throws")]
        static void CupsListQueues()
        {
            var queues = CupsPrinting.ListQueues();
            Check(queues != null, "a list");
            if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) Check(queues.Count == 0, "no CUPS here: empty");
            foreach (var q in queues) Check(!string.IsNullOrEmpty(q.Name), "every queue has a name");
            Console.WriteLine($"      {queues.Count} CUPS queue(s)" + string.Concat(queues.Select(q => $"; {q.Name} host={q.Host ?? "-"}{(q.IsDefault ? " (default)" : "")}")));
        }

        // ───────────────────────────── Windows (GDI) ─────────────────────────────

        [Test("Windows: PRINTDLGEX has the native layout", Only = "windows")]
        [SupportedOSPlatform("windows")]
        static void WindowsPrintDlgExLayout()
        {
            var type = typeof(WindowsPrintDialog.PRINTDLGEX);
            int Offset(string field) => (int)Marshal.OffsetOf(type, field);
            if (IntPtr.Size == 8)
            {
                Check(Marshal.SizeOf(type) == 136, $"136 bytes on 64-bit (got {Marshal.SizeOf(type)})");
                var expected = new (string Field, int Offset)[]
                {
                    ("hwndOwner", 8), ("hDevMode", 16), ("hDevNames", 24), ("hDC", 32), ("Flags", 40), ("nMaxPageRanges", 56),
                    ("lpPageRanges", 64), ("nCopies", 80), ("hInstance", 88), ("lpCallback", 104), ("nPropertyPages", 112),
                    ("lphPropertyPages", 120), ("nStartPage", 128), ("dwResultAction", 132),
                };
                foreach (var (field, offset) in expected) Check(Offset(field) == offset, $"{field} at {offset} (got {Offset(field)})");
            }
            else
                Check(Marshal.SizeOf(type) == 84, "84 bytes on 32-bit");
        }

        [Test("Windows: dialog handles turn into printer settings (Microsoft Print to PDF)", Only = "windows")]
        [SupportedOSPlatform("windows")]
        static void WindowsDialogHandles()
        {
            // Only the local PDF queue: building DEVMODEs for a network printer's driver may talk to the printer.
            const string pdfQueue = "Microsoft Print to PDF";
            if (!System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>().Contains(pdfQueue))
            {
                Console.WriteLine("      (skipped: " + pdfQueue + " is not installed)");
                return;
            }
            var (devNames, devMode) = WindowsPrintDialog.InitialHandles(pdfQueue.ToUpperInvariant());
            Check(devNames != IntPtr.Zero && devMode != IntPtr.Zero, "handles for the preferred queue");
            try
            {
                var settings = WindowsPrintDialog.SettingsFromHandles(devNames, devMode, 1);
                Check(settings.PrinterName == pdfQueue, $"queue chosen with its installed spelling (got {settings.PrinterName})");
                Check(settings.IsValid && settings.Copies >= 1, "valid settings");
                var paper = settings.DefaultPageSettings.PaperSize;
                Check(paper.Width > 0 && paper.Height > 0, "page settings read from the DEVMODE");
                Check(WindowsPrintDialog.SettingsFromHandles(devNames, devMode, 3).Copies == 3, "copies the dialog hands back are kept");
            }
            finally
            {
                WindowsPrintDialog.FreeHandles(devNames, devMode);
            }
        }

        [Test("Windows: printable area is turned to match landscape paper", Only = "windows")]
        [SupportedOSPlatform("windows")]
        static void WindowsPrintableAreaOrientation()
        {
            var portrait = new RectangleF(0, 0, 827, 1169);
            var landscape = new RectangleF(0, 0, 1169, 827);
            var area = new RectangleF(17, 25, 793, 1119); // as PageSettings reports it: portrait
            Check(WindowsPrinting.OrientLike(area, portrait) == area, "portrait unchanged");
            Check(WindowsPrinting.OrientLike(area, landscape) == new RectangleF(25, 17, 1119, 793), "swapped for landscape");
            Check(WindowsPrinting.OrientLike(RectangleF.Empty, landscape) == landscape, "unknown area: the whole sheet");
            Check(WindowsPrinting.RotateFlipFor(90) == RotateFlipType.Rotate90FlipNone && WindowsPrinting.RotateFlipFor(270) == RotateFlipType.Rotate270FlipNone, "clockwise turns");
        }

        /// <summary>A mid-gray page with a black square in its top-left corner (15% of each side).</summary>
        static PageImage MarkedPage(int width, int height, double dpi, int rotation)
        {
            using (var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Gray8, SKAlphaType.Opaque)))
            using (var canvas = new SKCanvas(bitmap))
            using (var paint = new SKPaint { Color = SKColors.Black })
            {
                canvas.Clear(new SKColor(160, 160, 160));
                canvas.DrawRect(0, 0, width * 0.15f, height * 0.15f, paint);
                canvas.Flush();
                return PageImage.FromJpeg(Imaging.EncodeJpeg(bitmap), dpi, "test", rotation);
            }
        }

        /// <summary>Draws the page on a white Letter "sheet" at 100 pixels per inch and finds the image and its mark.</summary>
        [SupportedOSPlatform("windows")]
        static (Rectangle Image, PointF Mark) DrawOnPaper(PageImage page, PointF hardMargin, RectangleF printableArea, string name)
        {
            using (var paper = new Bitmap(850, 1100, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(paper))
                {
                    g.PageUnit = GraphicsUnit.Pixel;
                    g.Clear(Color.White);
                    // A printer Graphics starts at the hard margin, not at the paper corner.
                    g.TranslateTransform(hardMargin.X, hardMargin.Y);
                    WindowsPrinting.DrawPage(g, page, new RectangleF(0, 0, 850, 1100), printableArea, hardMargin);
                }
                paper.Save(Path.Combine(Program.OutDir, $"gdi-{name}.png"), ImageFormat.Png);

                var data = paper.LockBits(new Rectangle(0, 0, paper.Width, paper.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                var pixels = new byte[data.Stride * data.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                paper.UnlockBits(data);

                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                double markX = 0, markY = 0;
                var marked = 0;
                for (var y = 0; y < paper.Height; y++)
                    for (var x = 0; x < paper.Width; x++)
                    {
                        var v = pixels[y * data.Stride + x * 3 + 1];
                        if (v < 220) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
                        if (v < 60) { markX += x + 0.5; markY += y + 0.5; marked++; }
                    }
                Check(marked > 0, name + ": the mark is on the paper");
                return (Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1), new PointF((float)(markX / marked), (float)(markY / marked)));
            }
        }

        [SupportedOSPlatform("windows")]
        static void CheckDrawn(string name, PageImage page, int rotation, Rectangle expectedBox, PointF hardMargin, RectangleF printableArea)
        {
            var (image, mark) = DrawOnPaper(page, hardMargin, printableArea, name);
            Check(Math.Abs(image.Left - expectedBox.Left) <= 2 && Math.Abs(image.Top - expectedBox.Top) <= 2 &&
                  Math.Abs(image.Right - expectedBox.Right) <= 2 && Math.Abs(image.Bottom - expectedBox.Bottom) <= 2,
                $"{name}: image at {expectedBox} (got {image})");
            var (fx, fy) = TurnClockwise(0.075, 0.075, rotation);
            var expected = new PointF((float)(expectedBox.Left + fx * expectedBox.Width), (float)(expectedBox.Top + fy * expectedBox.Height));
            Check(Math.Abs(mark.X - expected.X) <= 3 && Math.Abs(mark.Y - expected.Y) <= 3,
                $"{name}: top-left mark turned {rotation} at ({expected.X:0.#}, {expected.Y:0.#}) (got ({mark.X:0.#}, {mark.Y:0.#}))");
        }

        [Test("Windows: the printed bitmap is a plain memory copy, never the JPEG itself", Only = "windows")]
        [SupportedOSPlatform("windows")]
        static void WindowsPrintBitmap()
        {
            // A JPEG-backed bitmap goes to the driver as the JPEG ("passthrough"); Microsoft Print to PDF
            // printed such unturned pages blank. Every page must be decoded into memory first.
            foreach (var (rotation, color) in new[] { (0, false), (0, true), (90, false), (270, true) })
            {
                var page = MarkedPage(300, 200, 100, 0);
                if (color) page = PageImage.FromJpeg(TestImages.Jpeg(300, 200, true), 100, "test");
                using (var bitmap = WindowsPrinting.PrintBitmap(page, rotation))
                {
                    Check(bitmap.RawFormat.Guid == ImageFormat.MemoryBmp.Guid, $"rotation {rotation}, color {color}: memory bitmap (got {bitmap.RawFormat})");
                    var turned = rotation == 90 || rotation == 270;
                    Check(bitmap.Width == (turned ? 200 : 300) && bitmap.Height == (turned ? 300 : 200), $"rotation {rotation}: size {bitmap.Width}x{bitmap.Height}");
                    if (!color)
                    {
                        // The mark (top-left 45 x 30 pixels of the stored image) after the turn.
                        var (fx, fy) = TurnClockwise(0.075, 0.075, rotation);
                        var mark = bitmap.GetPixel((int)(fx * bitmap.Width), (int)(fy * bitmap.Height));
                        var gray = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
                        Check(mark.G < 40 && gray.G > 140 && gray.G < 180, $"rotation {rotation}: mark black ({mark.G}), page gray ({gray.G})");
                    }
                }
            }
        }

        [Test("Windows: GDI page drawing puts the image's corner where the turn says", Only = "windows")]
        [SupportedOSPlatform("windows")]
        static void WindowsDrawPage()
        {
            // Hard margins 0.25/0.15/0.30/0.20 in: the uniform margin is 0.30 in, so the printable area is 7.9 x 10.4 in at (0.3, 0.3).
            var hardMargin = new PointF(25, 15);
            var printableArea = new RectangleF(25, 15, 850 - 25 - 30, 1100 - 15 - 20);

            // 3 x 4 in portrait, as scanned: centred at real size.
            CheckDrawn("rotation-0", MarkedPage(300, 400, 100, 0), 0, new Rectangle(275, 350, 300, 400), hardMargin, printableArea);
            // 4 x 3 in landscape that the user turned to portrait: the mark moves to the top-right.
            CheckDrawn("rotation-90", MarkedPage(400, 300, 100, 90), 90, new Rectangle(275, 350, 300, 400), hardMargin, printableArea);
            // 5 x 3 in landscape left as it is: turned sideways automatically onto the portrait sheet.
            CheckDrawn("auto-turned", MarkedPage(500, 300, 100, 0), 90, new Rectangle(275, 300, 300, 500), hardMargin, printableArea);
            // No margins known (borderless report): the default 0.17 in, origin at the corner.
            CheckDrawn("default-margin", MarkedPage(300, 400, 100, 180), 180, new Rectangle(275, 350, 300, 400), PointF.Empty, new RectangleF(0, 0, 850, 1100));
        }
    }
}
