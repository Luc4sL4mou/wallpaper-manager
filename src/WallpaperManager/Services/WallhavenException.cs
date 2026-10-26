namespace WallpaperManager.Services;

/// <summary>
/// Falha ao consultar o Wallhaven. A mensagem já vem em português e pronta para ser exibida.
/// </summary>
public sealed class WallhavenException : Exception
{
    public WallhavenException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
