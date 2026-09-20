using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TaskbarMonitor
{
    /// <summary>
    /// Minimal window wrapper over RegisterClassEx and CreateWindowEx.
    ///
    /// Messages arrive at one static procedure and are routed to the owning instance
    /// through a handle lookup. Anything that arrives before CreateWindowEx returns,
    /// so before the handle is known, goes to DefWindowProc, which is fine because
    /// neither window here does any work at creation time.
    /// </summary>
    internal abstract class Win32Window : IDisposable
    {
        private const string ClassName = "TaskbarMonitorWindow";
        private const uint CS_DBLCLKS = 0x0008;

        private static readonly Dictionary<IntPtr, Win32Window> Windows = new Dictionary<IntPtr, Win32Window>();
        private static readonly Native.WndProc Procedure = Dispatch;
        private static bool _classRegistered;

        public IntPtr Handle { get; private set; }

        protected void CreateWindow(int exStyle, int style, IntPtr parent, int x, int y, int width, int height, string title)
        {
            EnsureClassRegistered();

            IntPtr handle = Native.CreateWindowExW(exStyle, ClassName, title, style,
                x, y, width, height, parent, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);

            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastWin32Error());

            Handle = handle;
            Windows[handle] = this;
        }

        protected void DestroyWindowHandle()
        {
            if (Handle == IntPtr.Zero) return;

            IntPtr handle = Handle;
            Handle = IntPtr.Zero;
            Windows.Remove(handle);
            Native.DestroyWindow(handle);
        }

        /// <summary>Return true from a handler to stop DefWindowProc seeing the message.</summary>
        protected abstract bool OnMessage(uint message, IntPtr wParam, IntPtr lParam, ref IntPtr result);

        private static IntPtr Dispatch(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
        {
            Win32Window window;
            if (Windows.TryGetValue(hWnd, out window))
            {
                IntPtr result = IntPtr.Zero;
                try
                {
                    if (window.OnMessage(message, wParam, lParam, ref result)) return result;
                }
                catch
                {
                    // A managed exception must not escape into Explorer's call stack.
                }

                if (message == Native.WM_DESTROY) Windows.Remove(hWnd);
            }

            return Native.DefWindowProcW(hWnd, message, wParam, lParam);
        }

        private static void EnsureClassRegistered()
        {
            if (_classRegistered) return;

            Native.WNDCLASSEX wc = new Native.WNDCLASSEX();
            wc.cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEX>();
            wc.style = CS_DBLCLKS;
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure);
            wc.hInstance = Native.GetModuleHandleW(null);
            wc.lpszClassName = ClassName;

            if (Native.RegisterClassExW(ref wc) == 0)
                throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastWin32Error());

            _classRegistered = true;
        }

        public virtual void Dispose()
        {
            DestroyWindowHandle();
        }
    }
}
