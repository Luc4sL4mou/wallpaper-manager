using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WallpaperManager.Services;

/// <summary>
/// Decide se uma imagem é "escura" o bastante para o Filtro de irritabilidade: pouca luz branca na tela.
/// Olha uma versão bem reduzida da imagem, então é rápido mesmo com fotos 4K.
/// </summary>
public static class BrightnessAnalyzer
{
    private const int SampleWidth = 96;

    /// <summary>Brilho médio máximo (0 = preto, 1 = branco).</summary>
    private const double MaxAverageLuma = 0.28;

    /// <summary>A partir deste brilho um pixel conta como "luz branca".</summary>
    private const double BrightLuma = 0.70;

    /// <summary>Fatia máxima da imagem com luz branca (tolera detalhes pequenos, como uma lua ou estrelas).</summary>
    private const double MaxBrightShare = 0.04;

    /// <summary>Verdadeiro se o arquivo é uma imagem escura; null se não foi possível ler.</summary>
    public static bool? IsDark(string path)
    {
        return IsDark(ThumbnailLoader.TryLoad(path, SampleWidth) as BitmapSource);
    }

    /// <summary>
    /// Verdadeiro se a imagem é escura; null se não há imagem. Funciona com qualquer tamanho, mas quanto menor
    /// a imagem, mais rápido (miniaturas já pequenas, como as do catálogo, podem ser medidas direto).
    /// </summary>
    public static bool? IsDark(BitmapSource? bitmap)
    {
        if (bitmap is null)
        {
            return null;
        }

        try
        {
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr32, null, 0);
            int width = converted.PixelWidth;
            int height = converted.PixelHeight;
            int total = width * height;
            if (total == 0)
            {
                return null;
            }

            int stride = width * 4;
            var pixels = new byte[stride * height];
            converted.CopyPixels(pixels, stride, 0);

            double sum = 0;
            int bright = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                // Bgr32: azul, verde, vermelho, sem uso. Pesos de luminância do sRGB (Rec. 709)
                double luma = (0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2]) / 255.0;
                sum += luma;
                if (luma >= BrightLuma)
                {
                    bright++;
                }
            }

            return sum / total <= MaxAverageLuma && (double)bright / total <= MaxBrightShare;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
