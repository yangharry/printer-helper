using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PrinterHelper.Escl;
using PrinterHelper.Printing;

namespace PrinterHelper.Discovery
{
    public sealed class FoundScanner
    {
        /// <summary>"192.168.0.48", or "192.168.0.48:8080" when eSCL is not on port 80.</summary>
        public string Host { get; set; }
        public string Model { get; set; }
        /// <summary>The installed print queue (Windows) or CUPS queue (macOS) on this device; null when it was only found on the network.</summary>
        public string QueueName { get; set; }
        /// <summary>The name the scanner announces over mDNS, e.g. "SEC001122334455 (M208x Series)"; null when it was not heard.</summary>
        public string ServiceName { get; set; }
    }

    public sealed class DiscoveryResult
    {
        /// <summary>The most likely one (an installed printer) first.</summary>
        public List<FoundScanner> Scanners { get; set; } = new List<FoundScanner>();
        /// <summary>
        /// No mDNS query could be sent on any network interface. On macOS 15+ this is what it looks like before
        /// the user allows Local Network access (System Settings, Privacy &amp; Security, Local Network); search again after.
        /// </summary>
        public bool NetworkSearchBlocked { get; set; }
    }

    /// <summary>One place a scanner might answer, before it is probed.</summary>
    internal sealed class ScannerCandidate
    {
        public string Host { get; set; }
        public string QueueName { get; set; }
        /// <summary>Model from mDNS TXT "ty", used when eSCL does not name one.</summary>
        public string Model { get; set; }
        public string ServiceName { get; set; }
        /// <summary>The device's own name from mDNS (SRV target, "XXX.local"), which an installed port may use instead of the IP.</summary>
        public string HostName { get; set; }
    }

    /// <summary>One device: the hosts to try on it, best first, and what we know about it.</summary>
    internal sealed class ScannerGroup
    {
        public List<string> Hosts { get; } = new List<string>();
        public string QueueName { get; set; }
        public string Model { get; set; }
        public string ServiceName { get; set; }
        /// <summary>Addresses and names (lower case, no port) that identify the device.</summary>
        public HashSet<string> Names { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Finds network scanners that speak eSCL.</summary>
    public static class PrinterDiscovery
    {
        /// <summary>Scanners found on the network, the most likely one (an installed printer) first.</summary>
        public static async Task<List<FoundScanner>> FindScannersAsync(CancellationToken ct) =>
            (await DiscoverAsync(ct).ConfigureAwait(false)).Scanners;

        /// <summary>
        /// Like <see cref="FindScannersAsync"/>, and also says whether the network search was blocked.
        /// Takes about two seconds; up to three and a half when an installed printer is switched off (its probe
        /// waits 3 s). Throws only OperationCanceledException, when <paramref name="ct"/> is canceled.
        /// </summary>
        public static Task<DiscoveryResult> DiscoverAsync(CancellationToken ct) =>
            DiscoverAsync(InstalledCandidates, t => MdnsBrowser.BrowseAsync(MdnsBrowser.DefaultListenTime, t), ProbeAsync, ct);

        /// <summary>Returns the scanner model when the host answers eSCL, otherwise null.</summary>
        public static async Task<string> ProbeAsync(string host, CancellationToken ct)
        {
            try
            {
                using (var client = new EsclClient(host, TimeSpan.FromSeconds(3)))
                {
                    var caps = await client.GetCapabilitiesAsync(ct).ConfigureAwait(false);
                    return string.IsNullOrWhiteSpace(caps.MakeAndModel) ? host : caps.MakeAndModel;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // HttpClient timeout
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                return null;
            }
        }

        /// <summary>
        /// The search with its three parts passed in (tests use fakes). Installed printers come first, in their
        /// own order, then what mDNS heard; a neighbour's scanner must never get ahead of the user's own printer,
        /// because the app connects to the first one. <paramref name="probe"/> behaves like <see cref="ProbeAsync"/>.
        /// </summary>
        internal static async Task<DiscoveryResult> DiscoverAsync(
            Func<List<ScannerCandidate>> installed,
            Func<CancellationToken, Task<MdnsBrowseResult>> browse,
            Func<string, CancellationToken, Task<string>> probe,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var probes = new Dictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);
            void StartProbe(string host)
            {
                var key = HostKey(host);
                if (!probes.ContainsKey(key)) probes[key] = SafeProbeAsync(probe, host.Trim(), ct);
            }

            var browsing = SafeBrowseAsync(browse, ct);
            var known = await Task.Run(() => SafeInstalled(installed), ct).ConfigureAwait(false);
            // Installed printers are asked while mDNS is still listening.
            foreach (var c in known.Where(c => !string.IsNullOrWhiteSpace(c.Host))) StartProbe(c.Host);
            var heard = await browsing.ConfigureAwait(false);

            var groups = MergeCandidates(known.Concat(MdnsCandidates(heard.Services)));
            foreach (var host in groups.SelectMany(g => g.Hosts)) StartProbe(host);
            await Task.WhenAll(probes.Values).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            return new DiscoveryResult
            {
                Scanners = PickScanners(groups, host => probes[HostKey(host)].Result),
                NetworkSearchBlocked = heard.Blocked,
            };
        }

        static List<ScannerCandidate> InstalledCandidates()
        {
            if (OperatingSystem.IsWindows())
                return WindowsPrinterPorts.GetNetworkPrinters()
                    .Select(p => new ScannerCandidate { Host = InstalledHost(p.Host), QueueName = p.QueueName })
                    .ToList();
            if (OperatingSystem.IsMacOS())
                return CupsPrinting.ListQueues()
                    .Where(q => !string.IsNullOrWhiteSpace(q.Host))
                    .OrderBy(q => q.IsDefault ? 0 : 1)
                    .Select(q => new ScannerCandidate { Host = InstalledHost(q.Host), QueueName = q.Name })
                    .ToList();
            return new List<ScannerCandidate>();
        }

        /// <summary>
        /// The printer's address without a port: a printing port (9100, 515, 631) says nothing about where
        /// eSCL listens, so the probe uses the eSCL default (80) and mDNS can add the real port.
        /// </summary>
        internal static string InstalledHost(string host)
        {
            host = host?.Trim() ?? "";
            if (host.StartsWith("["))
            {
                var close = host.IndexOf(']');
                return close > 0 ? host.Substring(0, close + 1) : host;
            }
            var colon = host.IndexOf(':');
            return colon > 0 && colon == host.LastIndexOf(':') ? host.Substring(0, colon) : host;
        }

        /// <summary>
        /// Candidates from mDNS: plain eSCL (_uscan) answers first, as "ip" or "ip:port".
        /// _uscans is eSCL over TLS (normally port 443), which EsclClient does not speak, so those answers
        /// only contribute the device's address and we try plain http on port 80 there.
        /// </summary>
        internal static List<ScannerCandidate> MdnsCandidates(IEnumerable<MdnsService> services)
        {
            var list = (services ?? Enumerable.Empty<MdnsService>()).Where(s => s.Address != null).ToList();
            ScannerCandidate Make(MdnsService s, string host) => new ScannerCandidate
            {
                Host = host,
                Model = s.Model,
                ServiceName = string.IsNullOrWhiteSpace(s.InstanceName) ? null : s.InstanceName,
                HostName = s.HostName,
            };
            var result = new List<ScannerCandidate>();
            foreach (var s in list.Where(s => !s.IsTls))
                result.Add(Make(s, s.Port == 80 ? s.Address.ToString() : s.Address + ":" + s.Port));
            foreach (var s in list.Where(s => s.IsTls))
                result.Add(Make(s, s.Address.ToString()));
            return result;
        }

        /// <summary>
        /// Groups candidates (in priority order) by device: the same host, the same IP on another port,
        /// or the device's .local name all mean the same device. Groups keep the position of their first
        /// candidate, so mDNS answers from the user's own printer stay with it at the top.
        /// </summary>
        internal static List<ScannerGroup> MergeCandidates(IEnumerable<ScannerCandidate> candidates)
        {
            var groups = new List<ScannerGroup>();
            foreach (var c in candidates)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.Host)) continue;
                var host = c.Host.Trim();
                var names = new HashSet<string>(DeviceNames(host).Concat(DeviceNames(c.HostName)), StringComparer.OrdinalIgnoreCase);

                var matches = groups.Where(g => g.Names.Overlaps(names)).ToList();
                ScannerGroup group;
                if (matches.Count == 0)
                {
                    group = new ScannerGroup();
                    groups.Add(group);
                }
                else
                {
                    // This candidate shows two groups are one device: fold the later ones into the first.
                    group = matches[0];
                    foreach (var other in matches.Skip(1))
                    {
                        foreach (var h in other.Hosts) AddHost(group, h);
                        group.Names.UnionWith(other.Names);
                        Fill(group, other.QueueName, other.Model, other.ServiceName);
                        groups.Remove(other);
                    }
                }
                group.Names.UnionWith(names);
                AddHost(group, host);
                Fill(group, c.QueueName, c.Model, c.ServiceName);
            }
            return groups;
        }

        /// <summary>
        /// One scanner per device: the first of its hosts that answered eSCL. <paramref name="probed"/> gives
        /// null for no answer, "" for an answer without a model name, or the model.
        /// </summary>
        internal static List<FoundScanner> PickScanners(IReadOnlyList<ScannerGroup> groups, Func<string, string> probed)
        {
            var found = new List<FoundScanner>();
            foreach (var g in groups)
            {
                foreach (var host in g.Hosts)
                {
                    var model = probed(host);
                    if (model == null) continue;
                    found.Add(new FoundScanner
                    {
                        Host = host,
                        Model = model.Length > 0 ? model : g.Model ?? host,
                        QueueName = g.QueueName,
                        ServiceName = g.ServiceName,
                    });
                    break;
                }
            }
            return found;
        }

        static void AddHost(ScannerGroup group, string host)
        {
            if (!group.Hosts.Any(h => HostKey(h) == HostKey(host))) group.Hosts.Add(host);
        }

        static void Fill(ScannerGroup group, string queueName, string model, string serviceName)
        {
            group.QueueName ??= queueName;
            group.Model ??= string.IsNullOrWhiteSpace(model) ? null : model;
            group.ServiceName ??= serviceName;
        }

        /// <summary>Same key for hosts that reach the same eSCL server: case and an explicit ":80" don't matter.</summary>
        static string HostKey(string host)
        {
            var h = host.Trim().ToLowerInvariant();
            return h.EndsWith(":80") && h.IndexOf(':') == h.Length - 3 ? h.Substring(0, h.Length - 3) : h;
        }

        /// <summary>The host without a port, lower case; a .local name also without ".local" (ports may use either).</summary>
        static IEnumerable<string> DeviceNames(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) yield break;
            var name = InstalledHost(host).Trim('[', ']').TrimEnd('.').ToLowerInvariant();
            if (name.Length == 0) yield break;
            yield return name;
            if (name.EndsWith(".local") && name.Length > ".local".Length)
                yield return name.Substring(0, name.Length - ".local".Length);
        }

        static List<ScannerCandidate> SafeInstalled(Func<List<ScannerCandidate>> installed)
        {
            try
            {
                return installed() ?? new List<ScannerCandidate>();
            }
            catch (Exception)
            {
                return new List<ScannerCandidate>(); // WMI or lpstat trouble: mDNS and typing the address still work
            }
        }

        static async Task<MdnsBrowseResult> SafeBrowseAsync(Func<CancellationToken, Task<MdnsBrowseResult>> browse, CancellationToken ct)
        {
            try
            {
                return await browse(ct).ConfigureAwait(false) ?? new MdnsBrowseResult();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return new MdnsBrowseResult();
            }
        }

        /// <summary>null when the host is not a scanner, "" when it is one without a model name, otherwise the model.</summary>
        static async Task<string> SafeProbeAsync(Func<string, CancellationToken, Task<string>> probe, string host, CancellationToken ct)
        {
            try
            {
                var model = await probe(host, ct).ConfigureAwait(false);
                return model == null ? null : model == host || string.IsNullOrWhiteSpace(model) ? "" : model;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
