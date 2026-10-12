using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Pulse
{
    public sealed class Dashboard : Window
    {
        private readonly UserControl root;
        private readonly LoadEngine engine = new LoadEngine();
        private readonly ConcurrentQueue<RequestResult> incoming = new ConcurrentQueue<RequestResult>();
        private readonly List<RequestResult> results = new List<RequestResult>();
        private readonly List<RunReport> history = new List<RunReport>();
        private readonly Stopwatch elapsed = new Stopwatch();
        private readonly DispatcherTimer ticker;
        private readonly LatencyChart chart;
        private CancellationTokenSource cancellation;
        private RunReport selectedReport;
        private bool closing;
        private bool loadRunning;
        private int plannedDuration = 30;

        public Dashboard()
        {
            Title = "Pulse — тестирование сервера";
            Width = 1240;
            Height = 900;
            MinWidth = 1060;
            MinHeight = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            Background = Brush("#0D0D0D");
            ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0, ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(0)
            });
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Pulse.Dashboard.xaml"))
                root = (UserControl)XamlReader.Load(stream);
            root.FontFamily = new FontFamily(new Uri(AppDomain.CurrentDomain.BaseDirectory), "./assets/fonts/#Montserrat");
            Content = root;
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(root, TextRenderingMode.ClearType);
            chart = new LatencyChart(root.FontFamily);
            Get<Grid>("ChartHost").Children.Add(chart);

            Get<Button>("StartButton").Click += async delegate { await StartTest(); };
            Get<Button>("ProbeButton").Click += async delegate { await Probe(); };
            Get<Button>("StopButton").Click += delegate { Stop(); };
            Get<Button>("ExportButton").Click += delegate { Export(selectedReport); };
            Get<Button>("TestNav").Click += delegate { Navigate(0); };
            Get<Button>("HistoryNav").Click += delegate { Navigate(1); };
            Get<Button>("HelpNav").Click += delegate { Navigate(2); };
            Get<Button>("MinimizeButton").Click += delegate { WindowState = WindowState.Minimized; };
            Get<Button>("MaximizeButton").Click += delegate { ToggleMaximize(); };
            Get<Button>("CloseButton").Click += delegate { Close(); };
            Get<Border>("TitleBar").MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.ClickCount == 2) ToggleMaximize();
                else if (e.ButtonState == MouseButtonState.Pressed) DragMove();
            };
            Get<TextBox>("RateInput").TextChanged += delegate { UpdatePlan(); };
            Get<TextBox>("DurationInput").TextChanged += delegate { UpdatePlan(); };
            Get<Grid>("ProgressTrack").SizeChanged += delegate { UpdateProgress(); };
            PreviewKeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) { Stop(); e.Handled = true; }
                if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control && cancellation == null)
                { e.Handled = true; await StartTest(); }
            };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (cancellation != null)
                {
                    closing = true;
                    e.Cancel = true;
                    Stop();
                }
                else ticker.Stop();
            };
            ticker = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            ticker.Tick += delegate { if (loadRunning) { Drain(); UpdateMetrics(); UpdateProgress(); } };
            ticker.Start();
        }

        private T Get<T>(string name) where T : FrameworkElement { return (T)root.FindName(name); }
        private void Text(string name, string value) { Get<TextBlock>(name).Text = value; }
        private static SolidColorBrush Brush(string value) { return (SolidColorBrush)new BrushConverter().ConvertFromString(value); }
        private void ToggleMaximize() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }

        private void Navigate(int page)
        {
            string[] pages = { "TestPage", "HistoryPage", "HelpPage" };
            string[] buttons = { "TestNav", "HistoryNav", "HelpNav" };
            string[] titles = { "Нагрузочный тест", "История сессии", "Как это работает" };
            string[] subtitles = { "Измеряйте отклик. Наблюдайте за стабильностью.", "Сравнивайте результаты и сохраняйте отчёты.", "Короткие HTTP-тесты с понятными метриками." };
            for (int i = 0; i < pages.Length; i++)
            {
                Get<StackPanel>(pages[i]).Visibility = i == page ? Visibility.Visible : Visibility.Collapsed;
                Get<Button>(buttons[i]).Background = Brush(i == page ? "#F0F0F0" : "#00000000");
                Get<Button>(buttons[i]).Foreground = Brush(i == page ? "#111111" : "#AAAAAA");
            }
            Text("PageTitle", titles[page]);
            Text("PageSubtitle", subtitles[page]);
        }

        private void UpdatePlan()
        {
            int rate, duration;
            bool valid = Int32.TryParse(Get<TextBox>("RateInput").Text, out rate) && rate >= 1 && rate <= TestOptions.MaxRate;
            valid = Int32.TryParse(Get<TextBox>("DurationInput").Text, out duration) && duration >= 5 && duration <= TestOptions.MaxDuration && valid;
            Text("PlannedLabel", valid ? "До " + (rate * duration) + " запросов за тест" : "1–100 запросов/с · 5–120 с");
            if (valid && cancellation == null && selectedReport == null)
            {
                plannedDuration = duration;
                chart.Duration = duration;
                chart.InvalidateVisual();
                UpdateProgress();
            }
        }

        private Uri ReadTarget()
        {
            Uri target;
            if (!Uri.TryCreate(Get<TextBox>("TargetInput").Text.Trim(), UriKind.Absolute, out target))
                throw new ArgumentException("Введите полный адрес, например http://localhost:8080/.");
            new TestOptions { Target = target, RequestsPerSecond = 1, DurationSeconds = 5 }.Validate();
            return target;
        }

        private TestOptions ReadOptions()
        {
            int rate, duration;
            if (!Int32.TryParse(Get<TextBox>("RateInput").Text.Trim(), out rate) ||
                !Int32.TryParse(Get<TextBox>("DurationInput").Text.Trim(), out duration))
                throw new ArgumentException("Частота и длительность должны быть целыми числами.");
            var options = new TestOptions { Target = ReadTarget(), RequestsPerSecond = rate, DurationSeconds = duration };
            options.Validate();
            return options;
        }

        private void Notice(string message)
        {
            Get<Border>("NoticeBox").Visibility = String.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            Text("NoticeText", message ?? "");
        }

        private void SetBusy(bool busy, string status)
        {
            foreach (string name in new[] { "StartButton", "ProbeButton", "TargetInput", "RateInput", "DurationInput" })
                Get<FrameworkElement>(name).IsEnabled = !busy;
            Get<Button>("StopButton").IsEnabled = busy;
            Get<Button>("ExportButton").IsEnabled = !busy && selectedReport != null;
            Text("StatusBadge", status);
            if (busy) Text("FooterText", loadRunning ? "Тест выполняется · Esc — остановить" : "Проверка сервера · Esc — остановить");
            UpdateHistory();
        }

        private async Task StartTest()
        {
            if (cancellation != null) return;
            Navigate(0);
            TestOptions options;
            try { options = ReadOptions(); }
            catch (ArgumentException ex) { Notice(ex.Message); return; }
            Notice(null);
            Get<Border>("ProbeDetailsBox").Visibility = Visibility.Collapsed;
            selectedReport = null;
            results.Clear();
            RequestResult discarded;
            while (incoming.TryDequeue(out discarded)) { }
            plannedDuration = options.DurationSeconds;
            chart.Duration = plannedDuration;
            cancellation = new CancellationTokenSource();
            loadRunning = true;
            elapsed.Restart();
            SetBusy(true, "Тест идёт");
            UpdateMetrics();
            try
            {
                RunReport report = await engine.RunAsync(options, delegate(RequestResult r) { incoming.Enqueue(r); }, cancellation.Token);
                Drain();
                selectedReport = report;
                results.Clear();
                results.AddRange(report.Results);
                history.Insert(0, report);
                if (history.Count > 20) history.RemoveAt(history.Count - 1);
                UpdateHistory();
                Notice(report.StopReason + ". Завершено: " + Statistics.From(results).Completed +
                    "; отменено: " + Statistics.From(results).Cancelled + "; пропущено: " + report.Skipped + ".");
                Text("FooterText", report.StopReason);
            }
            catch (Exception ex)
            {
                Notice("Не удалось завершить тест: " + ex.Message);
                Text("FooterText", "Ошибка теста");
            }
            finally
            {
                elapsed.Stop();
                loadRunning = false;
                cancellation.Dispose();
                cancellation = null;
                SetBusy(false, selectedReport == null ? "Ошибка" : "Завершено");
                UpdateMetrics();
                UpdateProgress();
                if (closing) Close();
            }
        }

        private async Task Probe()
        {
            if (cancellation != null) return;
            Uri target;
            try { target = ReadTarget(); }
            catch (ArgumentException ex) { Notice(ex.Message); return; }
            cancellation = new CancellationTokenSource();
            Notice("Проверяем доступность сервера…");
            Get<Border>("ProbeDetailsBox").Visibility = Visibility.Collapsed;
            SetBusy(true, "Проверка");
            try
            {
                RequestResult result = await engine.ProbeAsync(target, cancellation.Token);
                Notice(result.StatusText + (result.StatusCode > 0 ? " · " + result.LatencyMs.ToString("F0") + " мс до заголовков" : "") +
                    (result.Truncated ? " · тело ограничено 64 КиБ" : ""));
                if (!String.IsNullOrWhiteSpace(result.Headers))
                {
                    Get<TextBox>("ProbeDetailsText").Text = result.Headers;
                    Get<Border>("ProbeDetailsBox").Visibility = Visibility.Visible;
                }
                Text("FooterText", result.Cancelled ? "Проверка остановлена" : "Проверка выполнена");
            }
            catch (Exception ex) { Notice("Ошибка проверки: " + ex.Message); }
            finally
            {
                cancellation.Dispose();
                cancellation = null;
                SetBusy(false, "Ожидание");
                if (closing) Close();
            }
        }

        private void Stop()
        {
            if (cancellation == null) return;
            cancellation.Cancel();
            Get<Button>("StopButton").IsEnabled = false;
            Text("StatusBadge", "Остановка…");
            Text("FooterText", "Отменяем текущие запросы…");
        }

        private void Drain()
        {
            RequestResult result;
            while (incoming.TryDequeue(out result)) results.Add(result);
        }

        private void UpdateMetrics()
        {
            Statistics stats = Statistics.From(results);
            Text("RequestsValue", stats.Completed.ToString("N0"));
            bool hasTimings = results.Any(r => !r.Cancelled && r.StatusCode > 0);
            Text("AverageValue", hasTimings ? stats.Average.ToString("F0") : "—");
            Text("P95Value", hasTimings ? stats.P95.ToString("F0") : "—");
            Text("ErrorsValue", stats.ErrorPercent.ToString("0.#") + "%");
            Text("ErrorsFoot", stats.Failed + " из " + stats.Completed + " запросов");
            Text("RequestFoot", stats.Cancelled > 0 ? "отменено: " + stats.Cancelled : "запросов");
            Text("ChartHint", results.Count == 0 ? "Ожидание первого запроса" : "TTFB · без времени загрузки тела");
            chart.SetResults(results);
            StackPanel rows = Get<StackPanel>("LogRows");
            rows.Children.Clear();
            Get<TextBlock>("LogEmpty").Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (RequestResult item in results.OrderByDescending(r => r.Number).Take(6))
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(65) });
                AddCell(row, 0, "#" + item.Number.ToString("D3"), "#8F8F8F");
                AddCell(row, 1, item.StatusText, item.Success ? "#DDDDDD" : "#A0A0A0");
                AddCell(row, 2, item.StatusCode > 0 ? item.LatencyMs.ToString("F0") + " мс" : "—", "#BDBDBD");
                AddCell(row, 3, "+" + item.OffsetSeconds.ToString("F1") + " с", "#828282");
                rows.Children.Add(row);
            }
            Text("LogFoot", "Последние " + Math.Min(6, results.Count) + " из " + results.Count + " запросов. Полный журнал — в CSV.");
            Get<TextBlock>("LogFoot").Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void AddCell(Grid row, int column, string value, string color)
        {
            var label = new TextBlock
            {
                Text = value, Foreground = Brush(color), FontSize = 10, Padding = new Thickness(0, 7, 8, 7),
                TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, ToolTip = value
            };
            Grid.SetColumn(label, column);
            row.Children.Add(label);
        }

        private void UpdateProgress()
        {
            double seconds = loadRunning ? elapsed.Elapsed.TotalSeconds : selectedReport == null ? 0 : selectedReport.ElapsedSeconds;
            seconds = Math.Min(seconds, plannedDuration);
            Text("ElapsedLabel", FormatTime(seconds) + " / " + FormatTime(plannedDuration));
            Get<Border>("ProgressFill").Width = Get<Grid>("ProgressTrack").ActualWidth * Math.Min(1, seconds / Math.Max(1, plannedDuration));
        }

        private static string FormatTime(double seconds)
        {
            TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return ((int)time.TotalMinutes).ToString("D2") + ":" + time.Seconds.ToString("D2");
        }

        private void UpdateHistory()
        {
            StackPanel rows = Get<StackPanel>("HistoryRows");
            rows.Children.Clear();
            Get<TextBlock>("HistoryEmpty").Visibility = history.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (RunReport entry in history)
            {
                RunReport report = entry;
                Statistics stats = Statistics.From(report.Results);
                var card = new Border
                {
                    Background = Brush("#101010"), BorderBrush = Brush("#303030"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Margin = new Thickness(0, 0, 0, 12)
                };
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = report.Target, FontWeight = FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 0, 0, 9) });
                panel.Children.Add(new TextBlock
                {
                    Text = report.StartedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") + "   ·   " + report.Rate + " зап./с   ·   " +
                        stats.Completed + " ответов   ·   P95 " + stats.P95.ToString("F0") + " мс   ·   ошибки " + stats.ErrorPercent.ToString("0.#") + "%",
                    Foreground = Brush("#9A9A9A"), FontSize = 10, Margin = new Thickness(0, 0, 0, 8)
                });
                panel.Children.Add(new TextBlock { Text = report.StopReason, FontSize = 10, Foreground = Brush("#BBBBBB"), Margin = new Thickness(0, 0, 0, 12) });
                var buttons = new StackPanel { Orientation = Orientation.Horizontal };
                var open = new Button { Content = "Открыть", Padding = new Thickness(12, 7, 12, 7), FontSize = 10, Margin = new Thickness(0, 0, 8, 0) };
                open.IsEnabled = cancellation == null;
                open.Click += delegate
                {
                    if (cancellation != null) return;
                    selectedReport = report;
                    results.Clear();
                    results.AddRange(report.Results);
                    plannedDuration = report.Duration;
                    chart.Duration = plannedDuration;
                    Get<TextBox>("TargetInput").Text = report.Target;
                    Get<TextBox>("RateInput").Text = report.Rate.ToString();
                    Get<TextBox>("DurationInput").Text = report.Duration.ToString();
                    Get<Button>("ExportButton").IsEnabled = true;
                    Get<Border>("ProbeDetailsBox").Visibility = Visibility.Collapsed;
                    Notice(report.StopReason + ". Пропущено запросов: " + report.Skipped + ".");
                    Text("StatusBadge", "Завершено");
                    Text("FooterText", "Результат из истории сессии");
                    UpdateMetrics(); UpdateProgress(); Navigate(0);
                };
                var export = new Button { Content = "Экспорт CSV", Padding = new Thickness(12, 7, 12, 7), FontSize = 10 };
                export.Click += delegate { Export(report); };
                buttons.Children.Add(open); buttons.Children.Add(export); panel.Children.Add(buttons);
                card.Child = panel; rows.Children.Add(card);
            }
        }

        private void Export(RunReport report)
        {
            if (report == null) return;
            var dialog = new SaveFileDialog
            {
                Title = "Сохранить отчёт Pulse", Filter = "CSV (*.csv)|*.csv", DefaultExt = ".csv",
                FileName = "pulse-" + report.StartedUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss") + ".csv"
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                ReportWriter.WriteCsv(dialog.FileName, report);
                Text("FooterText", "CSV сохранён: " + Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex) { Notice("Не удалось сохранить CSV: " + ex.Message); Navigate(0); }
        }
    }

    internal sealed class LatencyChart : FrameworkElement
    {
        private readonly FontFamily font;
        private List<RequestResult> results = new List<RequestResult>();
        public int Duration { get; set; }
        public LatencyChart(FontFamily font) { this.font = font; Duration = 30; }
        public void SetResults(IEnumerable<RequestResult> source) { results = source.ToList(); InvalidateVisual(); }
        private static SolidColorBrush Gray(byte value) { return new SolidColorBrush(Color.FromRgb(value, value, value)); }
        private void Label(DrawingContext dc, string value, double x, double y, byte color)
        {
            var text = new FormattedText(value, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                new Typeface(font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 9, Gray(color), VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(x, y));
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            double left = 43, top = 12, right = Math.Max(45, ActualWidth - 15), bottom = Math.Max(20, ActualHeight - 23);
            var groups = results.Where(r => !r.Cancelled && r.StatusCode > 0)
                .GroupBy(r => (int)r.OffsetSeconds).OrderBy(g => g.Key)
                .Select(g => new { Second = g.Key, Value = g.Average(r => r.LatencyMs) }).ToArray();
            double maximum = groups.Length == 0 ? 200 : Math.Max(10, Math.Ceiling(groups.Max(g => g.Value) * 1.2 / 10) * 10);
            for (int i = 0; i <= 3; i++)
            {
                double y = top + (bottom - top) * i / 3;
                dc.DrawLine(new Pen(Gray(47), 1), new Point(left, y), new Point(right, y));
                Label(dc, (maximum * (3 - i) / 3).ToString("F0"), 0, y - 5, 116);
            }
            Label(dc, "0 с", left, bottom + 9, 116);
            Label(dc, (Duration / 2).ToString() + " с", (left + right) / 2 - 10, bottom + 9, 116);
            Label(dc, Duration + " с", right - 25, bottom + 9, 116);
            if (groups.Length == 0)
            {
                const string message = "График появится после запуска теста";
                Label(dc, message, Math.Max(left + 10, (ActualWidth - 232) / 2), (top + bottom) / 2 - 5, 140);
                return;
            }
            var points = groups.Select(g => new Point(left + (right - left) * Math.Min(1, g.Second / (double)Math.Max(1, Duration)),
                bottom - (bottom - top) * g.Value / maximum)).ToArray();
            var area = new StreamGeometry();
            using (StreamGeometryContext context = area.Open())
            {
                context.BeginFigure(new Point(points[0].X, bottom), true, true);
                foreach (Point p in points) context.LineTo(p, true, false);
                context.LineTo(new Point(points[points.Length - 1].X, bottom), true, false);
            }
            var gradient = new LinearGradientBrush(Color.FromArgb(35, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
            dc.DrawGeometry(gradient, null, area);
            var line = new Pen(Gray(228), 1.8) { LineJoin = PenLineJoin.Round };
            for (int i = 1; i < points.Length; i++)
                if (groups[i].Second - groups[i - 1].Second <= 2) dc.DrawLine(line, points[i - 1], points[i]);
            foreach (Point point in points) dc.DrawEllipse(Gray(230), null, point, 1.8, 1.8);
            dc.DrawEllipse(Gray(255), null, points[points.Length - 1], 3, 3);
        }
    }
}
