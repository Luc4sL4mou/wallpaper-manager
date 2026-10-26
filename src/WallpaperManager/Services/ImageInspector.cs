using System.IO;
using System.Windows.Media.Imaging;

namespace WallpaperManager.Services;

/// <summary>
/// Lê a resolução real de um arquivo de imagem sem decodificar todos os pixels.
/// Será reaproveitado na etapa 4 para validar a compatibilidade antes de aplicar.
/// </summary>
public static class ImageInspector
{
    public static (int Width, int Height) ReadSize(string path)
    {
        using FileStream stream = File.OpenRead(path);

        // DelayCreation: lê só o cabeçalho; None: não guarda cache em memória
        BitmapDecoder decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        BitmapFrame frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }
}
