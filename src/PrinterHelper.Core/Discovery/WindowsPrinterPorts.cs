using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrinterHelper.Discovery
{
    /// <summary>An installed print queue that prints through a TCP/IP port.</summary>
    public sealed class NetworkPrinter
    {
        public string QueueName { get; set; }
        /// <summary>The port's host address as typed when the printer was added: an IP address or a host name.</summary>
        public string Host { get; set; }
        public bool IsDefault { get; set; }
    }

    /// <summary>The printers already installed in Windows that point at a network address.</summary>
    [SupportedOSPlatform("windows")]
    public static class WindowsPrinterPorts
    {
        /// <summary>
        /// Queues on "Standard TCP/IP" ports with their host, the default printer first.
        /// Empty when WMI is unavailable (the user can still type the address).
        /// </summary>
        public static List<NetworkPrinter> GetNetworkPrinters()
        {
            var result = new List<NetworkPrinter>();
            try
            {
                var hostsByPort = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var ports = new ManagementObjectSearcher("SELECT Name, HostAddress FROM Win32_TCPIPPrinterPort"))
                using (var found = ports.Get())
                {
                    foreach (ManagementObject port in found)
                        using (port)
                        {
                            var name = port["Name"] as string;
                            var host = port["HostAddress"] as string;
                            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(host))
                                hostsByPort[name] = host.Trim();
                        }
                }
                using (var printers = new ManagementObjectSearcher("SELECT Name, PortName, Default FROM Win32_Printer"))
                using (var found = printers.Get())
                {
                    foreach (ManagementObject printer in found)
                        using (printer)
                        {
                            var portName = printer["PortName"] as string;
                            if (portName != null && hostsByPort.TryGetValue(portName, out var host))
                                result.Add(new NetworkPrinter
                                {
                                    QueueName = printer["Name"] as string,
                                    Host = host,
                                    IsDefault = printer["Default"] is bool isDefault && isDefault,
                                });
                        }
                }
            }
            catch (ManagementException)
            {
                // WMI unavailable or broken.
            }
            catch (COMException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            // The default printer is the likeliest to be the user's own; OrderBy is stable for the rest.
            return result.OrderBy(p => p.IsDefault ? 0 : 1).ToList();
        }
    }
}
