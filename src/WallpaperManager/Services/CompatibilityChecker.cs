using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// A regra de compatibilidade entre uma imagem e um monitor, num único lugar.
/// É usada para filtrar o catálogo, antes de baixar, depois de baixar e antes de aplicar.
/// </summary>
public static class CompatibilityChecker
{
    /// <summary>Diferença máxima de proporção, relativa à do monitor (1%).</summary>
    public const double AspectTolerance = 0.01;

    public static bool IsCompatible(int imageWidth, int imageHeight, MonitorInfo monitor)
    {
        return GetProblem(imageWidth, imageHeight, monitor.Width, monitor.Height) is null;
    }

    public static bool IsCompatible(int imageWidth, int imageHeight, int monitorWidth, int monitorHeight)
    {
        return GetProblem(imageWidth, imageHeight, monitorWidth, monitorHeight) is null;
    }

    /// <summary>
    /// Lança InvalidOperationException (com a explicação em português) se a imagem não servir no monitor.
    /// </summary>
    public static void EnsureCompatible(int imageWidth, int imageHeight, MonitorInfo monitor)
    {
        string? problem = GetProblem(imageWidth, imageHeight, monitor.Width, monitor.Height);
        if (problem is not null)
        {
            throw new InvalidOperationException(problem);
        }
    }

    /// <summary>
    /// Classifica a compatibilidade. Compatível = proporção igual à do monitor (tolerância de 1%) E largura
    /// e altura iguais ou maiores que as do monitor (assim a imagem nunca precisa ser esticada).
    /// O tamanho é verificado antes da proporção, então "TooSmall" tem prioridade sobre "WrongRatio".
    /// </summary>
    public static CompatibilityResult Classify(int imageWidth, int imageHeight, int monitorWidth, int monitorHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || monitorWidth <= 0 || monitorHeight <= 0)
        {
            return CompatibilityResult.Unknown;
        }

        if (imageWidth < monitorWidth || imageHeight < monitorHeight)
        {
            return CompatibilityResult.TooSmall;
        }

        double imageRatio = (double)imageWidth / imageHeight;
        double monitorRatio = (double)monitorWidth / monitorHeight;
        return Math.Abs(imageRatio - monitorRatio) / monitorRatio > AspectTolerance
            ? CompatibilityResult.WrongRatio
            : CompatibilityResult.Compatible;
    }

    /// <summary>
    /// Devolve null se for compatível; senão, o motivo em português.
    /// </summary>
    public static string? GetProblem(int imageWidth, int imageHeight, int monitorWidth, int monitorHeight)
    {
        return Classify(imageWidth, imageHeight, monitorWidth, monitorHeight) switch
        {
            CompatibilityResult.Compatible => null,
            CompatibilityResult.TooSmall =>
                $"A imagem tem {imageWidth}×{imageHeight}, menor que a resolução do monitor " +
                $"({monitorWidth}×{monitorHeight}); ficaria esticada e borrada.",
            CompatibilityResult.WrongRatio =>
                $"A proporção da imagem ({(double)imageWidth / imageHeight:0.000}) é diferente da do monitor " +
                $"({(double)monitorWidth / monitorHeight:0.000}); apareceriam bordas ou cortes.",
            _ => "Não foi possível determinar a resolução da imagem.",
        };
    }
}

/// <summary>
/// Resultado da regra de compatibilidade entre uma imagem e um monitor.
/// </summary>
public enum CompatibilityResult
{
    Compatible,
    TooSmall,
    WrongRatio,
    Unknown,
}
