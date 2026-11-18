using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PrinterHelper.Discovery;
using PrinterHelper.Escl;
using static PrinterHelper.Tests.Assert;

namespace PrinterHelper.Tests
{
    static class DiscoveryTests
    {
        const string SamsungInstance = "SEC001122334455 (M208x Series)";
        static readonly IPAddress SamsungIp = IPAddress.Parse("192.168.0.48");

        [Test("mDNS query bytes")]
        static void QueryBytes()
        {
            var query = MdnsPacket.BuildQuery(0x1234, MdnsBrowser.QueryNames);
            var expected = new DnsWriter()
                .U16(0x1234).U16(0).U16(2).U16(0).U16(0).U16(0)
                .Name("_uscan._tcp.local").U16(12).U16(1)
                .Name("_uscans._tcp.local").U16(12).U16(1)
                .ToArray();
            Check(query.SequenceEqual(expected), "ID, no flags, QDCOUNT=2, PTR/IN without the QU bit: " + BitConverter.ToString(query));
            Check(query.Length == 59, "uncompressed, 59 bytes");

            var parsed = MdnsPacket.Parse(query);
            Check(parsed != null && !parsed.IsResponse && parsed.Id == 0x1234 && parsed.Records.Count == 0, "our query reads back as a query");
        }

        [Test("mDNS response parsing with compression")]
        static void ResponseParsing()
        {
            var packet = SamsungAnswer(0xBEEF);
            var message = MdnsPacket.Parse(packet);
            Check(message != null, "parsed");
            Check(message.Id == 0xBEEF && message.IsResponse, "header");
            Check(message.Records.Count == 4, $"PTR, SRV, TXT, A kept; AAAA and NSEC skipped (got {message.Records.Count})");

            var ptr = message.Records[0];
            Check(ptr.Type == MdnsRecord.TypePtr && ptr.Name == "_uscan._tcp.local" && ptr.Ttl == 4500, "PTR owner through a pointer to the question");
            Check(ptr.Target == SamsungInstance + "._uscan._tcp.local", "PTR target: label then pointer");

            var srv = message.Records[1];
            Check(srv.Type == MdnsRecord.TypeSrv && srv.Name == ptr.Target, "SRV owner points into the PTR data");
            Check(srv.Port == 8080 && srv.Target == "SEC001122334455.local", "SRV port and target (cache-flush class accepted)");

            var txt = message.Records[2];
            Check(txt.Type == MdnsRecord.TypeTxt && txt.Name == ptr.Target, "TXT owner");
            Check(txt.Txt["ty"] == "M208x Series" && txt.Txt["TY"] == "M208x Series", "first ty wins, keys ignore case");
            Check(txt.Txt["rs"] == "eSCL" && txt.Txt["UUID"] == "16a65700-007c-1000-bb49-001122334455", "rs and UUID");
            Check(txt.Txt["duplex"] == "" && txt.Txt["note"] == "a=b", "flag without value, value containing '='");

            var a = message.Records[3];
            Check(a.Type == MdnsRecord.TypeA && a.Name == "SEC001122334455.local" && a.Address.Equals(SamsungIp), "A record in the additionals");

            var padded = packet.Concat(new byte[] { 0xFF, 0xFF, 0x00 }).ToArray();
            Check(MdnsPacket.Parse(padded)?.Records.Count == 4, "bytes after the counted records are ignored");
            Check(MdnsPacket.Parse(padded, packet.Length)?.Records.Count == 4, "length shorter than the buffer");
        }

        [Test("mDNS records resolve to services")]
        static void ResolveServices()
        {
            var services = MdnsBrowser.Resolve(SampleResponses());
            Check(services.Count == 3, $"three services (got {services.Count})");

            var uscan = services[0];
            Check(uscan.ServiceType == "_uscan._tcp" && !uscan.IsTls, "plain eSCL first");
            Check(uscan.InstanceName == SamsungInstance && uscan.HostName == "SEC001122334455.local", "names");
            Check(uscan.Address.Equals(SamsungIp) && uscan.Port == 8080, "address from the A record, SRV port");
            Check(uscan.Model == "M208x Series" && uscan.ResourcePath == "eSCL" && uscan.Uuid != null, "TXT values");

            var uscans = services[1];
            Check(uscans.IsTls && uscans.Port == 443 && uscans.InstanceName == SamsungInstance, "_uscans from a second packet");
            Check(uscans.Address.Equals(SamsungIp), "its A record came in the first packet");

            var hp = services[2];
            Check(hp.InstanceName == "NPI0A1B2C (HP LaserJet)" && hp.Port == 80, "second device");
            Check(hp.Address.Equals(IPAddress.Parse("192.168.0.77")), "no A record: the packet's source address");
            Check(hp.Model == "HP LaserJet MFP", "its model");

            Check(!services.Any(s => s.InstanceName == "Gone"), "goodbye (TTL 0) ignored");

            var candidates = PrinterDiscovery.MdnsCandidates(services);
            Check(candidates.Select(c => c.Host).SequenceEqual(new[] { "192.168.0.48:8080", "192.168.0.77", "192.168.0.48" }),
                "hosts: ip:port unless 80, _uscans last with the IP only: " + string.Join(", ", candidates.Select(c => c.Host)));
            Check(candidates[0].Model == "M208x Series" && candidates[0].ServiceName == SamsungInstance && candidates[0].HostName == "SEC001122334455.local", "candidate details");
        }

        [Test("mDNS malformed packets")]
        static void MalformedPackets()
        {
            var sw = Stopwatch.StartNew();
            var packet = SamsungAnswer(7);
            for (var n = 0; n < packet.Length; n++)
                Check(MdnsPacket.ParseUnchecked(packet, n) == null, $"cut at {n} of {packet.Length} bytes is ignored");
            Check(MdnsPacket.Parse(null) == null && MdnsPacket.Parse(new byte[0]) == null, "null and empty");

            byte[] Header(int questions, int answers) => new DnsWriter().U16(1).U16(0x8400).U16(questions).U16(answers).U16(0).U16(0).ToArray();
            byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
            var typeClass = new byte[] { 0, 12, 0, 1 };

            Check(MdnsPacket.ParseUnchecked(Join(Header(1, 0), new byte[] { 0xC0, 12 }, typeClass), 18) == null, "pointer to itself");
            var cycle = Join(Header(1, 0), new byte[] { 0xC0, 14, 0xC0, 12 }, typeClass);
            Check(MdnsPacket.ParseUnchecked(cycle, cycle.Length) == null, "two pointers pointing at each other");
            var growing = Join(Header(1, 0), new byte[] { 1, (byte)'a', 0xC0, 12 }, typeClass);
            Check(MdnsPacket.ParseUnchecked(growing, growing.Length) == null, "label plus pointer back to it");
            var beyond = Join(Header(1, 0), new byte[] { 0xC0, 0xFF }, typeClass);
            Check(MdnsPacket.ParseUnchecked(beyond, beyond.Length) == null, "pointer past the end");
            var reserved = Join(Header(1, 0), new byte[] { 0x40, 0 }, typeClass);
            Check(MdnsPacket.ParseUnchecked(reserved, reserved.Length) == null, "reserved label type");

            // A PTR whose data points at itself: that record is dropped, the rest of the packet still counts.
            var w = new DnsWriter().U16(1).U16(0x8400).U16(0).U16(2).U16(0).U16(0);
            w.U8(0); var at = w.RecordHeader(MdnsRecord.TypePtr, 1, 120); w.Pointer(w.Position); w.EndRecord(at);
            w.U8(0); at = w.RecordHeader(MdnsRecord.TypeA, 1, 120); w.U8(10).U8(0).U8(0).U8(1); w.EndRecord(at);
            var selfData = w.ToArray();
            var parsed = MdnsPacket.ParseUnchecked(selfData, selfData.Length);
            Check(parsed != null && parsed.Records.Count == 1 && parsed.Records[0].Type == MdnsRecord.TypeA, "looping record data skips only that record");

            // A TXT string running past its record, and an A record of the wrong size.
            w = new DnsWriter().U16(1).U16(0x8400).U16(0).U16(2).U16(0).U16(0);
            w.U8(0); at = w.RecordHeader(MdnsRecord.TypeTxt, 1, 120); w.U8(9).U8('a').U8('='); w.EndRecord(at);
            w.U8(0); at = w.RecordHeader(MdnsRecord.TypeA, 1, 120); w.U8(10).U8(0).U8(1); w.EndRecord(at);
            var badData = w.ToArray();
            parsed = MdnsPacket.ParseUnchecked(badData, badData.Length);
            Check(parsed != null && parsed.Records.Count == 0, "bad record data skipped");

            // Random damage must never throw, in parsing or in resolving.
            var random = new Random(1234);
            var source = IPAddress.Parse("192.168.0.2");
            for (var i = 0; i < 4000; i++)
            {
                byte[] data;
                if (i % 4 == 0)
                {
                    data = new byte[random.Next(0, 300)];
                    random.NextBytes(data);
                }
                else
                {
                    data = (byte[])packet.Clone();
                    for (var k = random.Next(1, 5); k > 0; k--) data[random.Next(data.Length)] = (byte)random.Next(256);
                }
                var message = MdnsPacket.ParseUnchecked(data, random.Next(data.Length + 1));
                if (message != null) MdnsBrowser.Resolve(new[] { new MdnsResponse { Message = message, Source = source } });
            }
            Check(sw.ElapsedMilliseconds < 3000, $"no hang ({sw.ElapsedMilliseconds} ms)");
        }

        [Test("mDNS interface and address filters")]
        static void InterfaceFilters()
        {
            foreach (var name in new[] { "utun3", "awdl0", "llw0", "bridge100", "bridge0" })
                Check(!MdnsBrowser.IsUsableInterfaceName(name), name + " skipped");
            foreach (var name in new[] { "en0", "en7", "Wi-Fi", "Ethernet", "이더넷" })
                Check(MdnsBrowser.IsUsableInterfaceName(name), name + " used");

            foreach (var ip in new[] { "10.1.2.3", "172.16.0.1", "172.31.255.254", "192.168.0.48", "169.254.12.34" })
                Check(MdnsBrowser.IsPrivateOrLinkLocal(IPAddress.Parse(ip)), ip + " is a LAN address");
            foreach (var ip in new[] { "8.8.8.8", "172.32.0.1", "172.15.0.1", "100.64.0.1", "127.0.0.1", "224.0.0.251", "fe80::1" })
                Check(!MdnsBrowser.IsPrivateOrLinkLocal(IPAddress.Parse(ip)), ip + " is not");

            var addresses = MdnsBrowser.GetInterfaceAddresses();
            Check(addresses.All(a => a.AddressFamily == AddressFamily.InterNetwork && MdnsBrowser.IsPrivateOrLinkLocal(a)), "this machine: LAN IPv4 only");
            Check(addresses.Distinct().Count() == addresses.Count, "no address twice");
            Console.WriteLine("      interfaces here: " + (addresses.Count == 0 ? "(none)" : string.Join(", ", addresses)));

            Check(PrinterDiscovery.InstalledHost(" 192.168.0.48 ") == "192.168.0.48", "plain address");
            Check(PrinterDiscovery.InstalledHost("192.168.0.48:9100") == "192.168.0.48", "printing port dropped");
            Check(PrinterDiscovery.InstalledHost("SEC001122334455.local") == "SEC001122334455.local", "name kept");
            Check(PrinterDiscovery.InstalledHost("[fe80::1]:631") == "[fe80::1]" && PrinterDiscovery.InstalledHost("fe80::1") == "fe80::1", "IPv6 left alone");
        }

        [Test("Discovery puts installed printers first")]
        static void MergeOrder()
        {
            var installed = new List<ScannerCandidate>
            {
                new ScannerCandidate { Host = "192.168.0.10", QueueName = "Office laser" },
                new ScannerCandidate { Host = "192.168.0.48", QueueName = "Samsung M2085FW" },
                new ScannerCandidate { Host = "192.168.0.48", QueueName = "Samsung M2085FW (copy 1)" },
            };
            // mDNS heard the Samsung on 8080 (and over TLS) and a neighbour's HP.
            var heard = PrinterDiscovery.MdnsCandidates(MdnsBrowser.Resolve(SampleResponses()));
            var groups = PrinterDiscovery.MergeCandidates(installed.Concat(heard));
            Check(groups.Count == 3, $"three devices (got {groups.Count})");
            Check(groups[0].Hosts.SequenceEqual(new[] { "192.168.0.10" }) && groups[0].QueueName == "Office laser", "installed printer without mDNS");
            Check(groups[1].Hosts.SequenceEqual(new[] { "192.168.0.48", "192.168.0.48:8080" }), "mDNS port joins the installed printer: " + string.Join(", ", groups[1].Hosts));
            Check(groups[1].QueueName == "Samsung M2085FW" && groups[1].Model == "M208x Series" && groups[1].ServiceName == SamsungInstance, "first queue, mDNS details");
            Check(groups[2].Hosts.SequenceEqual(new[] { "192.168.0.77" }) && groups[2].QueueName == null, "neighbour last");

            var probed = new Dictionary<string, string>
            {
                ["192.168.0.10"] = null,            // switched off
                ["192.168.0.48"] = null,            // eSCL is not on port 80
                ["192.168.0.48:8080"] = "",         // answers, names no model
                ["192.168.0.77"] = "HP LaserJet Pro",
            };
            var found = PrinterDiscovery.PickScanners(groups, h => probed[h]);
            Check(found.Count == 2, $"two scanners (got {found.Count})");
            Check(found[0].Host == "192.168.0.48:8080" && found[0].QueueName == "Samsung M2085FW", "own printer first, on the port that answered");
            Check(found[0].Model == "M208x Series" && found[0].ServiceName == SamsungInstance, "model from TXT ty when eSCL names none");
            Check(found[1].Host == "192.168.0.77" && found[1].Model == "HP LaserJet Pro" && found[1].QueueName == null, "neighbour after");

            probed["192.168.0.48"] = "Samsung M2085FW";
            found = PrinterDiscovery.PickScanners(groups, h => probed[h]);
            Check(found.Count == 2 && found[0].Host == "192.168.0.48" && found[0].Model == "Samsung M2085FW", "one entry per device: the installed host when it answers");
        }

        [Test("Discovery dedupes by host and device name")]
        static void MergeDedupe()
        {
            var groups = PrinterDiscovery.MergeCandidates(new[]
            {
                new ScannerCandidate { Host = "sec001122334455.local", QueueName = "Samsung" },
                new ScannerCandidate { Host = "192.168.0.99", Model = "Neighbour" },
                new ScannerCandidate { Host = "192.168.0.48", HostName = "SEC001122334455.local", Model = "M208x Series" },
                new ScannerCandidate { Host = "192.168.0.48:80" },
                new ScannerCandidate { Host = " 192.168.0.99 " },
                new ScannerCandidate { Host = "" },
                null,
                new ScannerCandidate { Host = "10.0.0.5", QueueName = "A" },
                new ScannerCandidate { Host = "PRN-5", QueueName = "B" },
                new ScannerCandidate { Host = "10.0.0.5:8080", HostName = "PRN-5.local." },
                new ScannerCandidate { Host = "192.168.0.99" },
            });
            Check(groups.Count == 3, $"three devices (got {groups.Count})");
            Check(groups[0].Hosts.SequenceEqual(new[] { "sec001122334455.local", "192.168.0.48" }), ".local name links the installed port to mDNS: " + string.Join(", ", groups[0].Hosts));
            Check(groups[0].QueueName == "Samsung" && groups[0].Model == "M208x Series", "details merged");
            Check(groups[1].Hosts.SequenceEqual(new[] { "192.168.0.99" }), "same host in other spellings counted once");
            Check(groups[2].Hosts.SequenceEqual(new[] { "10.0.0.5", "PRN-5", "10.0.0.5:8080" }), "an answer naming both joins two groups: " + string.Join(", ", groups[2].Hosts));
            Check(groups[2].QueueName == "A", "earlier queue kept");
        }

        [Test("Discovery survives failures and honours cancel")]
        static async Task DiscoveryFailures()
        {
            var r = await PrinterDiscovery.DiscoverAsync(
                () => throw new InvalidOperationException("WMI broken"),
                ct => throw new SocketException((int)SocketError.HostUnreachable),
                (h, ct) => throw new HttpRequestException("no"),
                CancellationToken.None);
            Check(r.Scanners.Count == 0 && !r.NetworkSearchBlocked, "throwing parts give an empty result");

            r = await PrinterDiscovery.DiscoverAsync(
                () => new List<ScannerCandidate> { new ScannerCandidate { Host = "192.168.0.48" } },
                ct => Task.FromException<MdnsBrowseResult>(new SocketException()),
                (h, ct) => Task.FromException<string>(new TaskCanceledException("HttpClient timeout")),
                CancellationToken.None);
            Check(r.Scanners.Count == 0, "faulted tasks and probe timeouts give an empty result");

            r = await PrinterDiscovery.DiscoverAsync(
                () => new List<ScannerCandidate>(),
                ct => Task.FromResult(new MdnsBrowseResult { Interfaces = 2, FailedInterfaces = 2 }),
                (h, ct) => Task.FromResult(h),
                CancellationToken.None);
            Check(r.NetworkSearchBlocked, "blocked mDNS is reported");

            // Installed printers are asked while mDNS is still listening, not after.
            var installedProbed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var probedDuringBrowse = false;
            r = await PrinterDiscovery.DiscoverAsync(
                () => new List<ScannerCandidate> { new ScannerCandidate { Host = "192.168.0.48", QueueName = "Samsung" } },
                async ct =>
                {
                    probedDuringBrowse = await Task.WhenAny(installedProbed.Task, Task.Delay(2000, ct)) == installedProbed.Task;
                    return new MdnsBrowseResult { Services = MdnsBrowser.Resolve(SampleResponses()), Interfaces = 1 };
                },
                (h, ct) =>
                {
                    if (h == "192.168.0.48") installedProbed.TrySetResult(true);
                    return Task.FromResult(h == "192.168.0.48" ? "Samsung M2085FW" : h == "192.168.0.77" ? "HP" : null);
                },
                CancellationToken.None);
            Check(probedDuringBrowse, "installed printer probed during the mDNS wait");
            Check(r.Scanners.Select(s => s.Host).SequenceEqual(new[] { "192.168.0.48", "192.168.0.77" }), "installed first, then the neighbour");

            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                await ExpectCanceled(() => PrinterDiscovery.DiscoverAsync(canceled.Token), "already canceled");
            }
            using (var cts = new CancellationTokenSource(150))
                await ExpectCanceled(() => PrinterDiscovery.DiscoverAsync(
                    () => new List<ScannerCandidate>(),
                    async ct => { await Task.Delay(Timeout.Infinite, ct); return new MdnsBrowseResult(); },
                    (h, ct) => Task.FromResult<string>(null),
                    cts.Token), "canceled while browsing");
            using (var cts = new CancellationTokenSource(150))
                await ExpectCanceled(() => PrinterDiscovery.DiscoverAsync(
                    () => new List<ScannerCandidate> { new ScannerCandidate { Host = "192.168.0.48" } },
                    ct => Task.FromResult(new MdnsBrowseResult()),
                    async (h, ct) => { await Task.Delay(Timeout.Infinite, ct); return h; },
                    cts.Token), "canceled while probing");
        }

        [Test("mDNS browse against a fake responder")]
        static async Task FakeBrowse()
        {
            using (var scanner = new FakeCapabilitiesServer("Fake Scanner 9000"))
            using (var notScanner = new FakeCapabilitiesServer(null))
            using (var responder = new FakeMdnsResponder(query =>
            {
                var id = MdnsPacket.Parse(query)?.Id ?? (ushort)0;
                return new[]
                {
                    new byte[] { 1, 2, 3 },                                     // garbage
                    query,                                                      // a query, not a response
                    Answer(id, "_uscan._tcp", "Fake (Test)", scanner.Port, "fake-scanner.local", IPAddress.Loopback, "ty=Fake ty"),
                };
            }))
            {
                var loopback = new[] { IPAddress.Loopback };
                var result = await MdnsBrowser.BrowseAsync(loopback, responder.EndPoint, TimeSpan.FromMilliseconds(1450), CancellationToken.None);

                var queries = responder.Queries.ToList();
                Check(queries.Count == 3, $"query sent three times (got {queries.Count})");
                var first = MdnsPacket.Parse(queries[0].Bytes);
                Check(first != null && !first.IsResponse && first.Id != 0, "nonzero random ID");
                Check(queries.All(q => q.Bytes.SequenceEqual(MdnsPacket.BuildQuery(first.Id, MdnsBrowser.QueryNames))), "all are our two-question query");
                var times = queries.Select(q => q.AtMs - queries[0].AtMs).ToList();
                Check(times[1] >= 300 && times[1] < 800 && times[2] >= 1050 && times[2] < 1450, $"sent at 0, ~400 and ~1200 ms ({string.Join(", ", times)})");

                Check(result.Interfaces == 1 && result.FailedInterfaces == 0 && !result.Blocked, "sent on the one interface");
                Check(result.Services.Count == 1, $"one service from three identical answers (got {result.Services.Count})");
                var s = result.Services[0];
                Check(s.InstanceName == "Fake (Test)" && s.Address.Equals(IPAddress.Loopback) && s.Port == scanner.Port && s.Model == "Fake ty", "service details");

                // The whole search: the installed port answers 404, mDNS finds the scanner on the same device.
                var sw = Stopwatch.StartNew();
                var found = await PrinterDiscovery.DiscoverAsync(
                    () => new List<ScannerCandidate> { new ScannerCandidate { Host = notScanner.Host, QueueName = "Office" } },
                    ct => MdnsBrowser.BrowseAsync(loopback, responder.EndPoint, TimeSpan.FromMilliseconds(500), ct),
                    PrinterDiscovery.ProbeAsync,
                    CancellationToken.None);
                Check(found.Scanners.Count == 1, $"one scanner (got {found.Scanners.Count})");
                var f = found.Scanners[0];
                Check(f.Host == scanner.Host && f.Model == "Fake Scanner 9000", $"host and eSCL model ({f.Host}, {f.Model})");
                Check(f.QueueName == "Office" && f.ServiceName == "Fake (Test)", "queue of the same device, mDNS name");
                Check(sw.ElapsedMilliseconds < 3000, $"quick ({sw.ElapsedMilliseconds} ms)");

                using (var cts = new CancellationTokenSource(200))
                    await ExpectCanceled(() => MdnsBrowser.BrowseAsync(loopback, responder.EndPoint, TimeSpan.FromSeconds(10), cts.Token), "browse canceled");
            }
        }

        [Test("mDNS browse reports blocked sends")]
        static async Task BlockedBrowse()
        {
            // TEST-NET-1 is never on this machine, so the socket cannot even bind: same outcome as a refused send.
            var result = await MdnsBrowser.BrowseAsync(new[] { IPAddress.Parse("192.0.2.1") }, MdnsBrowser.MulticastEndPoint, TimeSpan.FromMilliseconds(300), CancellationToken.None);
            Check(result.Interfaces == 1 && result.FailedInterfaces == 1 && result.Blocked && result.Services.Count == 0, "blocked");

            result = await MdnsBrowser.BrowseAsync(new IPAddress[0], MdnsBrowser.MulticastEndPoint, TimeSpan.FromMilliseconds(300), CancellationToken.None);
            Check(result.Interfaces == 0 && !result.Blocked, "no interfaces is not 'blocked'");
        }

        [LiveTest("mDNS finds the scanner")]
        static async Task LiveMdns(string host)
        {
            var ip = PrinterDiscovery.InstalledHost(host);
            bool Heard(MdnsBrowseResult r) => r.Services.Any(s => !s.IsTls && s.Address.ToString() == ip);
            Console.WriteLine($"      interfaces: {string.Join(", ", MdnsBrowser.GetInterfaceAddresses())}");

            // A printer in power-save may not answer: the M2085FW ignores mDNS until a TCP connection wakes it.
            var cold = await MdnsBrowser.BrowseAsync(MdnsBrowser.DefaultListenTime, CancellationToken.None);
            Console.WriteLine($"      mDNS before anything else: {(Heard(cold) ? "answered" : "no answer (asleep?)")}, sends failed on {cold.FailedInterfaces} of {cold.Interfaces} interfaces");

            // The real search; an installed printer's probe wakes it, and the late third query can then hear it.
            var sw = Stopwatch.StartNew();
            var found = await PrinterDiscovery.FindScannersAsync(CancellationToken.None);
            Console.WriteLine($"      search took {sw.ElapsedMilliseconds} ms");
            foreach (var f in found)
                Console.WriteLine($"      found: {f.Host} \"{f.Model}\" queue={f.QueueName ?? "-"} mDNS={f.ServiceName ?? "-"}");
            Check(found.Any(f => f.Host == host), "FindScannersAsync returns " + host);

            // Awake now (read-only status request): mDNS must answer.
            using (var client = new EsclClient(host, TimeSpan.FromSeconds(10)))
                await client.GetStatusAsync(CancellationToken.None);
            var warm = await MdnsBrowser.BrowseAsync(MdnsBrowser.DefaultListenTime, CancellationToken.None);
            foreach (var s in warm.Services)
                Console.WriteLine($"      mDNS: {s.ServiceType} \"{s.InstanceName}\" -> {s.HostName} {s.Address}:{s.Port} ty={s.Model} rs={s.ResourcePath}");
            Check(Heard(warm), "mDNS answer from " + ip + " once awake");
        }

        static async Task ExpectCanceled(Func<Task> action, string what)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await action();
            }
            catch (OperationCanceledException)
            {
                Check(sw.ElapsedMilliseconds < 2000, $"{what}: promptly ({sw.ElapsedMilliseconds} ms)");
                return;
            }
            throw new Exception("expected OperationCanceledException: " + what);
        }

        /// <summary>What the M2085FW sends, with every kind of name compression it uses, SRV on 8080 and extra record types.</summary>
        static byte[] SamsungAnswer(ushort id)
        {
            var w = new DnsWriter().U16(id).U16(0x8400).U16(1).U16(1).U16(0).U16(5);
            var service = w.Position;                    // the echoed question: _uscan._tcp.local
            w.Label("_uscan").Label("_tcp");
            var local = w.Position;
            w.Label("local").U8(0).U16(MdnsRecord.TypePtr).U16(1);

            w.Pointer(service);
            var at = w.RecordHeader(MdnsRecord.TypePtr, 1, 4500);
            var instance = w.Position;
            w.Label(SamsungInstance).Pointer(service);
            w.EndRecord(at);

            w.Pointer(instance);
            at = w.RecordHeader(MdnsRecord.TypeSrv, 0x8001, 120);
            w.U16(0).U16(0).U16(8080);
            var target = w.Position;
            w.Label("SEC001122334455").Pointer(local);
            w.EndRecord(at);

            w.Pointer(instance);
            at = w.RecordHeader(MdnsRecord.TypeTxt, 0x8001, 4500);
            foreach (var s in new[] { "txtvers=1", "ty=M208x Series", "rs=eSCL", "UUID=16a65700-007c-1000-bb49-001122334455", "TY=ignored", "duplex", "", "note=a=b" })
                w.Label(s);
            w.EndRecord(at);

            w.Pointer(target);
            at = w.RecordHeader(28, 0x8001, 120);        // AAAA: not used
            w.U16(0xFE80).U16(0).U16(0).U16(0).U16(0).U16(0).U16(0).U16(1);
            w.EndRecord(at);

            w.Pointer(target);
            at = w.RecordHeader(MdnsRecord.TypeA, 0x8001, 120);
            w.U8(192).U8(168).U8(0).U8(48);
            w.EndRecord(at);

            w.Pointer(instance);
            at = w.RecordHeader(47, 0x8001, 120);        // NSEC: not used
            w.Pointer(instance).U8(0).U8(5).U8(0).U8(0).U8(0x80).U8(0).U8(0x40);
            w.EndRecord(at);
            return w.ToArray();
        }

        /// <summary>Samsung over plain and TLS eSCL, an HP without A record, a goodbye and an unrelated service.</summary>
        static List<MdnsResponse> SampleResponses()
        {
            var samsung = SamsungIp;
            return new List<MdnsResponse>
            {
                Response(SamsungAnswer(1), samsung),
                Response(Answer(1, "_uscans._tcp", SamsungInstance, 443, "SEC001122334455.local", null), samsung),
                Response(Answer(1, "_uscan._tcp", "NPI0A1B2C (HP LaserJet)", 80, "NPI0A1B2C.local", null, "ty=HP LaserJet MFP"), IPAddress.Parse("192.168.0.77")),
                Response(Answer(1, "_uscan._tcp", "Gone", 80, "gone.local", IPAddress.Parse("192.168.0.90"), ttl: 0), IPAddress.Parse("192.168.0.90")),
                Response(Answer(1, "_ipp._tcp", "Printer", 631, "printer.local", IPAddress.Parse("192.168.0.91")), IPAddress.Parse("192.168.0.91")),
            };
        }

        static MdnsResponse Response(byte[] packet, IPAddress source) =>
            new MdnsResponse { Message = MdnsPacket.Parse(packet), Source = source };

        /// <summary>A plain answer: PTR, then SRV, TXT and (when given) A, owner names compressed.</summary>
        static byte[] Answer(ushort id, string serviceType, string instance, int port, string target, IPAddress address, params string[] txt) =>
            Answer(id, serviceType, instance, port, target, address, 120, txt);

        static byte[] Answer(ushort id, string serviceType, string instance, int port, string target, IPAddress address, uint ttl, params string[] txt)
        {
            var w = new DnsWriter().U16(id).U16(0x8400).U16(0).U16(1).U16(0).U16(address == null ? 2 : 3);
            var service = w.Position;
            w.Name(serviceType + ".local");
            var at = w.RecordHeader(MdnsRecord.TypePtr, 1, ttl);
            var instanceAt = w.Position;
            w.Label(instance).Pointer(service);
            w.EndRecord(at);

            w.Pointer(instanceAt);
            at = w.RecordHeader(MdnsRecord.TypeSrv, 0x8001, ttl);
            w.U16(0).U16(0).U16(port);
            var targetAt = w.Position;
            w.Name(target);
            w.EndRecord(at);

            w.Pointer(instanceAt);
            at = w.RecordHeader(MdnsRecord.TypeTxt, 0x8001, ttl);
            foreach (var s in txt.Length == 0 ? new[] { "" } : txt) w.Label(s);
            w.EndRecord(at);

            if (address != null)
            {
                w.Pointer(targetAt);
                at = w.RecordHeader(MdnsRecord.TypeA, 0x8001, ttl);
                foreach (var b in address.GetAddressBytes()) w.U8(b);
                w.EndRecord(at);
            }
            return w.ToArray();
        }

        static byte[] Answer(ushort id, string serviceType, string instance, int port, string target, IPAddress address, uint ttl) =>
            Answer(id, serviceType, instance, port, target, address, ttl, new string[0]);

        /// <summary>Writes DNS wire format by hand, so tests control every pointer.</summary>
        sealed class DnsWriter
        {
            readonly List<byte> bytes = new List<byte>();
            public int Position => bytes.Count;
            public DnsWriter U8(int v) { bytes.Add((byte)v); return this; }
            public DnsWriter U16(int v) => U8(v >> 8).U8(v);
            public DnsWriter U32(uint v) => U16((int)(v >> 16)).U16((int)(v & 0xFFFF));
            public DnsWriter Label(string s)
            {
                var b = Encoding.UTF8.GetBytes(s);
                U8(b.Length);
                bytes.AddRange(b);
                return this;
            }
            public DnsWriter Name(string name)
            {
                foreach (var label in name.TrimEnd('.').Split('.')) Label(label);
                return U8(0);
            }
            public DnsWriter Pointer(int offset) => U16(0xC000 | offset);
            /// <summary>Type, class, TTL and a length to fill in with EndRecord; returns where the length goes.</summary>
            public int RecordHeader(int type, int cls, uint ttl)
            {
                U16(type).U16(cls).U32(ttl);
                var at = Position;
                U16(0);
                return at;
            }
            public void EndRecord(int at)
            {
                var length = Position - at - 2;
                bytes[at] = (byte)(length >> 8);
                bytes[at + 1] = (byte)length;
            }
            public byte[] ToArray() => bytes.ToArray();
        }

        /// <summary>Answers unicast like a real responder does to a legacy query; records what it got.</summary>
        sealed class FakeMdnsResponder : IDisposable
        {
            readonly UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            readonly CancellationTokenSource stop = new CancellationTokenSource();
            readonly Func<byte[], IEnumerable<byte[]>> reply;
            readonly Stopwatch clock = Stopwatch.StartNew();

            public ConcurrentQueue<(byte[] Bytes, long AtMs)> Queries { get; } = new ConcurrentQueue<(byte[], long)>();
            public IPEndPoint EndPoint => (IPEndPoint)udp.Client.LocalEndPoint;

            public FakeMdnsResponder(Func<byte[], IEnumerable<byte[]>> reply)
            {
                this.reply = reply;
                Task.Run(Loop);
            }

            async Task Loop()
            {
                while (!stop.IsCancellationRequested)
                {
                    UdpReceiveResult received;
                    try { received = await udp.ReceiveAsync(stop.Token); }
                    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                    catch { return; }
                    Queries.Enqueue((received.Buffer, clock.ElapsedMilliseconds));
                    foreach (var packet in reply(received.Buffer))
                    {
                        try { await udp.SendAsync(packet, received.RemoteEndPoint); }
                        catch (SocketException) { }
                    }
                }
            }

            public void Dispose()
            {
                stop.Cancel();
                udp.Dispose();
            }
        }

        /// <summary>Answers GET /eSCL/ScannerCapabilities with a model, or 404 to everything when the model is null.</summary>
        sealed class FakeCapabilitiesServer : IDisposable
        {
            readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            readonly string model;

            public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
            public string Host => "127.0.0.1:" + Port;

            public FakeCapabilitiesServer(string model)
            {
                this.model = model;
                listener.Start();
                Task.Run(AcceptLoop);
            }

            async Task AcceptLoop()
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch { return; }
                    _ = Task.Run(() => Handle(client));
                }
            }

            void Handle(TcpClient client)
            {
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var reader = new StreamReader(stream, Encoding.ASCII, false, 1, leaveOpen: true);
                        var requestLine = reader.ReadLine() ?? "";
                        while (!string.IsNullOrEmpty(reader.ReadLine())) { }
                        var ok = model != null && requestLine.StartsWith("GET /eSCL/ScannerCapabilities ");
                        var body = ok
                            ? Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?><scan:ScannerCapabilities xmlns:scan=\"http://schemas.hp.com/imaging/escl/2011/05/03\" xmlns:pwg=\"http://www.pwg.org/schemas/2010/12/sm\">" +
                                                     "<pwg:Version>2.63</pwg:Version><pwg:MakeAndModel>" + model + "</pwg:MakeAndModel></scan:ScannerCapabilities>")
                            : new byte[0];
                        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Length: {body.Length}\r\nContent-Type: text/xml\r\nConnection: close\r\n\r\n");
                        stream.Write(head, 0, head.Length);
                        stream.Write(body, 0, body.Length);
                        stream.Flush();
                    }
                }
                catch (Exception)
                {
                }
            }

            public void Dispose() => listener.Stop();
        }
    }
}
