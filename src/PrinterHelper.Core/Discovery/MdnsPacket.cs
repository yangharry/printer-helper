using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace PrinterHelper.Discovery
{
    /// <summary>One resource record from an mDNS response; only the parts discovery needs.</summary>
    internal sealed class MdnsRecord
    {
        public const int TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;

        /// <summary>Owner name, labels joined with "." and no trailing dot.</summary>
        public string Name { get; set; }
        public int Type { get; set; }
        public uint Ttl { get; set; }

        /// <summary>PTR: the instance name. SRV: the target host name.</summary>
        public string Target { get; set; }
        public int Port { get; set; }
        public IPAddress Address { get; set; }
        /// <summary>TXT key/value pairs; keys are case-insensitive and the first one wins (RFC 6763 6.4).</summary>
        public Dictionary<string, string> Txt { get; set; }
    }

    internal sealed class MdnsMessage
    {
        public ushort Id { get; set; }
        public bool IsResponse { get; set; }
        /// <summary>Answers, authority and additional records together, in packet order.</summary>
        public List<MdnsRecord> Records { get; } = new List<MdnsRecord>();
    }

    /// <summary>Builds mDNS queries and reads responses (RFC 1035 wire format, RFC 6762).</summary>
    internal static class MdnsPacket
    {
        const int ClassIn = 1;
        // A name is at most 255 bytes, so a well-formed one needs only a few pointers; more means a loop.
        const int MaxPointerJumps = 16;

        /// <summary>
        /// A query asking PTR (class IN) for each name. Sent from a port other than 5353 it is a
        /// "legacy unicast" query, so responders answer straight back to that port (RFC 6762 6.7).
        /// </summary>
        public static byte[] BuildQuery(ushort id, IReadOnlyList<string> names)
        {
            var ms = new MemoryStream();
            void Short(int v) { ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }
            Short(id);
            Short(0);              // standard query, no flags
            Short(names.Count);    // QDCOUNT
            Short(0); Short(0); Short(0);
            foreach (var name in names)
            {
                foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
                {
                    var bytes = Encoding.UTF8.GetBytes(label);
                    ms.WriteByte((byte)bytes.Length);
                    ms.Write(bytes, 0, bytes.Length);
                }
                ms.WriteByte(0);
                Short(MdnsRecord.TypePtr);
                Short(ClassIn);        // top bit clear: no unicast-response (QU) bit, legacy queriers don't set it
            }
            return ms.ToArray();
        }

        /// <summary>The message, or null when the packet is malformed or cut short. Never throws.</summary>
        public static MdnsMessage Parse(byte[] data) => data == null ? null : Parse(data, data.Length);

        public static MdnsMessage Parse(byte[] data, int length)
        {
            try
            {
                return ParseUnchecked(data, Math.Min(length, data.Length));
            }
            catch (Exception)
            {
                return null; // belt and braces: a packet from the network must never take discovery down
            }
        }

        /// <summary>Parse without the safety net; tests call it to show malformed input is handled, not caught.</summary>
        internal static MdnsMessage ParseUnchecked(byte[] data, int length)
        {
            if (length < 12) return null;
            var message = new MdnsMessage
            {
                Id = U16(data, 0),
                IsResponse = (data[2] & 0x80) != 0,
            };
            int questions = U16(data, 4);
            int records = U16(data, 6) + U16(data, 8) + U16(data, 10);
            var pos = 12;

            // Responses to legacy unicast queries repeat the question; skip it.
            for (var i = 0; i < questions; i++)
            {
                if (!TryReadName(data, length, ref pos, out _)) return null;
                pos += 4;
                if (pos > length) return null;
            }

            for (var i = 0; i < records; i++)
            {
                if (!TryReadName(data, length, ref pos, out var name)) return null;
                if (pos + 10 > length) return null;
                int type = U16(data, pos);
                int cls = U16(data, pos + 2) & 0x7FFF; // top bit is the cache-flush flag
                var ttl = (uint)(data[pos + 4] << 24 | data[pos + 5] << 16 | data[pos + 6] << 8 | data[pos + 7]);
                int rdLength = U16(data, pos + 8);
                var rdStart = pos + 10;
                var rdEnd = rdStart + rdLength;
                if (rdEnd > length) return null;
                pos = rdEnd;

                if (cls != ClassIn) continue;
                var record = ReadRecord(data, length, rdStart, rdEnd, name, type);
                if (record == null) continue; // a type we don't use, or bad record data: skip just this record
                record.Ttl = ttl;
                message.Records.Add(record);
            }
            return message;
        }

        static MdnsRecord ReadRecord(byte[] data, int length, int rdStart, int rdEnd, string name, int type)
        {
            var record = new MdnsRecord { Name = name, Type = type };
            var rdLength = rdEnd - rdStart;
            switch (type)
            {
                case MdnsRecord.TypeA:
                    if (rdLength != 4) return null;
                    record.Address = new IPAddress(new ReadOnlySpan<byte>(data, rdStart, 4));
                    return record;

                case MdnsRecord.TypePtr:
                {
                    // Names inside record data may point anywhere earlier in the packet, but must start inside the record.
                    var p = rdStart;
                    if (!TryReadName(data, rdEnd, length, ref p, out var target)) return null;
                    record.Target = target;
                    return record;
                }

                case MdnsRecord.TypeSrv:
                {
                    if (rdLength < 7) return null;
                    record.Port = U16(data, rdStart + 4); // after priority and weight
                    var p = rdStart + 6;
                    if (!TryReadName(data, rdEnd, length, ref p, out var target)) return null;
                    record.Target = target;
                    return record;
                }

                case MdnsRecord.TypeTxt:
                {
                    record.Txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var p = rdStart;
                    while (p < rdEnd)
                    {
                        int len = data[p++];
                        if (p + len > rdEnd) return null;
                        var entry = Encoding.UTF8.GetString(data, p, len);
                        p += len;
                        if (entry.Length == 0) continue;
                        var eq = entry.IndexOf('=');
                        var key = eq < 0 ? entry : entry.Substring(0, eq);
                        if (key.Length > 0 && !record.Txt.ContainsKey(key))
                            record.Txt[key] = eq < 0 ? "" : entry.Substring(eq + 1);
                    }
                    return record;
                }

                default:
                    return null;
            }
        }

        static bool TryReadName(byte[] data, int length, ref int offset, out string name) =>
            TryReadName(data, length, length, ref offset, out name);

        /// <summary>
        /// Reads a possibly compressed name at offset. Its first bytes must lie before <paramref name="limit"/>
        /// (the end of the record data); pointers may lead anywhere before <paramref name="length"/>.
        /// </summary>
        static bool TryReadName(byte[] data, int limit, int length, ref int offset, out string name)
        {
            name = null;
            var labels = new List<string>();
            var pos = offset;
            var end = -1;          // where the name ends in the original place, once a pointer was followed
            var jumps = 0;
            var octets = 1;        // the final zero byte
            var bound = limit;
            while (true)
            {
                if (pos >= bound) return false;
                int len = data[pos];
                if (len == 0)
                {
                    pos++;
                    break;
                }
                if ((len & 0xC0) == 0xC0)
                {
                    if (pos + 1 >= bound) return false;
                    var target = (len & 0x3F) << 8 | data[pos + 1];
                    if (end < 0) end = pos + 2;
                    if (++jumps > MaxPointerJumps) return false;
                    pos = target;
                    bound = length;
                    continue;
                }
                if ((len & 0xC0) != 0) return false; // 0x40 / 0x80 label types are not in use
                if (pos + 1 + len > bound) return false;
                octets += len + 1;
                if (octets > 255) return false;
                labels.Add(Encoding.UTF8.GetString(data, pos + 1, len));
                pos += 1 + len;
            }
            offset = end >= 0 ? end : pos;
            name = string.Join(".", labels);
            return true;
        }

        static ushort U16(byte[] data, int pos) => (ushort)(data[pos] << 8 | data[pos + 1]);
    }
}
