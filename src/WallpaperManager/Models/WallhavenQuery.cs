namespace WallpaperManager.Models;

/// <summary>
/// Os parâmetros de uma busca no Wallhaven. Como é um record, dois pedidos iguais são "iguais",
/// o que ajuda a reconhecer buscas repetidas.
/// </summary>
/// <param name="MinWidth">Largura mínima (parâmetro atleast).</param>
/// <param name="MinHeight">Altura mínima (parâmetro atleast).</param>
/// <param name="Ratio">Proporção padrão do Wallhaven, ex.: 16x9.</param>
/// <param name="Sorting">date_added, relevance, views, favorites ou toplist.</param>
/// <param name="Categories">Três dígitos geral/anime/pessoas, ex.: 111.</param>
/// <param name="Purity">Três dígitos sfw/sketchy/nsfw, ex.: 100 (somente SFW).</param>
/// <param name="Page">Número da página, a partir de 1.</param>
public sealed record WallhavenQuery(
    int MinWidth,
    int MinHeight,
    string Ratio,
    string Sorting,
    string Categories,
    string Purity,
    int Page);
