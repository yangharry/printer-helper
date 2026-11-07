using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PrinterHelper.Tests
{
    /// <summary>
    /// Tiny HTTP server that behaves like an eSCL scanner: answers 503 a few times per page,
    /// then sends the pages, then 404. Records every request it gets.
    /// </summary>
    sealed class FakeEsclServer : IDisposable
    {
        readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        readonly Queue<byte[]> pages;
        readonly int busyAnswersPerPage;
        int busyLeft;
        readonly CancellationTokenSource stop = new CancellationTokenSource();

        public ConcurrentQueue<string> Requests { get; } = new ConcurrentQueue<string>();
        public string Host => "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        public string LastScanSettings { get; private set; }
        /// <summary>When set, NextDocument never finishes (keeps answering 503).</summary>
        public bool AlwaysBusy { get; set; }

        public FakeEsclServer(IEnumerable<byte[]> pages, int busyAnswersPerPage)
        {
            this.pages = new Queue<byte[]>(pages);
            this.busyAnswersPerPage = busyAnswersPerPage;
            busyLeft = busyAnswersPerPage;
            listener.Start();
            Task.Run(AcceptLoop);
        }

        async Task AcceptLoop()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(); }
                catch { return; }
                _ = Task.Run(() => Handle(client));
            }
        }

        void Handle(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var reader = new StreamReader(stream, Encoding.ASCII, false, 1, leaveOpen: true);
                var requestLine = reader.ReadLine();
                if (requestLine == null) return;
                var contentLength = 0;
                string line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        contentLength = int.Parse(line.Substring(15).Trim());
                var body = new char[contentLength];
                var read = 0;
                while (read < contentLength) read += reader.Read(body, read, contentLength - read);

                var parts = requestLine.Split(' ');
                var method = parts[0];
                var path = parts[1];
                Requests.Enqueue(method + " " + path);

                if (method == "POST" && path == "/eSCL/ScanJobs")
                {
                    LastScanSettings = new string(body);
                    // Absolute Location naming the printer by a .local host name, like real devices do.
                    Send(stream, 201, "Created", null, "Location: http://printer.local.:80/eSCL/ScanJobs/42\r\n");
                }
                else if (method == "GET" && path == "/eSCL/ScanJobs/42/NextDocument")
                {
                    lock (pages)
                    {
                        if (AlwaysBusy || (pages.Count > 0 && busyLeft > 0))
                        {
                            busyLeft--;
                            Send(stream, 503, "Service Unavailable", null, "Retry-After: 0\r\n");
                        }
                        else if (pages.Count > 0)
                        {
                            busyLeft = busyAnswersPerPage;
                            Send(stream, 200, "OK", pages.Dequeue(), "Content-Type: image/jpeg\r\n");
                        }
                        else
                        {
                            Send(stream, 404, "Not Found", null, null);
                        }
                    }
                }
                else if (method == "DELETE" && path == "/eSCL/ScanJobs/42")
                {
                    Send(stream, 200, "OK", null, null);
                }
                else if (method == "GET" && path == "/eSCL/ScannerStatus")
                {
                    var xml = "<?xml version=\"1.0\"?><scan:ScannerStatus xmlns:scan=\"http://schemas.hp.com/imaging/escl/2011/05/03\" xmlns:pwg=\"http://www.pwg.org/schemas/2010/12/sm\">" +
                              "<pwg:Version>2.63</pwg:Version><pwg:State>Idle</pwg:State><scan:AdfState>ScannerAdfLoaded</scan:AdfState>" +
                              "<scan:Jobs><scan:JobInfo><pwg:JobUri>/eSCL/ScanJobs/42</pwg:JobUri><pwg:JobState>Completed</pwg:JobState>" +
                              "<pwg:JobStateReasons><pwg:JobStateReason>JobCompletedSuccessfully</pwg:JobStateReason></pwg:JobStateReasons></scan:JobInfo></scan:Jobs></scan:ScannerStatus>";
                    Send(stream, 200, "OK", Encoding.UTF8.GetBytes(xml), "Content-Type: text/xml\r\n");
                }
                else
                {
                    Send(stream, 404, "Not Found", null, null);
                }
            }
        }

        static void Send(Stream stream, int code, string reason, byte[] body, string headers)
        {
            body = body ?? new byte[0];
            var head = $"HTTP/1.1 {code} {reason}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n{headers}\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            stream.Write(bytes, 0, bytes.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }
}
