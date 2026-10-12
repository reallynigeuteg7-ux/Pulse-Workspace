using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pulse
{
    public sealed class TestOptions
    {
        public const int MaxRate = 100;
        public const int MaxDuration = 120;
        public const int MaxConcurrency = 4;
        public const int TimeoutSeconds = 5;
        public const int MaxBodyBytes = 65536;
        public Uri Target { get; set; }
        public int RequestsPerSecond { get; set; }
        public int DurationSeconds { get; set; }

        public void Validate()
        {
            if (Target == null || !Target.IsAbsoluteUri ||
                (Target.Scheme != Uri.UriSchemeHttp && Target.Scheme != Uri.UriSchemeHttps) ||
                String.IsNullOrEmpty(Target.Host) || !String.IsNullOrEmpty(Target.UserInfo))
                throw new ArgumentException("Введите HTTP(S)-адрес без логина и пароля, например http://localhost:8080/.");
            if (RequestsPerSecond < 1 || RequestsPerSecond > MaxRate)
                throw new ArgumentException("Частота должна быть от 1 до 100 запросов/с.");
            if (DurationSeconds < 5 || DurationSeconds > MaxDuration)
                throw new ArgumentException("Длительность должна быть от 5 до 120 секунд.");
        }
    }

    public sealed class RequestResult
    {
        public int Number { get; set; }
        public DateTime TimestampUtc { get; set; }
        public double OffsetSeconds { get; set; }
        public double LatencyMs { get; set; }
        public int StatusCode { get; set; }
        public int BytesRead { get; set; }
        public bool Truncated { get; set; }
        public bool Cancelled { get; set; }
        public string Error { get; set; }
        public string Headers { get; set; }
        public bool Success { get { return !Cancelled && String.IsNullOrEmpty(Error) && StatusCode >= 200 && StatusCode < 300; } }
        public string StatusText
        {
            get
            {
                if (Cancelled) return "Отменён";
                if (!String.IsNullOrEmpty(Error)) return Error;
                return "HTTP " + StatusCode.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    public sealed class RunReport
    {
        public string Target { get; set; }
        public DateTime StartedUtc { get; set; }
        public int Rate { get; set; }
        public int Duration { get; set; }
        public double ElapsedSeconds { get; set; }
        public int Skipped { get; set; }
        public string StopReason { get; set; }
        public List<RequestResult> Results { get; set; }
    }

    public sealed class Statistics
    {
        public int Completed { get; private set; }
        public int Failed { get; private set; }
        public int Cancelled { get; private set; }
        public double Average { get; private set; }
        public double P95 { get; private set; }
        public double ErrorPercent { get { return Completed == 0 ? 0 : 100.0 * Failed / Completed; } }
        public static Statistics From(IEnumerable<RequestResult> source)
        {
            RequestResult[] all = source.ToArray();
            RequestResult[] finished = all.Where(r => !r.Cancelled).ToArray();
            // Latency means time to response headers. Network errors have no TTFB.
            double[] timings = finished.Where(r => r.StatusCode > 0).Select(r => r.LatencyMs).OrderBy(x => x).ToArray();
            return new Statistics
            {
                Completed = finished.Length,
                Failed = finished.Count(r => !r.Success),
                Cancelled = all.Count(r => r.Cancelled),
                Average = timings.Length == 0 ? 0 : timings.Average(),
                P95 = timings.Length == 0 ? 0 : timings[(int)Math.Ceiling(timings.Length * 0.95) - 1]
            };
        }
    }

    public sealed class LoadEngine
    {
        private static HttpClient CreateClient()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseDefaultCredentials = false,
                // Direct requests give predictable timing and do not forward test traffic to a system proxy.
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.None,
                MaxResponseHeadersLength = 32
            };
            var client = new HttpClient(handler);
            client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Pulse-Server-Test/1.0");
            return client;
        }

        private static async Task<RequestResult> SendAsync(HttpClient client, Uri target, int number,
            double offset, CancellationToken token)
        {
            var result = new RequestResult { Number = number, TimestampUtc = DateTime.UtcNow, OffsetSeconds = offset };
            var timer = Stopwatch.StartNew();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(TestOptions.TimeoutSeconds));
                try
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, target))
                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token).ConfigureAwait(false))
                    {
                        result.LatencyMs = timer.Elapsed.TotalMilliseconds;
                        result.StatusCode = (int)response.StatusCode;
                        result.Headers = response.Headers.ToString() + response.Content.Headers.ToString();
                        // Back off as soon as overload is reported; do not consume its body.
                        if (result.StatusCode == 429 || result.StatusCode == 503) return result;
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (timeout.Token.Register(delegate { stream.Dispose(); }))
                        {
                            byte[] buffer = new byte[8192];
                            while (result.BytesRead < TestOptions.MaxBodyBytes)
                            {
                                int read = await stream.ReadAsync(buffer, 0,
                                    Math.Min(buffer.Length, TestOptions.MaxBodyBytes - result.BytesRead), timeout.Token).ConfigureAwait(false);
                                if (read == 0) break;
                                result.BytesRead += read;
                            }
                            result.Truncated = result.BytesRead == TestOptions.MaxBodyBytes &&
                                (!response.Content.Headers.ContentLength.HasValue || response.Content.Headers.ContentLength.Value > result.BytesRead);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) result.Cancelled = true;
                    else if (timeout.IsCancellationRequested || ex is OperationCanceledException) result.Error = "Тайм-аут 5 с";
                    else if (ex is HttpRequestException || ex is IOException || ex is WebException || ex is ObjectDisposedException)
                        result.Error = "Ошибка соединения: " + ex.GetBaseException().Message;
                    else throw;
                }
            }
            return result;
        }

        public async Task<RequestResult> ProbeAsync(Uri target, CancellationToken token)
        {
            new TestOptions { Target = target, RequestsPerSecond = 1, DurationSeconds = 5 }.Validate();
            using (var client = CreateClient()) return await SendAsync(client, target, 1, 0, token).ConfigureAwait(false);
        }

        public async Task<RunReport> RunAsync(TestOptions options, Action<RequestResult> onResult,
            CancellationToken cancellationToken)
        {
            options.Validate();
            var report = new RunReport
            {
                Target = options.Target.AbsoluteUri, StartedUtc = DateTime.UtcNow,
                Rate = options.RequestsPerSecond, Duration = options.DurationSeconds,
                Results = new List<RequestResult>(), StopReason = "Тест завершён"
            };
            var gate = new object();
            var timer = Stopwatch.StartNew();
            int active = 0, failures = 0, sequence = 0;
            using (var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var client = CreateClient())
            {
                stop.CancelAfter(TimeSpan.FromSeconds(options.DurationSeconds));
                var tasks = new List<Task>();
                double interval = 1000.0 / options.RequestsPerSecond;
                double next = 0;
                Exception schedulingError = null;
                try
                {
                    while (!stop.IsCancellationRequested && timer.Elapsed.TotalSeconds < options.DurationSeconds)
                    {
                        double remaining = next - timer.Elapsed.TotalMilliseconds;
                        if (remaining > 0)
                            await Task.Delay((int)Math.Ceiling(remaining), stop.Token).ConfigureAwait(false);
                        if (stop.IsCancellationRequested || timer.Elapsed.TotalSeconds >= options.DurationSeconds) break;
                        // Schedule from the actual dispatch time: missed slots never become a catch-up burst.
                        next = timer.Elapsed.TotalMilliseconds + interval;
                        if (Volatile.Read(ref active) >= TestOptions.MaxConcurrency)
                        {
                            report.Skipped++;
                            continue;
                        }
                        Interlocked.Increment(ref active);
                        int number = ++sequence;
                        double offset = timer.Elapsed.TotalSeconds;
                        Func<Task> runRequest = async delegate
                        {
                            try
                            {
                                RequestResult result = await SendAsync(client, options.Target, number, offset, stop.Token).ConfigureAwait(false);
                                result.Headers = null;
                                bool mustStop = false;
                                lock (gate)
                                {
                                    report.Results.Add(result);
                                    if (!result.Cancelled)
                                        failures = String.IsNullOrEmpty(result.Error) ? 0 : failures + 1;
                                    if (result.StatusCode == 429 || result.StatusCode == 503)
                                    {
                                        report.StopReason = "Автостоп: сервер ответил HTTP " + result.StatusCode;
                                        mustStop = true;
                                    }
                                    else if (failures >= 5)
                                    {
                                        report.StopReason = "Автостоп: 5 ошибок соединения подряд";
                                        mustStop = true;
                                    }
                                }
                                if (mustStop) stop.Cancel();
                                if (onResult != null) onResult(result);
                            }
                            finally { Interlocked.Decrement(ref active); }
                        };
                        tasks.Add(runRequest());
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { schedulingError = ex; }
                finally
                {
                    stop.Cancel();
                }
                await Task.WhenAll(tasks).ConfigureAwait(false);
                if (schedulingError != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(schedulingError).Throw();
            }
            report.ElapsedSeconds = timer.Elapsed.TotalSeconds;
            if (cancellationToken.IsCancellationRequested && report.StopReason == "Тест завершён")
                report.StopReason = "Остановлено пользователем";
            report.Results = report.Results.OrderBy(r => r.Number).ToList();
            return report;
        }
    }

    public static class ReportWriter
    {
        private static string Cell(string value)
        {
            value = value ?? "";
            // Treat all user/server-controlled text as text in spreadsheet applications.
            string trimmed = value.TrimStart();
            if (trimmed.Length > 0 && "=+-@\t\r".IndexOf(trimmed[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        public static void WriteCsv(string path, RunReport report)
        {
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine("target,started_utc,rate_per_second,planned_seconds,elapsed_seconds,skipped,stop_reason");
                writer.WriteLine(String.Join(",", Cell(report.Target), Cell(report.StartedUtc.ToString("o")),
                    report.Rate.ToString(CultureInfo.InvariantCulture), report.Duration.ToString(CultureInfo.InvariantCulture),
                    report.ElapsedSeconds.ToString("F3", CultureInfo.InvariantCulture), report.Skipped.ToString(CultureInfo.InvariantCulture), Cell(report.StopReason)));
                writer.WriteLine();
                writer.WriteLine("number,timestamp_utc,offset_seconds,http_status,ttfb_ms,bytes_read,body_truncated,cancelled,success,error");
                foreach (RequestResult r in report.Results)
                    writer.WriteLine(String.Join(",", r.Number.ToString(CultureInfo.InvariantCulture), Cell(r.TimestampUtc.ToString("o")),
                        r.OffsetSeconds.ToString("F3", CultureInfo.InvariantCulture), r.StatusCode.ToString(CultureInfo.InvariantCulture),
                        r.LatencyMs.ToString("F2", CultureInfo.InvariantCulture), r.BytesRead.ToString(CultureInfo.InvariantCulture),
                        r.Truncated.ToString(), r.Cancelled.ToString(), r.Success.ToString(), Cell(r.Error)));
            }
        }
    }
}
