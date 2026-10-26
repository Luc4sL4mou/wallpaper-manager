using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WallpaperManager.Services;

/// <summary>
/// Cria miniaturas leves de arquivos de imagem. O resultado é "congelado" (Freeze), então pode ser
/// criado numa thread de segundo plano e usado na thread da interface.
/// </summary>
public static class ThumbnailLoader
{
    /// <param name="decodeWidth">Largura em pixels com que a imagem é decodificada (economiza memória com fotos 4K).</param>
    public static ImageSource? TryLoad(string path, int decodeWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.DecodePixelWidth = decodeWidth;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;              // lê tudo agora e solta o arquivo
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache; // sempre relê do disco
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            // Arquivo corrompido ou formato não suportado: só não mostra a miniatura
            return null;
        }
    }
}
