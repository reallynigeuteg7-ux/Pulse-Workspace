using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

[assembly: AssemblyTitle("Pulse — Server Testing")]
[assembly: AssemblyDescription("Bounded HTTP server testing dashboard")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace Pulse
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            ServicePointManager.DefaultConnectionLimit = TestOptions.MaxConcurrency;
            ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                var window = new Dashboard();
                if (args.Length == 2 && args[0] == "--preview")
                {
                    var root = (FrameworkElement)window.Content;
                    root.Width = 1240;
                    root.Height = 1000;
                    root.Measure(new Size(1240, 1000));
                    root.Arrange(new Rect(0, 0, 1240, 1000));
                    root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1240, 1000, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(args[1])) png.Save(output);
                    return 0;
                }
                return app.Run(window);
            }
            catch (Exception ex)
            {
                if (args.Length > 0)
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.txt"), ex.ToString());
                    return 1;
                }
                MessageBox.Show("Не удалось запустить Pulse.\n\n" + ex.Message, "Pulse", MessageBoxButton.OK, MessageBoxImage.Error);
                return 1;
            }
        }
    }
}
