using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pulse
{
    internal static class EngineTests
    {
        private static int passed;
        private static void Check(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }
        private static void Pass(string name) { passed++; Console.WriteLine("PASS " + name); }
        private static TestOptions Options(Uri target, int rate) { return new TestOptions { Target = target, RequestsPerSecond = rate, DurationSeconds = 5 }; }
        private static void Reject(TestOptions options)
        {
            bool rejected = false;
            try { options.Validate(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Invalid options were accepted");
        }

        private static int Main()
        {
            ServicePointManager.DefaultConnectionLimit = TestOptions.MaxConcurrency;
            try { Run().GetAwaiter().GetResult(); Console.WriteLine("ALL " + passed + " CHECKS PASSED"); return 0; }
            catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); return 1; }
        }

        private static async Task Run()
        {
            var engine = new LoadEngine();
            Reject(Options(new Uri("ftp://localhost/file"), 1));
            Reject(Options(new Uri("http://user:password@localhost/"), 1));
            Reject(Options(new Uri("http://localhost/"), 0));
            Reject(Options(new Uri("http://localhost/"), 101));
            var invalidDuration = Options(new Uri("http://localhost/"), 1);
            invalidDuration.DurationSeconds = 121; Reject(invalidDuration);
            invalidDuration.DurationSeconds = 4; Reject(invalidDuration);
            Pass("reject unsupported schemes, credentials and out-of-range settings");

            using (var server = new LocalServer())
            {
                RequestResult probe = await engine.ProbeAsync(server.Uri("/ok"), CancellationToken.None);
                Check(probe.Success && probe.StatusCode == 200 && probe.BytesRead == 2, "Probe must receive HTTP 200 and body: " + probe.StatusText + "; bytes=" + probe.BytesRead);
                Check(probe.Headers.Contains("X-Pulse-Test"), "Probe headers missing");
                Pass("GET probe includes status, timing, bounded body and headers");

                int before = server.Received;
                RequestResult redirect = await engine.ProbeAsync(server.Uri("/redirect"), CancellationToken.None);
                Check(redirect.StatusCode == 302 && !redirect.Success && server.Received == before + 1, "Redirect was followed");
                Pass("HTTP redirects are reported without following them");

                RequestResult large = await engine.ProbeAsync(server.Uri("/large"), CancellationToken.None);
                Check(large.Success && large.Truncated && large.BytesRead == TestOptions.MaxBodyBytes, "Body limit failed");
                Pass("response body capped at 64 KiB");

                int callbacks = 0;
                RunReport rateReport = await engine.RunAsync(Options(server.Uri("/ok"), 100), delegate { Interlocked.Increment(ref callbacks); }, CancellationToken.None);
                Check(rateReport.Results.Count >= 100 && rateReport.Results.Count <= 500, "Unexpected request count: " + rateReport.Results.Count);
                Check(rateReport.Results.All(r => r.Success || r.Cancelled), "Unexpected HTTP failure");
                Check(callbacks == rateReport.Results.Count, "Lost callback");
                Check(rateReport.ElapsedSeconds >= 4.8 && rateReport.ElapsedSeconds < 7, "Duration bound failed");
                for (int i = 1; i < rateReport.Results.Count; i++)
                    Check(rateReport.Results[i].OffsetSeconds - rateReport.Results[i - 1].OffsetSeconds >= 0.009, "Rate scheduling burst");
                Pass("100 RPS pacing cap, duration limit and exactly one result per request");

                RunReport capped = await engine.RunAsync(Options(server.Uri("/slow"), 100), null, CancellationToken.None);
                Check(capped.Skipped > 0, "Busy slots must be skipped");
                Check(capped.Results.Count <= 12, "Too many slow requests started");
                Check(server.MaxActive <= TestOptions.MaxConcurrency, "Concurrency bound exceeded: " + server.MaxActive);
                Pass("four-request concurrency cap and skipped slots under slow responses");

                // Allow the server-side delay of cancelled clients to finish before the next case.
                await Task.Delay(1200);
                foreach (string code in new[] { "429", "503" })
                {
                    RunReport overload = await engine.RunAsync(Options(server.Uri("/" + code), 20), null, CancellationToken.None);
                    Check(overload.StopReason.Contains(code) && overload.ElapsedSeconds < 2, "Overload did not stop immediately: " + code);
                    Check(overload.Results.Count <= 4, "Requests continued after overload");
                    Pass("automatic stop on HTTP " + code);
                }

                using (var stop = new CancellationTokenSource())
                {
                    stop.CancelAfter(350);
                    var timer = Stopwatch.StartNew();
                    RunReport cancelled = await engine.RunAsync(Options(server.Uri("/hang"), 10), null, stop.Token);
                    Check(timer.Elapsed.TotalSeconds < 2 && cancelled.StopReason.Contains("пользователем"), "Stop latency too high");
                    Check(cancelled.Results.Any(r => r.Cancelled), "In-flight cancellation not recorded");
                    Check(Statistics.From(cancelled.Results).Completed == 0, "Cancelled requests counted as failures/completions");
                    Pass("manual cancellation interrupts in-flight requests");
                }

                var timeoutTimer = Stopwatch.StartNew();
                RequestResult timeout = await engine.ProbeAsync(server.Uri("/hang"), CancellationToken.None);
                Check(timeout.Error != null && timeout.Error.Contains("Тайм-аут") && !timeout.Cancelled, "Timeout was not recorded");
                Check(timeoutTimer.Elapsed.TotalSeconds >= 4.7 && timeoutTimer.Elapsed.TotalSeconds < 7, "Timeout out of bounds");
                Pass("five-second request timeout");

                var bodyTimer = Stopwatch.StartNew();
                RequestResult stalledBody = await engine.ProbeAsync(server.Uri("/stall-body"), CancellationToken.None);
                Check(stalledBody.StatusCode == 200 && !stalledBody.Success && !String.IsNullOrEmpty(stalledBody.Error), "Stalled body incorrectly succeeded");
                Check(bodyTimer.Elapsed.TotalSeconds < 7, "Stalled body bypassed timeout");
                Pass("timeout also covers a stalled response body");
            }

            var unused = new TcpListener(IPAddress.Loopback, 0);
            unused.Start();
            int port = ((IPEndPoint)unused.LocalEndpoint).Port;
            unused.Stop();
            RunReport failures = await engine.RunAsync(Options(new Uri("http://127.0.0.1:" + port + "/"), 5), null, CancellationToken.None);
            Check(failures.StopReason.Contains("5 ошибок") && failures.Results.Count(r => !r.Cancelled) >= 5 &&
                failures.Results.Count <= 5 + TestOptions.MaxConcurrency - 1,
                "Connection failure circuit breaker failed: " + failures.StopReason + "; results=" + failures.Results.Count);
            Pass("automatic stop after five consecutive connection failures");

            var sample = Enumerable.Range(1, 20).Select(i => new RequestResult { Number = i, StatusCode = 200, LatencyMs = i }).ToList();
            sample.Add(new RequestResult { Number = 21, Error = "Network failure" });
            sample.Add(new RequestResult { Number = 22, Cancelled = true });
            Statistics stats = Statistics.From(sample);
            Check(stats.P95 == 19 && stats.Average == 10.5 && stats.Completed == 21 && stats.Failed == 1 && stats.Cancelled == 1, "Statistics incorrect");
            Pass("nearest-rank P95, mean, errors and cancellations");

            string exportPath = Path.Combine(Path.GetTempPath(), "pulse-test-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                sample[0].Error = "=1+1,\"quoted\"\r\nline";
                ReportWriter.WriteCsv(exportPath, new RunReport
                {
                    Target = "http://localhost/", StartedUtc = DateTime.UtcNow, Rate = 5, Duration = 5,
                    Results = sample, StopReason = "Тест завершён"
                });
                byte[] bytes = File.ReadAllBytes(exportPath);
                string csv = File.ReadAllText(exportPath, Encoding.UTF8);
                Check(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "UTF-8 BOM missing");
                Check(csv.Contains("\"'=1+1,\"\"quoted\"\"\r\nline\"") && csv.Contains("ttfb_ms"), "CSV quoting/formula escaping failed");
                Pass("CSV metadata, UTF-8 BOM, quoted text and formula escaping");
            }
            finally { File.Delete(exportPath); }
        }
    }

    internal sealed class LocalServer : IDisposable
    {
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private readonly List<TcpClient> clients = new List<TcpClient>();
        private readonly List<Task> workers = new List<Task>();
        private readonly Task accept;
        private int active;
        private int maxActive;
        private int received;
        private readonly int port;
        public int Received { get { return Volatile.Read(ref received); } }
        public int MaxActive { get { return Volatile.Read(ref maxActive); } }
        public LocalServer()
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            accept = Accept();
        }
        public Uri Uri(string path) { return new Uri("http://127.0.0.1:" + port + path); }
        private async Task Accept()
        {
            try
            {
                while (!shutdown.IsCancellationRequested)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    lock (clients) clients.Add(client);
                    Task work = Serve(client);
                    lock (workers) workers.Add(work);
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { if (!shutdown.IsCancellationRequested) throw; }
        }
        private async Task Serve(TcpClient client)
        {
            bool counted = false;
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    var input = new StringBuilder();
                    byte[] one = new byte[1];
                    while (input.Length < 8192 && !input.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(one, 0, 1, shutdown.Token).ConfigureAwait(false) == 0) return;
                        input.Append((char)one[0]);
                    }
                    Interlocked.Increment(ref received);
                    int current = Interlocked.Increment(ref active);
                    counted = true;
                    int previous;
                    do { previous = Volatile.Read(ref maxActive); if (current <= previous) break; }
                    while (Interlocked.CompareExchange(ref maxActive, current, previous) != previous);
                    string path = input.ToString().Split(' ')[1];
                    if (path == "/hang") { await Task.Delay(15000, shutdown.Token).ConfigureAwait(false); return; }
                    if (path == "/slow") await Task.Delay(2000, shutdown.Token).ConfigureAwait(false);
                    string status = path == "/429" ? "429 Too Many Requests" : path == "/503" ? "503 Service Unavailable" : path == "/redirect" ? "302 Found" : "200 OK";
                    byte[] body = Encoding.ASCII.GetBytes(path == "/large" ? new string('x', 100000) : "OK");
                    string headers = "HTTP/1.1 " + status + "\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) +
                        "\r\nX-Pulse-Test: yes\r\nConnection: close\r\n" + (path == "/redirect" ? "Location: " + Uri("/ok").AbsoluteUri + "\r\n" : "") + "\r\n";
                    byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
                    await stream.WriteAsync(headerBytes, 0, headerBytes.Length, shutdown.Token).ConfigureAwait(false);
                    if (path == "/stall-body") { await Task.Delay(15000, shutdown.Token).ConfigureAwait(false); return; }
                    await stream.WriteAsync(body, 0, body.Length, shutdown.Token).ConfigureAwait(false);
                }
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            catch (OperationCanceledException) { }
            finally { if (counted) Interlocked.Decrement(ref active); }
        }
        public void Dispose()
        {
            shutdown.Cancel();
            listener.Stop();
            accept.GetAwaiter().GetResult();
            lock (clients) foreach (TcpClient client in clients) client.Close();
            Task[] pending;
            lock (workers) pending = workers.ToArray();
            Task.WhenAll(pending).GetAwaiter().GetResult();
            shutdown.Dispose();
        }
    }
}
