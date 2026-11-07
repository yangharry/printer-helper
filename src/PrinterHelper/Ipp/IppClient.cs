using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PrinterHelper.Ipp
{
    public sealed class PrinterInfo
    {
        public string MakeAndModel { get; set; }
        /// <summary>3 = idle, 4 = processing, 5 = stopped.</summary>
        public int State { get; set; }
        public IReadOnlyList<string> Reasons { get; set; } = Array.Empty<string>();
        public string StateMessage { get; set; }
        /// <summary>Toner level in percent, or null when the printer does not report it.</summary>
        public int? TonerLevel { get; set; }
        public int? TonerLowLevel { get; set; }
        public IReadOnlyList<string> MediaReady { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Reads printer status with one IPP Get-Printer-Attributes request (RFC 8011).
    /// </summary>
    public static class IppClient
    {
        static readonly string[] RequestedAttributes =
        {
            "printer-make-and-model", "printer-state", "printer-state-reasons", "printer-state-message",
            "marker-levels", "marker-low-levels", "marker-types", "media-ready",
        };

        public static async Task<PrinterInfo> GetPrinterInfoAsync(HttpClient http, string host, CancellationToken ct)
        {
            var hostOnly = StripPort(host);
            var body = BuildGetPrinterAttributes($"ipp://{hostOnly}:631/ipp/print");
            using (var content = new ByteArrayContent(body))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/ipp");
                using (var response = await http.PostAsync($"http://{hostOnly}:631/ipp/print", content, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new IOException($"IPP HTTP {(int)response.StatusCode}");
                    var data = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    return ToPrinterInfo(Parse(data));
                }
            }
        }

        internal static PrinterInfo ToPrinterInfo(Dictionary<string, List<object>> attrs)
        {
            string First(string name) => attrs.TryGetValue(name, out var v) ? v.OfType<string>().FirstOrDefault() : null;
            int? FirstInt(string name) => attrs.TryGetValue(name, out var v) ? v.OfType<int>().Cast<int?>().FirstOrDefault() : null;

            var info = new PrinterInfo
            {
                MakeAndModel = First("printer-make-and-model"),
                State = FirstInt("printer-state") ?? 0,
                StateMessage = First("printer-state-message"),
                Reasons = attrs.TryGetValue("printer-state-reasons", out var r) ? r.OfType<string>().Where(s => s != "none").ToArray() : Array.Empty<string>(),
                MediaReady = attrs.TryGetValue("media-ready", out var m) ? m.OfType<string>().ToArray() : Array.Empty<string>(),
            };

            // Use the first toner marker; negative levels mean "unknown".
            if (attrs.TryGetValue("marker-levels", out var levels))
            {
                var types = attrs.TryGetValue("marker-types", out var t) ? t.OfType<string>().ToList() : new List<string>();
                var lows = attrs.TryGetValue("marker-low-levels", out var l) ? l.OfType<int>().ToList() : new List<int>();
                var values = levels.OfType<int>().ToList();
                var index = types.FindIndex(x => x.StartsWith("toner", StringComparison.OrdinalIgnoreCase));
                if (index < 0) index = 0;
                if (index < values.Count && values[index] >= 0) info.TonerLevel = Math.Min(values[index], 100);
                if (index < lows.Count && lows[index] >= 0) info.TonerLowLevel = lows[index];
            }
            return info;
        }

        internal static byte[] BuildGetPrinterAttributes(string printerUri)
        {
            var ms = new MemoryStream();
            ms.WriteByte(2); ms.WriteByte(0);              // IPP 2.0
            WriteShort(ms, 0x000B);                        // Get-Printer-Attributes
            WriteInt(ms, 1);                               // request-id
            ms.WriteByte(0x01);                            // operation-attributes-tag
            WriteAttribute(ms, 0x47, "attributes-charset", "utf-8");
            WriteAttribute(ms, 0x48, "attributes-natural-language", "en");
            WriteAttribute(ms, 0x45, "printer-uri", printerUri);
            for (var i = 0; i < RequestedAttributes.Length; i++)
                WriteAttribute(ms, 0x44, i == 0 ? "requested-attributes" : "", RequestedAttributes[i]);
            ms.WriteByte(0x03);                            // end-of-attributes-tag
            return ms.ToArray();
        }

        /// <summary>Parses an IPP response into attribute name → values (string, int or bool).</summary>
        internal static Dictionary<string, List<object>> Parse(byte[] d)
        {
            if (d.Length < 8) throw new IOException("IPP 응답이 너무 짧습니다.");
            var status = (d[2] << 8) | d[3];
            if (status >= 0x0400) throw new IOException($"IPP 오류 0x{status:X4}");

            var result = new Dictionary<string, List<object>>(StringComparer.Ordinal);
            List<object> current = null;
            var collectionDepth = 0;
            var i = 8;
            while (i < d.Length)
            {
                int tag = d[i++];
                if (tag == 0x03) break;                    // end-of-attributes
                if (tag < 0x10) { current = null; continue; } // group delimiter
                if (tag == 0x7F) i += 4;                   // extended tag value
                if (i + 2 > d.Length) break;
                var nameLength = (d[i] << 8) | d[i + 1]; i += 2;
                if (i + nameLength + 2 > d.Length) break;
                var name = Encoding.UTF8.GetString(d, i, nameLength); i += nameLength;
                var valueLength = (d[i] << 8) | d[i + 1]; i += 2;
                if (i + valueLength > d.Length) break;
                var start = i;
                i += valueLength;

                // Skip members of collection values; none of the attributes we read are collections.
                if (tag == 0x34) { collectionDepth++; continue; }
                if (tag == 0x37) { collectionDepth = Math.Max(0, collectionDepth - 1); continue; }
                if (collectionDepth > 0) continue;

                if (nameLength > 0)
                {
                    if (!result.TryGetValue(name, out current))
                        result[name] = current = new List<object>();
                }
                if (current == null) continue;

                object value;
                switch (tag)
                {
                    case 0x21: // integer
                    case 0x23: // enum
                        value = valueLength == 4 ? (object)((d[start] << 24) | (d[start + 1] << 16) | (d[start + 2] << 8) | d[start + 3]) : null;
                        break;
                    case 0x22: // boolean
                        value = valueLength == 1 ? (object)(d[start] != 0) : null;
                        break;
                    case 0x35: // textWithLanguage
                    case 0x36: // nameWithLanguage
                        value = ReadWithLanguage(d, start, valueLength);
                        break;
                    default:
                        value = tag >= 0x40 && tag <= 0x5F ? Encoding.UTF8.GetString(d, start, valueLength) : null;
                        break;
                }
                if (value != null) current.Add(value);
            }
            return result;
        }

        static string ReadWithLanguage(byte[] d, int start, int length)
        {
            if (length < 4) return null;
            var langLength = (d[start] << 8) | d[start + 1];
            var textAt = start + 2 + langLength;
            if (textAt + 2 > start + length) return null;
            var textLength = (d[textAt] << 8) | d[textAt + 1];
            if (textAt + 2 + textLength > start + length) return null;
            return Encoding.UTF8.GetString(d, textAt + 2, textLength);
        }

        static string StripPort(string host)
        {
            var colon = host.LastIndexOf(':');
            return colon > 0 && host.IndexOf(':') == colon ? host.Substring(0, colon) : host;
        }

        static void WriteAttribute(Stream s, byte tag, string name, string value)
        {
            var n = Encoding.UTF8.GetBytes(name);
            var v = Encoding.UTF8.GetBytes(value);
            s.WriteByte(tag);
            WriteShort(s, n.Length); s.Write(n, 0, n.Length);
            WriteShort(s, v.Length); s.Write(v, 0, v.Length);
        }

        static void WriteShort(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }

        static void WriteInt(Stream s, int v) { WriteShort(s, v >> 16); WriteShort(s, v & 0xFFFF); }
    }
}
