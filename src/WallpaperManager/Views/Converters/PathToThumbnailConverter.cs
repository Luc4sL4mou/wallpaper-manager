using System.Globalization;
using System.IO;
using System.Windows.Data;
using WallpaperManager.Services;

namespace WallpaperManager.Views.Converters;

/// <summary>
/// Converte o caminho de um arquivo em uma miniatura leve para exibir na tela.
/// Fica na View porque imagem é assunto visual; o ViewModel só conhece o caminho.
/// </summary>
public sealed class PathToThumbnailConverter : IValueConverter
{
    /// <summary>Largura em pixels com que a imagem é decodificada (economiza memória com fotos 4K).</summary>
    public int DecodeWidth { get; set; } = 480;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return ThumbnailLoader.TryLoad(path, DecodeWidth);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
