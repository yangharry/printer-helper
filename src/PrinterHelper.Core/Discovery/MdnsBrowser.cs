using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PrinterHelper.Discovery
{
    /// <summary>A scanner service announced over mDNS.</summary>
    internal sealed class MdnsService
    {
        /// <summary>"_uscan._tcp" (eSCL over http) or "_uscans._tcp" (eSCL over https).</summary>
        public string ServiceType { get; set; }
        /// <summary>Instance name, e.g. "SEC001122334455 (M208x Series)".</summary>
        public string InstanceName { get; set; }
        /// <summary>SRV target, e.g. "SEC001122334455.local", or null when the responder sent no SRV.</summary>
        public string HostName { get; set; }
        public IPAddress Address { get; set; }
        public int Port { get; set; }
        public IReadOnlyDictionary<string, string> Txt { get; set; } = new Dictionary<string, string>();

        public bool IsTls => string.Equals(ServiceType, MdnsBrowser.UscansType, StringComparison.OrdinalIgnoreCase);
        /// <summary>TXT "ty": the model name the device shows people.</summary>
        public string Model => TxtValue("ty");
        /// <summary>TXT "rs": eSCL resource path, normally "eSCL" (EsclClient assumes /eSCL).</summary>
        public string ResourcePath => TxtValue("rs");
        public string Uuid => TxtValue("UUID");

        string TxtValue(string key) =>
            Txt.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
    }

    internal sealed class MdnsResponse
    {
        public MdnsMessage Message { get; set; }
        public IPAddress Source { get; set; }
    }

    internal sealed class MdnsBrowseResult
    {
        public List<MdnsService> Services { get; set; } = new List<MdnsService>();
        /// <summary>Interfaces the query was tried on.</summary>
        public int Interfaces { get; set; }
        /// <summary>Interfaces where not a single query could be sent.</summary>
        public int FailedInterfaces { get; set; }

        /// <summary>
        /// Every send failed. On macOS 15+ that is what a missing Local Network permission looks like
        /// (EHOSTUNREACH), so the app can ask the user to allow it and search again.
        /// </summary>
        public bool Blocked => Interfaces > 0 && FailedInterfaces == Interfaces;
    }

    /// <summary>
    /// Browses for eSCL scanners with mDNS legacy unicast queries: one socket per network interface on an
    /// ephemeral port, so responders answer us directly. We need neither port 5353 (the system's own mDNS
    /// service holds it) nor a multicast group, and Windows Firewall lets unicast replies to our own multicast
    /// query in without a rule for the app.
    /// </summary>
    internal static class MdnsBrowser
    {
        public const string UscanType = "_uscan._tcp";
        public const string UscansType = "_uscans._tcp";
        public static readonly string[] QueryNames = { UscanType + ".local", UscansType + ".local" };
        public static readonly IPEndPoint MulticastEndPoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        public static readonly TimeSpan DefaultListenTime = TimeSpan.FromSeconds(1.8);
        // When each query goes out. The second covers a lost packet (responders answer within ~120 ms). The third is
        // for printers that ignore mDNS in power-save until a TCP connection wakes them: the M2085FW does, and takes
        // ~0.6 s to wake. Discovery probes installed printers meanwhile, so by 1.2 s such a printer answers.
        static readonly TimeSpan[] SendTimes = { TimeSpan.Zero, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(1200) };

        // Anyone on the network can answer, so keep a flood of answers from costing more than a moment: up to 64
        // devices, each with as many packets as one honest device sends (an answer per query, split in two at most).
        const int MaxSources = 64;
        static readonly int MaxPacketsPerSource = 2 * SendTimes.Length;
        const int MaxRecords = 2000;
        const int MaxServices = 32;

        /// <summary>Asks every usable interface; returns what was heard within <paramref name="listen"/>.</summary>
        public static Task<MdnsBrowseResult> BrowseAsync(TimeSpan listen, CancellationToken ct) =>
            BrowseAsync(GetInterfaceAddresses(), MulticastEndPoint, listen, ct);

        /// <summary>
        /// Sends the query from each local address to <paramref name="target"/> (the mDNS group, or a test responder)
        /// and collects the answers. Throws only OperationCanceledException, when <paramref name="ct"/> is canceled.
        /// </summary>
        public static async Task<MdnsBrowseResult> BrowseAsync(IReadOnlyList<IPAddress> localAddresses, IPEndPoint target, TimeSpan listen, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var query = MdnsPacket.BuildQuery((ushort)Random.Shared.Next(1, 0x10000), QueryNames);
            var responses = new ConcurrentQueue<MdnsResponse>();
            int[] failed;
            using (var window = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                window.CancelAfter(listen);
                var queries = localAddresses.Select(a => QueryAsync(a, target, query, responses, window.Token)).ToArray();
                var sent = await Task.WhenAll(queries).ConfigureAwait(false);
                failed = sent.Select(s => s ? 0 : 1).ToArray();
            }
            ct.ThrowIfCancellationRequested();
            return new MdnsBrowseResult
            {
                Services = Resolve(responses.ToList(), ct),
                Interfaces = failed.Length,
                FailedInterfaces = failed.Sum(),
            };
        }

        /// <summary>True when at least one query went out on this interface.</summary>
        static async Task<bool> QueryAsync(IPAddress local, IPEndPoint target, byte[] query, ConcurrentQueue<MdnsResponse> responses, CancellationToken window)
        {
            UdpClient udp = null;
            try
            {
                udp = new UdpClient(new IPEndPoint(local, 0));
                if (IsMulticast(target.Address))
                {
                    // The address form of IP_MULTICAST_IF picks the interface on both Windows and macOS.
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                    // What mDNSResponder and Avahi send with; old responders may ignore anything else. Optional.
                    try { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255); }
                    catch (SocketException) { }
                }
            }
            catch (Exception)
            {
                // Address gone or unusable (SocketException mostly): this interface is out.
                udp?.Dispose();
                return false;
            }

            using (udp)
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(window))
            {
                var receiving = ReceiveAsync(udp, responses, stop.Token);
                var sent = false;
                var clock = Stopwatch.StartNew();
                foreach (var at in SendTimes)
                {
                    if (window.IsCancellationRequested) break;
                    try
                    {
                        var wait = at - clock.Elapsed;
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, window).ConfigureAwait(false);
                        await udp.SendAsync(query, target, window).ConfigureAwait(false);
                        sent = true;
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception)
                    {
                        // SocketException: macOS 15+ fails sends (EHOSTUNREACH) until the user allows Local Network
                        // access, and a network going away fails the same way. The later sends still get their turn.
                    }
                }
                if (!sent) stop.Cancel();
                await receiving.ConfigureAwait(false);
                return sent;
            }
        }

        static async Task ReceiveAsync(UdpClient udp, ConcurrentQueue<MdnsResponse> responses, CancellationToken token)
        {
            // Per socket, so per interface: a device answers once on each network it shares with us.
            var perSource = new Dictionary<IPAddress, int>();
            while (true)
            {
                UdpReceiveResult received;
                try
                {
                    received = await udp.ReceiveAsync(token).ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    continue; // Windows reports an ICMP "port unreachable" for an earlier send here; keep listening
                }
                catch (Exception)
                {
                    return; // the listening window ended (OperationCanceledException), or the socket failed
                }
                // Keep draining the socket, but stop keeping answers beyond the limits.
                var source = received.RemoteEndPoint.Address;
                if (!perSource.ContainsKey(source) && perSource.Count >= MaxSources) continue;
                perSource.TryGetValue(source, out var count);
                perSource[source] = ++count;
                if (count > MaxPacketsPerSource) continue;
                var message = MdnsPacket.Parse(received.Buffer);
                if (message != null && message.IsResponse)
                    responses.Enqueue(new MdnsResponse { Message = message, Source = received.RemoteEndPoint.Address });
            }
        }

        /// <summary>
        /// Turns the records heard into services, in the order their PTR (or SRV) records arrived.
        /// Records are pooled across packets, since a responder may split its answer.
        /// </summary>
        public static List<MdnsService> Resolve(IReadOnlyList<MdnsResponse> responses, CancellationToken ct = default)
        {
            var records = new List<(MdnsRecord Record, IPAddress Source)>();
            foreach (var response in responses)
                foreach (var record in response.Message.Records)
                    if (record.Ttl > 0 && records.Count < MaxRecords) // TTL 0 is a "goodbye": the service is going away
                        records.Add((record, response.Source));

            // Look-ups by name, the first record of each kind winning, so resolving stays linear.
            var srvByName = new Dictionary<string, (MdnsRecord Record, IPAddress Source)>(StringComparer.OrdinalIgnoreCase);
            var txtByName = new Dictionary<string, (MdnsRecord Record, IPAddress Source)>(StringComparer.OrdinalIgnoreCase);
            var addressesByName = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in records)
            {
                var r = entry.Record;
                if (r.Type == MdnsRecord.TypeSrv) srvByName.TryAdd(r.Name, entry);
                else if (r.Type == MdnsRecord.TypeTxt) txtByName.TryAdd(r.Name, entry);
                else if (r.Type == MdnsRecord.TypeA && r.Address != null)
                {
                    if (!addressesByName.TryGetValue(r.Name, out var list)) addressesByName[r.Name] = list = new List<IPAddress>();
                    list.Add(r.Address);
                }
            }

            var services = new List<MdnsService>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (record, source) in records)
            {
                ct.ThrowIfCancellationRequested();
                if (services.Count >= MaxServices) break;
                string type, instance;
                if (record.Type == MdnsRecord.TypePtr && ServiceTypeOf(record.Name) is string ptrType &&
                    record.Target.EndsWith("." + record.Name, StringComparison.OrdinalIgnoreCase))
                {
                    type = ptrType;
                    instance = record.Target;
                }
                else if (record.Type == MdnsRecord.TypeSrv && InstanceServiceType(record.Name) is string srvType)
                {
                    type = srvType;
                    instance = record.Name;
                }
                else
                {
                    continue;
                }
                if (!seen.Add(instance)) continue;

                srvByName.TryGetValue(instance, out var srv);
                txtByName.TryGetValue(instance, out var txt);
                var from = srv.Record != null ? srv.Source : source;
                IPAddress address = null;
                if (srv.Record != null && srv.Record.Target != null && addressesByName.TryGetValue(srv.Record.Target, out var addresses))
                {
                    // A device with several addresses: the one it answered from is the one we can reach.
                    address = addresses.FirstOrDefault(a => a.Equals(from)) ?? addresses.FirstOrDefault();
                }
                var tls = string.Equals(type, UscansType, StringComparison.OrdinalIgnoreCase);
                services.Add(new MdnsService
                {
                    ServiceType = type,
                    InstanceName = instance.Substring(0, instance.Length - type.Length - ".local".Length - 1),
                    HostName = srv.Record?.Target,
                    Address = address ?? from,
                    Port = srv.Record != null && srv.Record.Port > 0 ? srv.Record.Port : tls ? 443 : 80,
                    Txt = txt.Record?.Txt ?? new Dictionary<string, string>(),
                });
            }
            return services;
        }

        /// <summary>"_uscan._tcp" for "_uscan._tcp.local", null for names we did not ask about.</summary>
        static string ServiceTypeOf(string name)
        {
            foreach (var q in QueryNames)
                if (SameName(name, q)) return q.Substring(0, q.Length - ".local".Length);
            return null;
        }

        /// <summary>The service type when the name is "instance._uscan._tcp.local" (or _uscans), otherwise null.</summary>
        static string InstanceServiceType(string name)
        {
            foreach (var q in QueryNames)
                if (name.Length > q.Length + 1 && name.EndsWith("." + q, StringComparison.OrdinalIgnoreCase))
                    return q.Substring(0, q.Length - ".local".Length);
            return null;
        }

        static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static bool IsMulticast(IPAddress address) =>
            address.AddressFamily == AddressFamily.InterNetwork && (address.GetAddressBytes()[0] & 0xF0) == 0xE0;

        /// <summary>
        /// IPv4 addresses of interfaces that are up and can multicast, except loopback, tunnels and dial-up links:
        /// a scanner is never behind a VPN tunnel. Public addresses count too: many school and office LANs use them.
        /// </summary>
        public static List<IPAddress> GetInterfaceAddresses()
        {
            var result = new List<IPAddress>();
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException)
            {
                return result;
            }
            foreach (var ni in interfaces)
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Ppp || !IsUsableInterfaceName(ni.Name))
                        continue;
                    foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
                    {
                        var address = unicast.Address;
                        if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) &&
                            !address.Equals(IPAddress.Any) && !result.Contains(address))
                            result.Add(address);
                    }
                }
                catch (NetworkInformationException)
                {
                    // An interface that vanished while we looked: skip it.
                }
            }
            return result;
        }

        /// <summary>
        /// False for macOS tunnels and peer-to-peer links (utun = VPN, awdl/llw = AirDrop Wi-Fi,
        /// bridge = Thunderbolt bridge / Internet sharing): no scanner lives there.
        /// </summary>
        public static bool IsUsableInterfaceName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            foreach (var prefix in new[] { "utun", "awdl", "llw", "bridge" })
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>RFC 1918 private (10/8, 172.16/12, 192.168/16) or link-local (169.254/16).</summary>
        public static bool IsPrivateOrLinkLocal(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = address.GetAddressBytes();
            return b[0] == 10 ||
                   b[0] == 172 && (b[1] & 0xF0) == 16 ||
                   b[0] == 192 && b[1] == 168 ||
                   b[0] == 169 && b[1] == 254;
        }
    }
}
