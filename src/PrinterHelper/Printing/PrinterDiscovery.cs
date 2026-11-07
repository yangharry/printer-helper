using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;
using PrinterHelper.Escl;

namespace PrinterHelper.Printing
{
    public sealed class WindowsPrinter
    {
        public string QueueName { get; set; }
        public string Host { get; set; }
    }

    public sealed class FoundScanner
    {
        public string Host { get; set; }
        public string Model { get; set; }
        public string QueueName { get; set; }
    }

    /// <summary>
    /// Finds network scanners through the printers already installed in Windows:
    /// every TCP/IP printer port is asked whether it speaks eSCL.
    /// </summary>
    public static class PrinterDiscovery
    {
        public static List<WindowsPrinter> GetNetworkPrinters()
        {
            var result = new List<WindowsPrinter>();
            try
            {
                var hostsByPort = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var ports = new ManagementObjectSearcher("SELECT Name, HostAddress FROM Win32_TCPIPPrinterPort"))
                {
                    foreach (ManagementObject port in ports.Get())
                    {
                        var name = port["Name"] as string;
                        var host = port["HostAddress"] as string;
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(host))
                            hostsByPort[name] = host.Trim();
                    }
                }
                using (var printers = new ManagementObjectSearcher("SELECT Name, PortName FROM Win32_Printer"))
                {
                    foreach (ManagementObject printer in printers.Get())
                    {
                        var portName = printer["PortName"] as string;
                        if (portName != null && hostsByPort.TryGetValue(portName, out var host))
                            result.Add(new WindowsPrinter { QueueName = printer["Name"] as string, Host = host });
                    }
                }
            }
            catch (ManagementException)
            {
                // WMI unavailable: the user can still type the address.
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }
            return result;
        }

        public static string FindQueueForHost(string host) =>
            GetNetworkPrinters().FirstOrDefault(p => string.Equals(p.Host, host, StringComparison.OrdinalIgnoreCase))?.QueueName;

        public static async Task<List<FoundScanner>> FindScannersAsync(CancellationToken ct)
        {
            var printers = await Task.Run(() => GetNetworkPrinters(), ct).ConfigureAwait(false);
            var probes = printers
                .GroupBy(p => p.Host, StringComparer.OrdinalIgnoreCase)
                .Select(async g =>
                {
                    var model = await ProbeAsync(g.Key, ct).ConfigureAwait(false);
                    return model == null ? null : new FoundScanner { Host = g.Key, Model = model, QueueName = g.First().QueueName };
                });
            return (await Task.WhenAll(probes).ConfigureAwait(false)).Where(s => s != null).ToList();
        }

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
    }
}
