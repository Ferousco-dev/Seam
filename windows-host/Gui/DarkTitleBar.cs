using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Seam.WindowsHost.Gui
{
    /// <summary>
    /// Asks the desktop compositor to draw the window chrome dark, so the title bar matches the
    /// graphite workspace instead of sitting above it as a light grey strip.
    ///
    /// This is the only place the GUI talks to DWM. Everything is optional: on Windows 10 before
    /// 2004, on a remote session or under a custom theme each call simply fails and the window
    /// keeps its default chrome. No behaviour depends on the result.
    /// </summary>
    internal static class DarkTitleBar
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeLegacy = 19;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaBorderColor = 34;

        [DllImport("dwmapi.dll", SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>Applies the dark chrome. Safe to call more than once.</summary>
        public static void Apply(Form form)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            {
                return;
            }

            try
            {
                IntPtr handle = form.Handle;
                int enabled = 1;
                if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref enabled, sizeof(int));
                }

                // DWMWA_CAPTION_COLOR / DWMWA_BORDER_COLOR are Windows 11 only; the call fails
                // harmlessly elsewhere, which is why the result is ignored.
                int caption = ToColorRef(Theme.NavBackground);
                DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));

                int border = ToColorRef(Theme.NavBackground);
                DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            catch (Exception)
            {
                // Chrome colour is cosmetic: never let it stop the window from opening.
            }
        }

        /// <summary>COLORREF is 0x00BBGGRR, not the 0x00RRGGBB that Color.ToArgb returns.</summary>
        private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
    }
}
