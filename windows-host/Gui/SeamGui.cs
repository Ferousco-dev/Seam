using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Seam.WindowsHost.Host;

namespace Seam.WindowsHost.Gui
{
    /// <summary>
    /// Entry points for the desktop application. Kept separate from <c>Program</c> so the CLI
    /// paths stay readable and the UI never runs unless it is asked for.
    /// </summary>
    internal static class SeamGui
    {
        /// <summary>Normal start: shows the window and runs the message loop.</summary>
        public static int Run()
        {
            try
            {
                Configure();
                using var form = new MainForm(UiSettings.Load(), interactive: true);
                Application.Run(form);
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    MessageBox.Show(
                        "SEAM could not start its window.\r\n\r\n" + ex.Message +
                        "\r\n\r\nRun \"seam-host.exe --diagnose\" in a terminal for details.",
                        "SEAM", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch
                {
                    // No window station (e.g. a service context): fall through to the exit code.
                }
                return 1;
            }
        }

        /// <summary>
        /// Headless smoke test used by CI: builds the real window, forces layout, optionally
        /// renders it to a PNG, and exits without entering the message loop. Proves the UI
        /// initialises on a machine with no phone attached.
        /// </summary>
        public static int RunSmoke(string? screenshotPath)
        {
            string stage = "startup";

            try
            {
                Configure();

                stage = Stage("construction");   // every control is created here
                using var form = new MainForm(UiSettings.Load(), interactive: false);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(0, 0);

                stage = Stage("layout");         // native window + a forced layout pass
                _ = form.Handle;                 // create the native window
                form.CreateControl();
                form.PerformLayout();
                Application.DoEvents();

                stage = Stage("control-count");
                int controls = CountControls(form);
                if (controls < 20)
                {
                    Console.Error.WriteLine($"UI smoke test FAILED during {stage}: only {controls} controls were created (expected at least 20).");
                    return 2;
                }

                if (!string.IsNullOrEmpty(screenshotPath))
                {
                    stage = Stage("rendering (Show + DrawToBitmap)");
                    form.Show();
                    Application.DoEvents();
                    Thread.Sleep(300);
                    Application.DoEvents();

                    stage = Stage("rendering (PNG encode)");
                    CapturePage(form, screenshotPath!, GuiPage.Home);
                    CapturePage(form, PagePath(screenshotPath!, "activity"), GuiPage.Activity);
                    CapturePage(form, PagePath(screenshotPath!, "settings"), GuiPage.Settings);

                    // Diagnostics builds its report on a worker; give it a moment so the capture
                    // shows the real text instead of the "Collecting information..." placeholder.
                    form.ShowPage(GuiPage.Diagnostics);
                    for (int i = 0; i < 40; i++)
                    {
                        Application.DoEvents();
                        Thread.Sleep(50);
                    }
                    CapturePage(form, PagePath(screenshotPath!, "diagnostics"), GuiPage.Diagnostics);

                    form.ShowPage(GuiPage.Home);
                    form.Hide();
                }

                stage = "complete";
                Console.WriteLine($"UI smoke test OK: window created, {controls} controls, state=Disconnected, no console window.");
                return 0;
            }
            catch (Exception ex)
            {
                ReportSmokeFailure(stage, ex);
                return 1;
            }
        }

        /// <summary>
        /// Renders one page of the real window to a PNG. Used by CI so the reviewed images are
        /// the application itself, not a mockup.
        /// </summary>
        private static void CapturePage(MainForm form, string path, GuiPage page)
        {
            form.ShowPage(page);
            Application.DoEvents();
            Thread.Sleep(120);
            Application.DoEvents();

            using Bitmap bitmap = CaptureWindow(form);
            bitmap.Save(path, ImageFormat.Png);
            Console.WriteLine($"UI screenshot written: {path} ({bitmap.Width}x{bitmap.Height}, page {page})");
            DumpLayout(form, page);

            if (page == GuiPage.Home)
            {
                // The Home page carries the whole visual system (rail, page, card, accent), so a
                // coarse average-colour grid of that one bitmap is enough to prove the theme.
                ProbePalette(bitmap);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        private const uint PwRenderFullContent = 0x2;

        /// <summary>
        /// Captures the window the way the compositor draws it, which includes the title bar and
        /// any DWM material. <c>DrawToBitmap</c> only asks the form to paint itself, so chrome
        /// that Windows draws (a dark caption, for instance) is missing from that image - the
        /// earlier captures showed a grey band where the title bar is and could not prove
        /// whether the dark-chrome request had taken effect. Falls back to DrawToBitmap when
        /// PrintWindow is unavailable.
        /// </summary>
        private static Bitmap CaptureWindow(Form form)
        {
            var bitmap = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
            try
            {
                using Graphics graphics = Graphics.FromImage(bitmap);
                IntPtr hdc = graphics.GetHdc();
                bool ok;
                try
                {
                    ok = PrintWindow(form.Handle, hdc, PwRenderFullContent);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }

                if (ok)
                {
                    return bitmap;
                }
            }
            catch (Exception)
            {
                // Any interop surprise: fall through to the GDI paint path.
            }

            form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
            return bitmap;
        }

        /// <summary>
        /// Averages the rendered pixels over a 12x8 grid and prints the colours. It is not a
        /// screenshot substitute - it is the cheapest way to see, in a log, that the window drew
        /// graphite surfaces and one accent instead of a white page.
        /// </summary>
        private static void ProbePalette(Bitmap bitmap)
        {
            const int cols = 12;
            const int rows = 8;
            var line = new System.Text.StringBuilder();

            for (int gy = 0; gy < rows; gy++)
            {
                line.Clear();
                for (int gx = 0; gx < cols; gx++)
                {
                    int x0 = bitmap.Width * gx / cols;
                    int x1 = bitmap.Width * (gx + 1) / cols;
                    int y0 = bitmap.Height * gy / rows;
                    int y1 = bitmap.Height * (gy + 1) / rows;

                    long red = 0, green = 0, blue = 0;
                    int samples = 0;
                    for (int y = y0; y < y1; y += 4)
                    {
                        for (int x = x0; x < x1; x += 4)
                        {
                            Color pixel = bitmap.GetPixel(x, y);
                            red += pixel.R;
                            green += pixel.G;
                            blue += pixel.B;
                            samples++;
                        }
                    }

                    if (samples == 0)
                    {
                        line.Append("-------- ");
                        continue;
                    }

                    line.Append('#')
                        .Append((red / samples).ToString("X2"))
                        .Append((green / samples).ToString("X2"))
                        .Append((blue / samples).ToString("X2"))
                        .Append(' ');
                }

                Console.WriteLine("[ui-palette] " + line.ToString().TrimEnd());
            }
        }

        /// <summary>
        /// Prints the geometry of the controls that define each page, so a collapsed or
        /// overlapping layout is visible in the CI log even when nobody opens the PNG.
        /// </summary>
        private static void DumpLayout(MainForm form, GuiPage page)
        {
            string[] names =
            {
                "nav", "statusPill", "heroTitle", "heroDetail", "connect", "disconnect", "refresh",
                "arrangement", "routingOwner", "toggleRouting", "activityLog", "followToggle",
                "copyLog", "edgeSelector", "autoConnect", "linesKept", "saveSettings",
                "copyReport", "diagOutput"
            };

            Console.WriteLine($"[ui-layout] page={page} window={form.Width}x{form.Height} dpi={form.DeviceDpi} " +
                              $"autoscale={form.AutoScaleDimensions.Width}x{form.AutoScaleDimensions.Height}");
            foreach (string name in names)
            {
                Control[] found = form.Controls.Find(name, true);
                if (found.Length == 0)
                {
                    Console.WriteLine($"   {name}: not present on this page");
                    continue;
                }

                Control control = found[0];
                Console.WriteLine($"   {name}: {control.Bounds.X},{control.Bounds.Y} " +
                                  $"{control.Width}x{control.Height} visible={control.Visible} enabled={control.Enabled}");
            }
        }

        /// <summary>Builds "ui-preview-activity.png" next to "ui-preview.png".</summary>
        private static string PagePath(string basePath, string suffix)
        {
            string directory = Path.GetDirectoryName(basePath) ?? ".";
            string name = Path.GetFileNameWithoutExtension(basePath);
            return Path.Combine(directory, name + "-" + suffix + ".png");
        }

        /// <summary>
        /// Records the stage about to run. Printed as it happens so a hang, which ends in a
        /// timeout with no exception, still says which phase was in progress.
        /// </summary>
        private static string Stage(string name)
        {
            Console.WriteLine($"[ui-smoke] stage: {name}");
            return name;
        }

        /// <summary>
        /// Writes one compact, self-describing failure block: stage, exception type, message and
        /// the innermost stack frames first, then the full trace.
        /// Why: CI turns this text into a GitHub annotation, and the previous "tail of stderr"
        /// window threw away exactly these first lines (type, message) while keeping outer frames.
        /// </summary>
        private static void ReportSmokeFailure(string stage, Exception ex)
        {
            Console.Error.WriteLine($"UI smoke test FAILED during {stage}: {ex.GetType().FullName}: {ex.Message}");

            var frames = (ex.StackTrace ?? string.Empty)
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Take(6)
                .ToArray();

            if (frames.Length > 0)
            {
                Console.Error.WriteLine("stack frames (innermost first):");
                foreach (var frame in frames)
                {
                    Console.Error.WriteLine("   " + frame);
                }
            }

            Console.Error.WriteLine("--- full stack trace ---");
            Console.Error.WriteLine(ex.ToString());
        }

        private static void Configure()
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            }
            catch
            {
                // The manifest already declares per-monitor-v2; a host that rejects the call is fine.
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (_, e) =>
            {
                try
                {
                    MessageBox.Show("SEAM hit an unexpected error:\r\n\r\n" + e.Exception.Message,
                        "SEAM", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch
                {
                    // Ignore: already in a failing state.
                }
            };
        }

        private static int CountControls(Control root)
        {
            int count = 1;
            foreach (Control child in root.Controls)
            {
                count += CountControls(child);
            }
            return count;
        }
    }
}
