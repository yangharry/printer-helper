using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace PrinterHelper.Escl
{
    public enum ScanSource { Feeder, Platen }

    public sealed class ScanRequest
    {
        public ScanSource Source { get; set; }
        public bool Color { get; set; }
        public int Dpi { get; set; } = 200;
        /// <summary>Scan area in 1/300 inch.</summary>
        public int Width { get; set; }
        public int Height { get; set; }
    }

    public sealed class ScannerCapabilities
    {
        public string MakeAndModel { get; set; }
        public bool HasPlaten { get; set; }
        public bool HasFeeder { get; set; }
        public int PlatenMaxWidth { get; set; } = 2550;
        public int PlatenMaxHeight { get; set; } = 3507;
        public int FeederMaxWidth { get; set; } = 2550;
        public int FeederMaxHeight { get; set; } = 4200;
        public int FeederCapacity { get; set; }
        public IReadOnlyList<int> Resolutions { get; set; } = new[] { 100, 200, 300 };

        public int MaxWidth(ScanSource s) => s == ScanSource.Feeder ? FeederMaxWidth : PlatenMaxWidth;
        public int MaxHeight(ScanSource s) => s == ScanSource.Feeder ? FeederMaxHeight : PlatenMaxHeight;
    }

    public sealed class EsclJobInfo
    {
        public string JobUri { get; set; }
        public string JobState { get; set; }
        public IReadOnlyList<string> Reasons { get; set; } = Array.Empty<string>();
    }

    public sealed class ScannerStatus
    {
        /// <summary>Idle, Processing, Testing, Stopped or Down.</summary>
        public string State { get; set; }
        /// <summary>ScannerAdfLoaded, ScannerAdfEmpty, ScannerAdfJam, ... or null when there is no feeder.</summary>
        public string AdfState { get; set; }
        public IReadOnlyList<EsclJobInfo> Jobs { get; set; } = Array.Empty<EsclJobInfo>();

        public bool IsIdle => State == "Idle";
        public bool AdfLoaded => AdfState == "ScannerAdfLoaded";

        public EsclJobInfo FindJob(string jobPath) =>
            Jobs.FirstOrDefault(j => string.Equals(EsclClient.NormalizeJobPath(j.JobUri), jobPath, StringComparison.OrdinalIgnoreCase));
    }

    public sealed class EsclException : Exception
    {
        public EsclException(string message) : base(message) { }
    }

    /// <summary>
    /// Minimal eSCL (AirScan / Mopria scan) client. Talks to the scanner over plain HTTP,
    /// so no vendor scanner driver is needed.
    /// </summary>
    public sealed class EsclClient : IDisposable
    {
        const string ScanNs = "http://schemas.hp.com/imaging/escl/2011/05/03";
        const string PwgNs = "http://www.pwg.org/schemas/2010/12/sm";

        /// <summary>How long a page may stay "busy" (HTTP 503) before giving up.</summary>
        public static readonly TimeSpan PageWaitLimit = TimeSpan.FromSeconds(90);

        readonly HttpClient http;

        public string Host { get; }

        public EsclClient(string host, TimeSpan? timeout = null)
        {
            Host = host;
            http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(120) };
        }

        string Url(string path) => "http://" + Host + path;

        public async Task<ScannerCapabilities> GetCapabilitiesAsync(CancellationToken ct)
        {
            var doc = await GetXmlAsync("/eSCL/ScannerCapabilities", ct).ConfigureAwait(false);
            var caps = new ScannerCapabilities { MakeAndModel = Value(doc, "MakeAndModel") };

            var platen = Find(doc, "PlatenInputCaps").FirstOrDefault();
            if (platen != null)
            {
                caps.HasPlaten = true;
                caps.PlatenMaxWidth = IntValue(platen, "MaxWidth") ?? caps.PlatenMaxWidth;
                caps.PlatenMaxHeight = IntValue(platen, "MaxHeight") ?? caps.PlatenMaxHeight;
            }
            var feeder = Find(doc, "AdfSimplexInputCaps").FirstOrDefault();
            if (feeder != null)
            {
                caps.HasFeeder = true;
                caps.FeederMaxWidth = IntValue(feeder, "MaxWidth") ?? caps.FeederMaxWidth;
                caps.FeederMaxHeight = IntValue(feeder, "MaxHeight") ?? caps.FeederMaxHeight;
            }
            caps.FeederCapacity = IntValue(doc, "FeederCapacity") ?? 0;

            var resolutions = Find(doc, "DiscreteResolution")
                .Select(r => IntValue(r, "XResolution"))
                .Where(r => r.HasValue && r.Value > 0)
                .Select(r => r.Value)
                .Distinct()
                .OrderBy(r => r)
                .ToArray();
            if (resolutions.Length > 0) caps.Resolutions = resolutions;
            return caps;
        }

        public async Task<ScannerStatus> GetStatusAsync(CancellationToken ct)
        {
            var doc = await GetXmlAsync("/eSCL/ScannerStatus", ct).ConfigureAwait(false);
            var root = doc.Root;
            return new ScannerStatus
            {
                State = root.Elements().FirstOrDefault(e => e.Name.LocalName == "State")?.Value,
                AdfState = root.Elements().FirstOrDefault(e => e.Name.LocalName == "AdfState")?.Value,
                Jobs = Find(root, "JobInfo").Select(j => new EsclJobInfo
                {
                    JobUri = Value(j, "JobUri"),
                    JobState = Value(j, "JobState"),
                    Reasons = Find(j, "JobStateReason").Select(r => r.Value).ToArray(),
                }).ToArray(),
            };
        }

        /// <summary>Starts a scan job and returns its path (e.g. /eSCL/ScanJobs/12).</summary>
        public async Task<string> CreateJobAsync(ScanRequest request, CancellationToken ct)
        {
            var body = BuildScanSettings(request);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (true)
            {
                using (var content = new StringContent(body, Encoding.UTF8, "text/xml"))
                using (var response = await http.PostAsync(Url("/eSCL/ScanJobs"), content, ct).ConfigureAwait(false))
                {
                    var code = (int)response.StatusCode;
                    if (code == 201 && response.Headers.Location != null)
                        return NormalizeJobPath(response.Headers.Location.OriginalString);

                    // 503: the scanner is still busy with something else; try again shortly.
                    if (code == 503 && DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(RetryDelay(response), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (code == 409)
                        throw new EsclException("프린터가 이 스캔 설정을 받아들이지 않았습니다 (HTTP 409).");
                    if (code == 503)
                        throw new EsclException("프린터가 다른 작업 중이라 스캔을 시작하지 못했습니다.");
                    throw new EsclException($"스캔 작업을 시작하지 못했습니다 (HTTP {code}).");
                }
            }
        }

        /// <summary>
        /// Returns the next scanned page as JPEG bytes, or null when the job has no more pages.
        /// </summary>
        public async Task<byte[]> NextDocumentAsync(string jobPath, CancellationToken ct)
        {
            var giveUpAt = DateTime.UtcNow + PageWaitLimit;
            while (true)
            {
                using (var response = await http.GetAsync(Url(jobPath + "/NextDocument"), HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    var code = (int)response.StatusCode;
                    if (code == 200)
                        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (code == 404)
                        return null;
                    if (code == 503)
                    {
                        if (DateTime.UtcNow > giveUpAt)
                            throw new EsclException("프린터가 90초 동안 다음 쪽을 보내지 않았습니다.");
                        await Task.Delay(RetryDelay(response), ct).ConfigureAwait(false);
                        continue;
                    }
                    throw new EsclException($"프린터가 오류를 보냈습니다 (HTTP {code}).");
                }
            }
        }

        /// <summary>Cancels a job on the scanner. Never throws.</summary>
        public async Task CancelJobAsync(string jobPath)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                using (await http.DeleteAsync(Url(jobPath), cts.Token).ConfigureAwait(false)) { }
            }
            catch
            {
                // Best effort: the scanner also drops abandoned jobs on its own.
            }
        }

        public void Dispose() => http.Dispose();

        internal static string NormalizeJobPath(string location)
        {
            if (string.IsNullOrEmpty(location)) return location;
            // Location may be absolute and may name the printer by its .local host name;
            // keep only the path so every request goes to the address we already use.
            if (Uri.TryCreate(location, UriKind.Absolute, out var absolute) && (absolute.Scheme == "http" || absolute.Scheme == "https"))
                location = absolute.AbsolutePath;
            location = location.TrimEnd('/');
            return location.StartsWith("/", StringComparison.Ordinal) ? location : "/" + location;
        }

        static TimeSpan RetryDelay(HttpResponseMessage response)
        {
            var retry = response.Headers.RetryAfter?.Delta;
            if (retry.HasValue && retry.Value > TimeSpan.Zero && retry.Value < TimeSpan.FromSeconds(10))
                return retry.Value;
            return TimeSpan.FromMilliseconds(500);
        }

        static string BuildScanSettings(ScanRequest r)
        {
            // Element order matches what the Samsung M2085FW accepted in testing.
            var inv = CultureInfo.InvariantCulture;
            return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
                   $"<scan:ScanSettings xmlns:scan=\"{ScanNs}\" xmlns:pwg=\"{PwgNs}\">\n" +
                   "  <pwg:Version>2.63</pwg:Version>\n" +
                   "  <scan:Intent>Document</scan:Intent>\n" +
                   "  <pwg:ScanRegions>\n" +
                   "    <pwg:ScanRegion>\n" +
                   "      <pwg:ContentRegionUnits>escl:ThreeHundredthsOfInches</pwg:ContentRegionUnits>\n" +
                   "      <pwg:XOffset>0</pwg:XOffset>\n" +
                   "      <pwg:YOffset>0</pwg:YOffset>\n" +
                   $"      <pwg:Width>{r.Width.ToString(inv)}</pwg:Width>\n" +
                   $"      <pwg:Height>{r.Height.ToString(inv)}</pwg:Height>\n" +
                   "    </pwg:ScanRegion>\n" +
                   "  </pwg:ScanRegions>\n" +
                   $"  <pwg:InputSource>{(r.Source == ScanSource.Feeder ? "Feeder" : "Platen")}</pwg:InputSource>\n" +
                   "  <pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat>\n" +
                   "  <scan:DocumentFormatExt>image/jpeg</scan:DocumentFormatExt>\n" +
                   $"  <scan:ColorMode>{(r.Color ? "RGB24" : "Grayscale8")}</scan:ColorMode>\n" +
                   $"  <scan:XResolution>{r.Dpi.ToString(inv)}</scan:XResolution>\n" +
                   $"  <scan:YResolution>{r.Dpi.ToString(inv)}</scan:YResolution>\n" +
                   "</scan:ScanSettings>\n";
        }

        async Task<XDocument> GetXmlAsync(string path, CancellationToken ct)
        {
            using (var response = await http.GetAsync(Url(path), ct).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                    throw new EsclException($"프린터가 응답하지 않았습니다 (HTTP {(int)response.StatusCode}).");
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return XDocument.Parse(text);
            }
        }

        static IEnumerable<XElement> Find(XContainer c, string localName) =>
            c.Descendants().Where(e => e.Name.LocalName == localName);

        static string Value(XContainer c, string localName) => Find(c, localName).FirstOrDefault()?.Value;

        static int? IntValue(XContainer c, string localName) =>
            int.TryParse(Value(c, localName), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int?)null;
    }
}
