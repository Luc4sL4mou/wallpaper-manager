namespace WallpaperManager.Models;

/// <summary>
/// Um monitor conectado. DeviceId é a identidade do monitor; resolução e posição são apenas atributos.
/// </summary>
/// <param name="DeviceId">Caminho do dispositivo (\\?\DISPLAY#...), o mesmo que o IDesktopWallpaper usa.</param>
/// <param name="DisplayName">Nome GDI da saída de vídeo (ex.: \\.\DISPLAY1).</param>
/// <param name="MonitorName">Descrição do monitor informada pelo driver.</param>
/// <param name="Width">Largura em pixels físicos.</param>
/// <param name="Height">Altura em pixels físicos.</param>
/// <param name="X">Posição horizontal no desktop virtual.</param>
/// <param name="Y">Posição vertical no desktop virtual.</param>
/// <param name="ScalePercent">Escala do Windows (100, 125, 150...).</param>
/// <param name="IsPrimary">Se é o monitor principal.</param>
public sealed record MonitorInfo(
    string DeviceId,
    string DisplayName,
    string MonitorName,
    int Width,
    int Height,
    int X,
    int Y,
    int ScalePercent,
    bool IsPrimary)
{
    public double AspectRatio => (double)Width / Height;

    public string ResolutionText => $"{Width} × {Height}";

    public string AspectRatioText
    {
        get
        {
            int divisor = GreatestCommonDivisor(Width, Height);
            return $"{Width / divisor}:{Height / divisor} ({AspectRatio:0.000})";
        }
    }

    public string PositionText => $"({X}, {Y})";

    public string ScaleText => $"{ScalePercent}%";

    public string PrimaryText => IsPrimary ? "Sim" : "";

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return Math.Max(a, 1);
    }
}
