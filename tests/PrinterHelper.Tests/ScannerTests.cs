using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PrinterHelper.Documents;
using PrinterHelper.Escl;
using PrinterHelper.Ipp;
using static PrinterHelper.Tests.Assert;

namespace PrinterHelper.Tests
{
    static class ScannerTests
    {
        [Test("IPP request/response round trip")]
        static void IppRoundTrip()
        {
            var request = IppClient.BuildGetPrinterAttributes("ipp://10.0.0.5:631/ipp/print");
            Check(request[0] == 2 && request[1] == 0 && request[2] == 0 && request[3] == 0x0B, "IPP 2.0 Get-Printer-Attributes");
            Check(request[request.Length - 1] == 3, "ends with end-of-attributes");

            // Hand-built response: status ok, printer group with a few attributes.
            var ms = new MemoryStream();
            void Short(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            void Attr(byte tag, string name, byte[] value)
            {
                var n = Encoding.UTF8.GetBytes(name);
                ms.WriteByte(tag); Short(n.Length); ms.Write(n, 0, n.Length); Short(value.Length); ms.Write(value, 0, value.Length);
            }
            byte[] Int(int v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            ms.Write(new byte[] { 2, 0, 0, 0, 0, 0, 0, 1 }, 0, 8);
            ms.WriteByte(0x01);
            Attr(0x47, "attributes-charset", Encoding.ASCII.GetBytes("utf-8"));
            ms.WriteByte(0x04);
            Attr(0x41, "printer-make-and-model", Encoding.UTF8.GetBytes("M208x Series"));
            Attr(0x23, "printer-state", Int(3));
            Attr(0x44, "printer-state-reasons", Encoding.ASCII.GetBytes("toner-low-report"));
            Attr(0x44, "", Encoding.ASCII.GetBytes("media-empty-warning"));
            Attr(0x44, "marker-types", Encoding.ASCII.GetBytes("toner"));
            Attr(0x21, "marker-levels", Int(40));
            Attr(0x21, "marker-low-levels", Int(5));
            Attr(0x34, "media-col-ready", new byte[0]);     // a collection we must skip
            Attr(0x4A, "", Encoding.ASCII.GetBytes("media-size"));
            Attr(0x37, "", new byte[0]);
            Attr(0x44, "media-ready", Encoding.ASCII.GetBytes("iso_a4_210x297mm"));
            ms.WriteByte(0x03);

            var info = IppClient.ToPrinterInfo(IppClient.Parse(ms.ToArray()));
            Check(info.MakeAndModel == "M208x Series", "model");
            Check(info.State == 3, "state");
            Check(info.Reasons.SequenceEqual(new[] { "toner-low-report", "media-empty-warning" }), "reasons");
            Check(info.TonerLevel == 40 && info.TonerLowLevel == 5, "toner");
            Check(info.MediaReady.SequenceEqual(new[] { "iso_a4_210x297mm" }), "media-ready after collection");
        }

        [Test("eSCL job path normalization")]
        static void JobPathNormalization()
        {
            Check(EsclClient.NormalizeJobPath("http://SEC001122334455.local./eSCL/ScanJobs/5") == "/eSCL/ScanJobs/5", "absolute .local");
            Check(EsclClient.NormalizeJobPath("/eSCL/ScanJobs/5/") == "/eSCL/ScanJobs/5", "trailing slash");
            Check(EsclClient.NormalizeJobPath("eSCL/ScanJobs/5") == "/eSCL/ScanJobs/5", "relative");
        }

        static byte[][] FakePages(int n) =>
            Enumerable.Range(0, n).Select(i => TestImages.Jpeg(170 + i, 220, false)).ToArray();

        [Test("eSCL scan against a fake scanner")]
        static async Task FakeScan()
        {
            using (var server = new FakeEsclServer(FakePages(3), busyAnswersPerPage: 2))
            using (var client = new EsclClient(server.Host))
            {
                var job = await client.CreateJobAsync(new ScanRequest { Source = ScanSource.Feeder, Dpi = 200, Width = 2480, Height = 3508 }, CancellationToken.None);
                Check(job == "/eSCL/ScanJobs/42", "job path from absolute .local Location");
                Check(server.LastScanSettings.Contains("<pwg:InputSource>Feeder</pwg:InputSource>"), "feeder requested");
                Check(server.LastScanSettings.Contains("<scan:ColorMode>Grayscale8</scan:ColorMode>"), "gray requested");

                var received = new List<byte[]>();
                byte[] page;
                while ((page = await client.NextDocumentAsync(job, CancellationToken.None)) != null) received.Add(page);
                Check(received.Count == 3, $"three pages (got {received.Count})");
                Check(received.Select(p => JpegInfo.TryRead(p).Width).SequenceEqual(new[] { 170, 171, 172 }), "pages in order");
                Check(server.Requests.Count(r => r.EndsWith("/NextDocument")) == 3 * 3 + 1, "retried on 503 then stopped at 404");

                var status = await client.GetStatusAsync(CancellationToken.None);
                Check(status.IsIdle && status.AdfLoaded, "status parsed");
                Check(status.FindJob(job)?.JobState == "Completed", "job found in status");
            }
        }

        [Test("eSCL cancel sends DELETE")]
        static async Task FakeScanCancel()
        {
            using (var server = new FakeEsclServer(FakePages(1), busyAnswersPerPage: 0) { AlwaysBusy = true })
            using (var client = new EsclClient(server.Host))
            {
                var job = await client.CreateJobAsync(new ScanRequest { Source = ScanSource.Feeder, Dpi = 200, Width = 2480, Height = 3508 }, CancellationToken.None);
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1.5)))
                {
                    var canceled = false;
                    try { await client.NextDocumentAsync(job, cts.Token); }
                    catch (OperationCanceledException) { canceled = true; }
                    Check(canceled, "waiting for a busy page can be canceled");
                }
                await client.CancelJobAsync(job);
                Check(server.Requests.Contains("DELETE /eSCL/ScanJobs/42"), "DELETE sent");
            }
        }

        [LiveTest("Live read-only status")]
        static async Task LiveStatus(string host)
        {
            using (var client = new EsclClient(host))
            {
                var caps = await client.GetCapabilitiesAsync(CancellationToken.None);
                Console.WriteLine($"      eSCL: {caps.MakeAndModel}, feeder={caps.HasFeeder} ({caps.FeederCapacity}), platen={caps.HasPlaten}, dpi={string.Join("/", caps.Resolutions)}, feeder max {caps.FeederMaxWidth}x{caps.FeederMaxHeight}");
                Check(caps.HasFeeder && caps.HasPlaten, "feeder and platen");
                var status = await client.GetStatusAsync(CancellationToken.None);
                Console.WriteLine($"      status: {status.State}, ADF {status.AdfState}, {status.Jobs.Count} jobs");
            }
            using (var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) })
            {
                var info = await IppClient.GetPrinterInfoAsync(http, host, CancellationToken.None);
                Console.WriteLine($"      IPP: {info.MakeAndModel}, state {info.State}, toner {info.TonerLevel}% (low {info.TonerLowLevel}), reasons [{string.Join(",", info.Reasons)}], media [{string.Join(",", info.MediaReady)}]");
                Check(info.TonerLevel.HasValue, "toner level");
            }
        }
    }
}
