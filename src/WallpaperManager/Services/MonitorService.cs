using System.Runtime.InteropServices;
using Microsoft.Win32;
using WallpaperManager.Interop;
using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// Detecta os monitores conectados usando a API Win32.
/// Como o app é PerMonitorV2, os retângulos vêm em pixels físicos reais.
/// </summary>
public sealed class MonitorService
{
    /// <summary>
    /// Disparado quando um monitor é conectado/desconectado ou muda de resolução.
    /// Atenção: pode chegar fora da thread da interface.
    /// </summary>
    public event EventHandler? MonitorsChanged
    {
        add => SystemEvents.DisplaySettingsChanged += value;
        remove => SystemEvents.DisplaySettingsChanged -= value;
    }

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        // EnumDisplayMonitors chama o callback uma vez para cada monitor ativo
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
            {
                var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    var (deviceId, monitorName) = GetMonitorDevice(info.szDevice);
                    NativeMethods.RECT bounds = info.rcMonitor;

                    monitors.Add(new MonitorInfo(
                        DeviceId: deviceId,
                        DisplayName: info.szDevice,
                        MonitorName: monitorName,
                        Width: bounds.Right - bounds.Left,
                        Height: bounds.Bottom - bounds.Top,
                        X: bounds.Left,
                        Y: bounds.Top,
                        ScalePercent: GetScalePercent(hMonitor),
                        IsPrimary: (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0));
                }
                return true; // true = continuar enumerando
            },
            IntPtr.Zero);

        // Ordena da esquerda para a direita, como os monitores estão fisicamente na mesa
        return monitors.OrderBy(m => m.X).ThenBy(m => m.Y).ToList();
    }

    /// <summary>
    /// Para uma saída de vídeo (\\.\DISPLAY1), busca o monitor ativo ligado a ela.
    /// Com EDD_GET_DEVICE_INTERFACE_NAME, DeviceID vem no formato \\?\DISPLAY#...{guid},
    /// que é o mesmo ID aceito por IDesktopWallpaper.SetWallpaper.
    /// </summary>
    private static (string DeviceId, string MonitorName) GetMonitorDevice(string adapterName)
    {
        var device = new NativeMethods.DISPLAY_DEVICE { cb = Marshal.SizeOf<NativeMethods.DISPLAY_DEVICE>() };

        for (uint index = 0;
             NativeMethods.EnumDisplayDevices(adapterName, index, ref device, NativeMethods.EDD_GET_DEVICE_INTERFACE_NAME);
             index++)
        {
            if ((device.StateFlags & NativeMethods.DISPLAY_DEVICE_ACTIVE) != 0)
            {
                return (device.DeviceID, device.DeviceString);
            }
        }

        return (string.Empty, "(desconhecido)");
    }

    private static int GetScalePercent(IntPtr hMonitor)
    {
        const int S_OK = 0;
        return NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == S_OK
            ? (int)Math.Round(dpiX * 100.0 / 96)
            : 100;
    }
}
