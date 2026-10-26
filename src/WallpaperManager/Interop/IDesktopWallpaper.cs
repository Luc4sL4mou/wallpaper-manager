using System.Runtime.InteropServices;

namespace WallpaperManager.Interop;

/// <summary>
/// Modos de ajuste da imagem (DESKTOP_WALLPAPER_POSITION em shobjidl_core.h).
/// </summary>
internal enum DesktopWallpaperPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5,
}

/// <summary>
/// Interface COM do Windows que controla o papel de parede POR MONITOR.
/// A ordem dos métodos precisa ser exatamente a do Windows (é uma tabela de ponteiros, a "vtable").
/// Declaramos só até GetPosition; os métodos de apresentação de slides que vêm depois não são usados.
/// </summary>
[ComImport]
[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId);

    [return: MarshalAs(UnmanagedType.LPWStr)]
    string GetMonitorDevicePathAt(uint monitorIndex);

    uint GetMonitorDevicePathCount();

    NativeMethods.RECT GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId);

    void SetBackgroundColor(uint color);

    uint GetBackgroundColor();

    void SetPosition(DesktopWallpaperPosition position);

    DesktopWallpaperPosition GetPosition();
}

/// <summary>
/// Identificador da classe COM "DesktopWallpaper", usado para criar o objeto que implementa a interface.
/// </summary>
internal static class DesktopWallpaperClsid
{
    public static readonly Guid Value = new("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD");
}
