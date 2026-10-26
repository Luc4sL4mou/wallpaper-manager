namespace WallpaperManager.Models;

/// <summary>
/// Um papel de parede do catálogo do Wallhaven.
/// </summary>
/// <param name="Id">Identificador no Wallhaven (ex.: d8vvlo).</param>
/// <param name="PageUrl">Página do papel no site.</param>
/// <param name="ImageUrl">Link da imagem original, usado no download (etapa 4).</param>
/// <param name="ThumbnailUrl">Miniatura usada no catálogo.</param>
/// <param name="Width">Largura em pixels informada pela API.</param>
/// <param name="Height">Altura em pixels informada pela API.</param>
/// <param name="FileSize">Tamanho do arquivo em bytes.</param>
public sealed record WallpaperItem(
    string Id,
    string PageUrl,
    string ImageUrl,
    string ThumbnailUrl,
    int Width,
    int Height,
    long FileSize)
{
    public string ResolutionText => $"{Width} × {Height}";

    public string FileSizeText => $"{FileSize / 1024.0 / 1024.0:0.0} MB";
}
