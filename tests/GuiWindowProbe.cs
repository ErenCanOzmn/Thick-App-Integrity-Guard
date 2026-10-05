using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class GuiWindowProbe
{
    private delegate bool EnumCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, UIntPtr wParam,
        StringBuilder buffer, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public static IntPtr[] Windows(int processId)
    {
        var result = new List<IntPtr>();
        EnumWindows((window, parameter) => {
            uint pid; GetWindowThreadProcessId(window, out pid);
            if (pid == processId) result.Add(window);
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
    public static IntPtr[] Children(IntPtr parent)
    {
        var result = new List<IntPtr>();
        EnumChildWindows(parent, (window, parameter) => { result.Add(window); return true; }, IntPtr.Zero);
        return result.ToArray();
    }
    public static string Text(IntPtr window)
    {
        var buffer = new StringBuilder(4096);
        UIntPtr copied;
        SendMessageTimeout(window, 0x000D, new UIntPtr(4096), buffer, 2, 1000, out copied);
        return buffer.ToString();
    }
    public static string ClassName(IntPtr window)
    {
        var buffer = new StringBuilder(256);
        GetClassName(window, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
