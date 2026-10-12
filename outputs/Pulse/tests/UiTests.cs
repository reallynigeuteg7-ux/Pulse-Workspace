using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pulse
{
    internal static class UiTests
    {
        private static UserControl root;
        private static T Find<T>(string name) where T : FrameworkElement { return (T)root.FindName(name); }
        private static void Click(string name) { Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        private static async Task Until(Func<bool> condition, int milliseconds)
        {
            var clock = Stopwatch.StartNew();
            while (!condition())
            {
                if (clock.ElapsedMilliseconds > milliseconds) throw new Exception("UI operation timed out");
                await Task.Delay(25);
            }
        }
        private static void Snapshot(string path, int width, int height)
        {
            root.Width = width; root.Height = height;
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(path)) encoder.Save(output);
        }

        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.DefaultConnectionLimit = TestOptions.MaxConcurrency;
            string outputDirectory = args.Length == 0 ? AppDomain.CurrentDomain.BaseDirectory : args[0];
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            int exitCode = 1;
            app.Dispatcher.BeginInvoke(new Action(async delegate
            {
                Dashboard window = null;
                try
                {
                    window = new Dashboard(); root = (UserControl)window.Content;
                    Directory.CreateDirectory(outputDirectory);
                    Snapshot(Path.Combine(outputDirectory, "ui-initial.png"), 1240, 940);
                    GlyphTypeface glyph;
                    Check(new Typeface(root.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out glyph)
                        && glyph.FontUri.LocalPath.IndexOf("Montserrat", StringComparison.OrdinalIgnoreCase) >= 0,
                        "Bundled Montserrat did not load: " + (glyph == null ? "no glyph typeface" : glyph.FontUri.ToString()));
                    Check(glyph.CharacterToGlyphMap.ContainsKey(0x0416), "Font has no Cyrillic glyphs");
                    Console.WriteLine("PASS WPF XAML and bundled Montserrat with Cyrillic");
                    Snapshot(Path.Combine(outputDirectory, "ui-minimum.png"), 1060, 680);
                    Point startPosition = Find<Button>("StartButton").TranslatePoint(new Point(), root);
                    Check(Find<TextBox>("TargetInput").ActualWidth >= 300 && startPosition.Y + Find<Button>("StartButton").ActualHeight < 680,
                        "Essential controls do not fit the minimum window size");
                    Console.WriteLine("PASS minimum window layout keeps target and start controls usable");
                    Find<TextBox>("TargetInput").Text = "ftp://localhost/file";
                    Click("StartButton");
                    Check(Find<Border>("NoticeBox").Visibility == Visibility.Visible && Find<Button>("StartButton").IsEnabled, "Invalid target was not shown inline");
                    Console.WriteLine("PASS inline validation leaves the UI usable");
                    using (var server = new LocalServer())
                    {
                        Find<TextBox>("TargetInput").Text = server.Uri("/ok").AbsoluteUri;
                        Click("ProbeButton");
                        await Until(() => Find<Button>("ProbeButton").IsEnabled, 3000);
                        Check(Find<TextBlock>("NoticeText").Text.Contains("HTTP 200") && Find<TextBox>("ProbeDetailsText").Text.Contains("X-Pulse-Test"), "Probe UI did not show HTTP result");
                        Console.WriteLine("PASS probe button displays real status and headers");
                        Find<TextBox>("DurationInput").Text = "5";
                        Find<TextBox>("RateInput").Text = "100";
                        Click("StartButton");
                        Check(!Find<TextBox>("TargetInput").IsEnabled && Find<Button>("StopButton").IsEnabled, "Running inputs not locked");
                        await Until(() => Find<Button>("StartButton").IsEnabled, 7000);
                        Check(Int32.Parse(Find<TextBlock>("RequestsValue").Text) >= 100 && Find<Button>("ExportButton").IsEnabled, "Completed metrics/export state incorrect");
                        Check(Find<StackPanel>("LogRows").Children.Count == 6, "Request log did not update");
                        Snapshot(Path.Combine(outputDirectory, "ui-completed.png"), 1240, 1100);
                        Console.WriteLine("PASS actual load test updates metrics, chart, log and export state");
                        Click("HistoryNav");
                        Check(Find<StackPanel>("HistoryPage").Visibility == Visibility.Visible && Find<StackPanel>("HistoryRows").Children.Count == 1, "History navigation failed");
                        Snapshot(Path.Combine(outputDirectory, "ui-history.png"), 1240, 840);
                        Click("HelpNav");
                        Check(Find<StackPanel>("HelpPage").Visibility == Visibility.Visible, "Help navigation failed");
                        Click("TestNav");
                        Find<TextBox>("TargetInput").Text = server.Uri("/hang").AbsoluteUri;
                        Click("StartButton");
                        await Task.Delay(150);
                        Click("StopButton");
                        await Until(() => Find<Button>("StartButton").IsEnabled, 2000);
                        Check(Find<TextBlock>("NoticeText").Text.Contains("пользователем"), "Stop button did not cancel the test");
                        Console.WriteLine("PASS history, help navigation and manual stop button");
                        Click("StartButton");
                        await Task.Delay(150);
                        window.Close();
                        await Until(() => Find<Button>("StartButton").IsEnabled, 2000);
                        Console.WriteLine("PASS closing a running window cancels pending work");
                    }
                    exitCode = 0;
                    Console.WriteLine("ALL 7 UI CHECKS PASSED");
                }
                catch (Exception ex) { Console.Error.WriteLine("FAIL " + ex); }
                finally { if (window != null) window.Close(); app.Shutdown(); }
            }));
            app.Run();
            return exitCode;
        }
    }
}
