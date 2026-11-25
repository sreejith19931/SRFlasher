using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SRFlasher.Services
{
    /// <summary>
    /// Paints the native window frame to match the app theme. Windows draws the
    /// caption itself, so a themed window needs the DWM immersive-dark flag;
    /// without it a dark app sits under a bright white title bar.
    /// </summary>
    internal static class WindowChrome
    {
        // Attribute id on Windows 10 20H1+ / Windows 11, and the earlier id.
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeLegacy = 19;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>
        /// Ask DWM to render the title bar dark or light. Harmlessly does nothing
        /// on Windows versions or DWM states that do not support the attribute.
        /// </summary>
        public static void ApplyTitleBar(Window window, bool dark)
        {
            if (window == null)
                return;

            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
                return;

            int value = dark ? 1 : 0;

            try
            {
                if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
            }
            catch (DllNotFoundException)
            {
                // DWM missing (very old Windows): keep the default frame.
            }
            catch (EntryPointNotFoundException)
            {
                // dwmapi.dll present but without the export.
            }
        }
    }
}
