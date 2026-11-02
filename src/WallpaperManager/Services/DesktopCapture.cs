using System.Runtime.InteropServices;
using System.Text;
using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// Informações sobre a área de trabalho de um monitor usadas pela transição: onde fica a janela da área de
/// trabalho, qual é a área útil (sem a barra de tarefas) e se alguma janela cobre o monitor.
/// </summary>
internal static class DesktopCapture
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }

    /// <summary>
    /// A área útil do monitor (a tela menos a barra de tarefas), em pixels do desktop virtual.
    /// Se o Windows não responder, devolve o monitor inteiro.
    /// </summary>
    public static (int X, int Y, int Width, int Height) GetWorkArea(MonitorInfo monitor)
    {
        IntPtr handle = MonitorFromPoint(new POINT { X = monitor.X + monitor.Width / 2, Y = monitor.Y + monitor.Height / 2 }, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (handle == IntPtr.Zero || !GetMonitorInfo(handle, ref info))
        {
            return (monitor.X, monitor.Y, monitor.Width, monitor.Height);
        }

        return (info.Work.L, info.Work.T, info.Work.R - info.Work.L, info.Work.B - info.Work.T);
    }

    /// <summary>
    /// A "janela" da área de trabalho. A janela de transição é colocada logo acima dela.
    /// </summary>
    public static IntPtr FindDesktopWindow() => FindWindow("Progman", null);

    /// <summary>
    /// Verdadeiro se a janela em primeiro plano cobre o monitor (tela cheia ou maximizada): ninguém veria a
    /// transição, então ela é pulada (importante para jogos).
    /// </summary>
    public static bool IsDesktopCovered(MonitorInfo monitor)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !GetWindowRect(foreground, out RECT r))
        {
            return false;
        }

        var name = new StringBuilder(64);
        GetClassName(foreground, name, name.Capacity);
        if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            return false; // o foco está na própria área de trabalho ou na barra de tarefas
        }

        long overlapW = Math.Max(0, Math.Min(r.R, monitor.X + monitor.Width) - Math.Max(r.L, monitor.X));
        long overlapH = Math.Max(0, Math.Min(r.B, monitor.Y + monitor.Height) - Math.Max(r.T, monitor.Y));
        return overlapW * overlapH >= 0.9 * monitor.Width * monitor.Height;
    }
}
