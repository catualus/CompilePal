using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CompilePalX
{
    /// <summary>
    /// Flashes a window's taskbar button to say it wants attention.
    ///
    /// A compile can run for half an hour, and the point at which it finishes is exactly the point
    /// at which nobody is looking at it. There is already a completion sound, but a sound is missed
    /// with headphones off or the machine muted, and it says nothing once it has stopped playing.
    /// The taskbar button keeps saying it until the window is looked at.
    ///
    /// There is no WPF equivalent, so this is the Win32 call. It is deliberately the whole of the
    /// interop surface for this feature: no window subclassing, no message loop, nothing to undo.
    /// </summary>
    internal static class TaskbarFlash
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint cbSize;
            public IntPtr hwnd;
            public uint dwFlags;
            public uint uCount;
            public uint dwTimeout;
        }

        /// <summary>Flash the taskbar button.</summary>
        private const uint FLASHW_TRAY = 0x00000002;

        /// <summary>Keep flashing until the window comes to the foreground, then stop by itself.</summary>
        private const uint FLASHW_TIMERNOFG = 0x0000000C;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

        /// <summary>
        /// Asks for attention, unless the window already has it.
        ///
        /// Flashing a window the user is looking at is noise, and on some Windows configurations a
        /// foreground window flashes anyway rather than being ignored, so the check is here rather
        /// than left to the platform.
        ///
        /// Every failure is silent. This is a courtesy at the end of a compile; it must never be the
        /// reason anything goes wrong, and there is no sensible thing to tell the user if the
        /// platform declines to flash a window.
        /// </summary>
        public static void Request(Window window)
        {
            try
            {
                if (window is null || window.IsActive)
                    return;

                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero)
                    return;

                var info = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = handle,
                    dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,

                    // Ignored when FLASHW_TIMERNOFG is set, which is what governs how long this runs.
                    uCount = uint.MaxValue,
                    dwTimeout = 0,
                };

                FlashWindowEx(ref info);
            }
            catch (Exception ex)
            {
                CompilePalX.Compiling.CompilePalLogger.LogLineDebug(
                    $"Could not flash the taskbar button: {ex.Message}");
            }
        }
    }
}
