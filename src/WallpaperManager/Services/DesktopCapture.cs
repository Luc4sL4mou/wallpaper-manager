using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WallpaperManager.Models;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace WallpaperManager.Services;

/// <summary>
/// Fotografa a área de trabalho (papel de parede + ícones, sem as janelas) de um monitor.
/// Usado pela transição suave: a foto de antes e a de depois têm os mesmos ícones, então eles ficam parados
/// enquanto só o papel de parede se mistura.
/// </summary>
internal static class DesktopCapture
{
    // Tamanho da grade usada para comparar fotos (pequena de propósito: é só uma impressão digital)
    private const int GridWidth = 32;
    private const int GridHeight = 18;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
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
    /// Foto da área de trabalho na região do monitor, ou null se não foi possível.
    /// </summary>
    public static BitmapSource? CaptureMonitor(MonitorInfo monitor)
    {
        IntPtr progman = FindDesktopWindow();
        if (progman == IntPtr.Zero || !GetWindowRect(progman, out RECT area))
        {
            return null;
        }

        int x = monitor.X - area.L, y = monitor.Y - area.T;
        int width = area.R - area.L, height = area.B - area.T;
        if (x < 0 || y < 0 || x + monitor.Width > width || y + monitor.Height > height)
        {
            return null;
        }

        using var bitmap = new Bitmap(width, height, DrawingPixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            IntPtr dc = graphics.GetHdc();
            try
            {
                // 2 = PW_RENDERFULLCONTENT: pede ao DWM a imagem já composta (inclui o papel de parede)
                if (!PrintWindow(progman, dc, 2))
                {
                    return null;
                }
            }
            finally
            {
                graphics.ReleaseHdc(dc);
            }
        }

        BitmapData data = bitmap.LockBits(new Rectangle(x, y, monitor.Width, monitor.Height), ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            int bufferSize = data.Stride * (monitor.Height - 1) + monitor.Width * 4;
            BitmapSource source = BitmapSource.Create(
                monitor.Width, monitor.Height, 96, 96, PixelFormats.Bgr32, null, data.Scan0, bufferSize, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>
    /// A foto "se parece" com a imagem que deveria estar no papel de parede? Protege contra versões do Windows
    /// em que a captura vem vazia ou errada: nesse caso a transição é pulada em vez de mostrar uma imagem falsa.
    /// </summary>
    public static bool LooksLike(BitmapSource capture, string imagePath)
    {
        BitmapSource? thumbnail = ThumbnailLoader.TryLoad(imagePath, 160) as BitmapSource;
        return thumbnail is not null && MeanDifference(Fingerprint(capture), Fingerprint(thumbnail)) < 45;
    }

    /// <summary>As duas fotos são visivelmente diferentes (o papel de parede já trocou)?</summary>
    public static bool Differs(BitmapSource a, BitmapSource b) => MeanDifference(Fingerprint(a), Fingerprint(b)) > 1.5;

    /// <summary>
    /// Verdadeiro se a janela em primeiro plano cobre o monitor (tela cheia ou maximizada): ninguém veria a
    /// transição, então ela é pulada (importante para jogos, que não devem sofrer com capturas de tela).
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

    // Imagem reduzida para uma grade fixa, em bytes BGR, para comparar rapidamente
    private static byte[] Fingerprint(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        var scaled = new TransformedBitmap(converted, new ScaleTransform(
            GridWidth / (double)converted.PixelWidth, GridHeight / (double)converted.PixelHeight));
        int stride = scaled.PixelWidth * 4;
        var pixels = new byte[stride * scaled.PixelHeight];
        scaled.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        long sum = 0;
        int counted = 0;
        for (int i = 0; i + 2 < n; i += 4)
        {
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            counted += 3;
        }

        return counted == 0 ? 0 : sum / (double)counted;
    }
}
