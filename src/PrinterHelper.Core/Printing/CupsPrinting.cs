using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PrinterHelper.Documents;

namespace PrinterHelper.Printing
{
    public enum SheetSize { A4, Letter, Legal }

    /// <summary>A printer queue known to CUPS (macOS, Linux).</summary>
    public sealed class CupsQueue
    {
        /// <summary>Queue name used with lp -d.</summary>
        public string Name { get; set; }
        /// <summary>Name to show (printer-info); may be Korean.</summary>
        public string Info { get; set; }
        public string DeviceUri { get; set; }
        public string MakeAndModel { get; set; }
        public bool IsDefault { get; set; }
        /// <summary>IP address or host taken from the device URI, or null (e.g. dnssd:// URIs).</summary>
        public string Host { get; set; }

        public override string ToString() => string.IsNullOrWhiteSpace(Info) ? Name : Info;
    }

    /// <summary>Printing through CUPS: the pages are laid out on the sheet in a PDF, which lp sends to the queue.</summary>
    public static class CupsPrinting
    {
        // Absolute paths: an app started from Finder has a minimal PATH, and nothing goes through a shell.
        const string LpstatPath = "/usr/bin/lpstat";
        const string LpoptionsPath = "/usr/bin/lpoptions";
        const string LpPath = "/usr/bin/lp";

        static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(10);
        // lp only hands the file to the local scheduler, but a big colour job takes a moment to copy.
        static readonly TimeSpan LpTimeout = TimeSpan.FromMinutes(2);

        /// <summary>
        /// All queues, or an empty list when CUPS is not available or does not answer within 10 seconds.
        /// A queue whose details do not come in time is still listed, by name only.
        /// </summary>
        public static IReadOnlyList<CupsQueue> ListQueues()
        {
            try
            {
                var clock = Stopwatch.StartNew();
                TimeSpan Left() => ListTimeout - clock.Elapsed;

                // lpstat -e is not localized and lists queue names only, one per line.
                var names = Run(LpstatPath, new[] { "-e" }, Left());
                if (names == null || names.ExitCode != 0) return Array.Empty<CupsQueue>();
                var queueNames = ParseQueueNames(names.Output);
                if (queueNames.Count == 0) return Array.Empty<CupsQueue>();

                // Printers found through Bonjour but not added yet are resolved on the network by
                // lpoptions, which can take seconds each: ask for all of them at once.
                var defaultTask = Task.Run(() => Run(LpstatPath, new[] { "-d" }, Left()));
                var detailTasks = queueNames.Select(name => Task.Run(() => Run(LpoptionsPath, new[] { "-p", name }, Left()))).ToArray();
                Task.WaitAll(detailTasks.Append<Task>(defaultTask).ToArray());

                var defaultResult = defaultTask.Result;
                var defaultName = defaultResult != null && defaultResult.ExitCode == 0 ? ParseDefault(defaultResult.Output, queueNames) : null;
                var queues = new List<CupsQueue>();
                for (var i = 0; i < queueNames.Count; i++)
                {
                    var details = detailTasks[i].Result;
                    // A queue removed in the meantime still gets listed by name; lp will report it.
                    var options = details != null && details.ExitCode == 0 ? ParseOptions(details.Output) : new Dictionary<string, string>();
                    queues.Add(MakeQueue(queueNames[i], options, defaultName));
                }
                return queues;
            }
            catch (Exception)
            {
                return Array.Empty<CupsQueue>();
            }
        }

        /// <summary>The queue that prints on the scanner's device, or null (the caller then uses the default queue).</summary>
        public static CupsQueue FindQueueForScanner(IReadOnlyList<CupsQueue> queues, string scannerHost, string scannerModel)
        {
            if (queues == null || queues.Count == 0) return null;

            var host = NormalizeHost(scannerHost);
            if (host != null)
            {
                var byHost = queues.FirstOrDefault(q => q != null && NormalizeHost(q.Host) == host);
                if (byHost != null) return byHost;
            }

            // Queues added through Bonjour have dnssd:// URIs without an address; their names carry the model.
            var model = NormalizeModel(scannerModel);
            if (model.Length == 0) return null;
            var byModel = queues
                .Where(q => q != null && (ContainsWords(NormalizeModel(q.MakeAndModel), model) || ContainsWords(NormalizeModel(q.Info), model)))
                .ToList();
            return byModel.FirstOrDefault(q => q.IsDefault) ?? byModel.FirstOrDefault();
        }

        public static async Task PrintAsync(string queue, int copies, SheetSize sheet, IReadOnlyList<PageImage> pages, string title, CancellationToken ct)
        {
            if (pages == null || pages.Count == 0) throw new ArgumentException("인쇄할 페이지가 없습니다.", nameof(pages));
            if (!File.Exists(LpPath)) throw new InvalidOperationException("인쇄를 보내지 못했습니다: 이 컴퓨터에서 CUPS(lp)를 찾을 수 없습니다.");

            // Copy the list: the window may change it while the PDF is written in the background.
            var snapshot = pages.ToList();
            var file = Path.Combine(Path.GetTempPath(), "PrinterHelper-print-" + Guid.NewGuid().ToString("N") + ".pdf");
            try
            {
                await Task.Run(() =>
                {
                    using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
                        LayoutPdfWriter.Write(stream, snapshot, sheet, DateTime.Now);
                }, ct).ConfigureAwait(false);

                ToolResult result;
                try
                {
                    result = await RunAsync(LpPath, LpArguments(queue, copies, sheet, title, file), LpTimeout, ct).ConfigureAwait(false);
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw new InvalidOperationException("인쇄를 보내지 못했습니다: " + ex.Message, ex);
                }
                if (result == null)
                    throw new InvalidOperationException("인쇄를 보내지 못했습니다: lp가 응답하지 않습니다.");
                if (result.ExitCode != 0)
                {
                    var message = result.Error.Trim();
                    if (message.Length == 0) message = result.Output.Trim();
                    if (message.Length == 0) message = $"lp 종료 코드 {result.ExitCode}";
                    throw new InvalidOperationException("인쇄를 보내지 못했습니다: " + message);
                }
            }
            finally
            {
                // lp has copied the job to the spooler by the time it exits.
                try { File.Delete(file); } catch { }
            }
        }

        // ───────────────────────────── parsing (internal for tests) ─────────────────────────────

        /// <summary>The arguments for /usr/bin/lp. No queue means the default queue.</summary>
        internal static IReadOnlyList<string> LpArguments(string queue, int copies, SheetSize sheet, string title, string file)
        {
            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(queue)) { args.Add("-d"); args.Add(queue); }
            copies = Math.Clamp(copies, 1, 999);
            args.Add("-n");
            args.Add(copies.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(title)) { args.Add("-t"); args.Add(title); }
            args.Add("-o");
            args.Add("media=" + LayoutPdfWriter.MediaName(sheet));
            // The PDF is already laid out on the sheet at real size; CUPS must not fit it again.
            args.Add("-o");
            args.Add("print-scaling=none");
            // Several copies of a multi-page document come out as whole sets, as on Windows.
            if (copies > 1) { args.Add("-o"); args.Add("collate=true"); }
            args.Add(file);
            return args;
        }

        /// <summary>Queue names from `lpstat -e`, one per line.</summary>
        internal static List<string> ParseQueueNames(string output) =>
            (output ?? "").Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.Any(char.IsWhiteSpace))
                .Distinct(StringComparer.Ordinal)
                .ToList();

        /// <summary>
        /// The default queue from `lpstat -d`. The sentence around it is translated
        /// ("system default destination: X", "시스템 기본 대상: X"), so take the last word if it is a queue.
        /// </summary>
        internal static string ParseDefault(string output, IReadOnlyCollection<string> names)
        {
            var words = (output ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return null;
            var last = words[words.Length - 1];
            return names != null && names.Contains(last) ? last : null;
        }

        /// <summary>
        /// The key=value pairs printed by `lpoptions -p NAME`. Values with spaces are quoted
        /// ('Samsung M2080 Series'), with \ escaping quotes and backslashes; options without a value
        /// are printed as a bare key.
        /// </summary>
        internal static Dictionary<string, string> ParseOptions(string output)
        {
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var token in Tokenize(output ?? ""))
            {
                var eq = token.IndexOf('=');
                if (eq == 0) continue;
                if (eq < 0) options[token] = "";
                else options[token.Substring(0, eq)] = token.Substring(eq + 1);
            }
            return options;
        }

        static IEnumerable<string> Tokenize(string text)
        {
            var token = new StringBuilder();
            var inToken = false;
            char quote = '\0';
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    token.Append(text[++i]);
                    inToken = true;
                }
                else if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else token.Append(c);
                }
                else if (c == '\'' || c == '"')
                {
                    quote = c;
                    inToken = true;
                }
                else if (char.IsWhiteSpace(c))
                {
                    if (inToken) yield return token.ToString();
                    token.Clear();
                    inToken = false;
                }
                else
                {
                    token.Append(c);
                    inToken = true;
                }
            }
            if (inToken) yield return token.ToString();
        }

        internal static CupsQueue MakeQueue(string name, IReadOnlyDictionary<string, string> options, string defaultName)
        {
            string Value(string key) => options != null && options.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
            var uri = Value("device-uri");
            return new CupsQueue
            {
                Name = name,
                Info = Value("printer-info"),
                DeviceUri = uri,
                MakeAndModel = Value("printer-make-and-model"),
                IsDefault = name == defaultName,
                Host = HostFromDeviceUri(uri),
            };
        }

        static readonly string[] NetworkSchemes = { "ipp", "ipps", "socket", "lpd", "http", "https" };

        /// <summary>
        /// The printer's address from a device URI such as ipp://192.168.0.48/ipp/print or
        /// socket://[fe80::1]:9100, without port or brackets. Null for dnssd://, usb:// and the like.
        /// </summary>
        internal static string HostFromDeviceUri(string uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return null;
            var schemeEnd = uri.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0) return null;
            var scheme = uri.Substring(0, schemeEnd).ToLowerInvariant();
            if (Array.IndexOf(NetworkSchemes, scheme) < 0) return null;

            var authority = uri.Substring(schemeEnd + 3);
            var end = authority.IndexOfAny(new[] { '/', '?', '#' });
            if (end >= 0) authority = authority.Substring(0, end);
            var at = authority.LastIndexOf('@');
            if (at >= 0) authority = authority.Substring(at + 1);
            return StripPort(Unescape(authority));
        }

        /// <summary>"192.168.0.48:8080" → "192.168.0.48", "[fe80::1]:80" → "fe80::1"; a bare IPv6 address stays as it is.</summary>
        internal static string StripPort(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return null;
            host = host.Trim();
            if (host.StartsWith("[", StringComparison.Ordinal))
            {
                var close = host.IndexOf(']');
                host = close > 1 ? host.Substring(1, close - 1) : host.Trim('[', ']');
            }
            else
            {
                var colon = host.IndexOf(':');
                if (colon >= 0 && colon == host.LastIndexOf(':')) host = host.Substring(0, colon);
            }
            return host.Length > 0 ? host : null;
        }

        /// <summary>For comparing hosts: no port, brackets, IPv6 zone or trailing dot; lower case.</summary>
        static string NormalizeHost(string host)
        {
            host = StripPort(host);
            if (host == null) return null;
            var zone = host.IndexOf('%');
            if (zone > 0 && host.Contains(':')) host = host.Substring(0, zone);
            host = host.TrimEnd('.').ToLowerInvariant();
            return host.Length > 0 ? host : null;
        }

        static string Unescape(string text)
        {
            try { return Uri.UnescapeDataString(text); }
            catch (Exception) { return text; }
        }

        static readonly Regex SeriesWord = new Regex(@"\bseries\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex Spaces = new Regex(@"\s+");

        /// <summary>"Samsung M2080 Series" and "SAMSUNG M2080" compare equal: no "Series", single spaces.</summary>
        static string NormalizeModel(string model) =>
            string.IsNullOrWhiteSpace(model) ? "" : Spaces.Replace(SeriesWord.Replace(model, " "), " ").Trim();

        /// <summary>Contains, ignoring case, but only as whole words: "M2080" is not found in "M2080W".</summary>
        static bool ContainsWords(string text, string words)
        {
            if (text.Length == 0 || words.Length == 0) return false;
            for (var i = text.IndexOf(words, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(words, i + 1, StringComparison.OrdinalIgnoreCase))
            {
                var end = i + words.Length;
                var startsWord = i == 0 || !char.IsLetterOrDigit(text[i - 1]) || !char.IsLetterOrDigit(words[0]);
                var endsWord = end == text.Length || !char.IsLetterOrDigit(text[end]) || !char.IsLetterOrDigit(words[words.Length - 1]);
                if (startsWord && endsWord) return true;
            }
            return false;
        }

        // ───────────────────────────── running the CUPS tools ─────────────────────────────

        sealed class ToolResult
        {
            public int ExitCode { get; set; }
            public string Output { get; set; }
            public string Error { get; set; }
        }

        static Process Start(string program, IEnumerable<string> args)
        {
            var info = new ProcessStartInfo(program)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            var process = Process.Start(info) ?? throw new InvalidOperationException(program + " did not start");
            // Nothing is ever read from stdin; closing it keeps a tool from waiting for input.
            process.StandardInput.Close();
            return process;
        }

        /// <summary>Runs a tool and waits for it. Null when it is missing, fails to start or runs past the timeout.</summary>
        static ToolResult Run(string program, IEnumerable<string> args, TimeSpan timeout)
        {
            if (timeout <= TimeSpan.Zero || !File.Exists(program)) return null;
            try
            {
                using (var process = Start(program, args))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit((int)Math.Ceiling(timeout.TotalMilliseconds)) || !Task.WaitAll(new Task[] { output, error }, timeout))
                    {
                        Kill(process);
                        return null;
                    }
                    return new ToolResult { ExitCode = process.ExitCode, Output = output.Result, Error = error.Result };
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Null on timeout; throws OperationCanceledException when <paramref name="ct"/> is cancelled.</summary>
        static async Task<ToolResult> RunAsync(string program, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
        {
            using (var process = Start(program, args))
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(timeout);
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Kill(process);
                    ct.ThrowIfCancellationRequested();
                    return null;
                }
                return new ToolResult
                {
                    ExitCode = process.ExitCode,
                    Output = await output.ConfigureAwait(false),
                    Error = await error.ConfigureAwait(false),
                };
            }
        }

        static void Kill(Process process)
        {
            try { process.Kill(true); } catch { }
        }
    }
}
