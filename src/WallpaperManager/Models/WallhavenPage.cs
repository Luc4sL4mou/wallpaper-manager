namespace WallpaperManager.Models;

/// <summary>
/// Uma página de resultados da busca no Wallhaven.
/// </summary>
/// <param name="Items">Papéis da página, exatamente como a API devolveu (ainda sem o filtro local).</param>
/// <param name="CurrentPage">Número desta página (começa em 1).</param>
/// <param name="LastPage">Total de páginas da busca.</param>
/// <param name="Total">Total de papéis encontrados em todas as páginas.</param>
public sealed record WallhavenPage(
    IReadOnlyList<WallpaperItem> Items,
    int CurrentPage,
    int LastPage,
    int Total);
