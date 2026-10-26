namespace WallpaperManager.Services;

/// <summary>
/// Falha ao baixar uma imagem. A mensagem já vem em português e pronta para ser exibida.
/// </summary>
public sealed class DownloadException : Exception
{
    public DownloadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
