using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;

namespace ChGhost.Core.Native;

public static class Win32Interop
{
    public const uint WdaExcludeFromCapture = 0x00000011;
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExLayered = 0x00080000;
    public const int WmHotkey = 0x0312;
    public const uint ModAlt = 0x0001;
    public const uint ModNoRepeat = 0x4000;
    public const int VkLButton = 0x01;

    public static bool TrySetWindowDisplayAffinity(IntPtr window, uint affinity, out int errorCode)
    {
        Marshal.SetLastPInvokeError(0);
        bool succeeded = NativeMethods.SetWindowDisplayAffinity(window, affinity);
        errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    public static bool TrySetWindowLong(IntPtr window, int index, int value, out int errorCode)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr previous = NativeMethods.SetWindowLongPtr(window, index, new IntPtr(value));
        errorCode = previous == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        return errorCode == 0;
    }

    public static int GetWindowLong(IntPtr window, int index) =>
        NativeMethods.GetWindowLongPtr(window, index).ToInt32();

    public static bool TryRegisterHotKey(IntPtr window, int id, uint modifiers, uint key, out int errorCode)
    {
        Marshal.SetLastPInvokeError(0);
        bool succeeded = NativeMethods.RegisterHotKey(window, id, modifiers, key);
        errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    public static bool UnregisterHotKey(IntPtr window, int id) =>
        NativeMethods.UnregisterHotKey(window, id);

    public static bool TryUnregisterHotKey(IntPtr window, int id, out int errorCode)
    {
        Marshal.SetLastPInvokeError(0);
        bool succeeded = NativeMethods.UnregisterHotKey(window, id);
        errorCode = succeeded ? 0 : Marshal.GetLastWin32Error();
        return succeeded;
    }

    public static Point GetCursorPosition()
    {
        if (!NativeMethods.GetCursorPos(out Point point))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetCursorPos failed.");
        return point;
    }

    public static bool IsLeftMouseButtonDown() =>
        (NativeMethods.GetAsyncKeyState(VkLButton) & 0x8000) != 0;

    public static Rectangle GetWindowRectangle(IntPtr window)
    {
        if (!NativeMethods.GetWindowRect(window, out NativeRect rectangle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
        return Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        internal static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);
    }
}
