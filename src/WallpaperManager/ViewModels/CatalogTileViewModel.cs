using WallpaperManager.Models;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Uma miniatura do catálogo: o papel do Wallhaven mais a informação "já está baixado?", que muda
/// com a tela aberta (depois de um download, ou quando a pasta de downloads é trocada).
/// </summary>
public sealed class CatalogTileViewModel : ViewModelBase
{
    private bool _isDownloaded;

    public CatalogTileViewModel(WallpaperItem item, bool isDownloaded)
    {
        Item = item;
        _isDownloaded = isDownloaded;
    }

    public WallpaperItem Item { get; }

    public string ThumbnailUrl => Item.ThumbnailUrl;

    public string ResolutionText => Item.ResolutionText;

    /// <summary>Verdadeiro se o arquivo deste papel já existe na pasta de downloads.</summary>
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set => SetProperty(ref _isDownloaded, value);
    }
}

/// <summary>
/// Um botão do paginador do catálogo: um número de página ou as reticências entre blocos de páginas.
/// </summary>
public sealed class PageButtonViewModel
{
    public PageButtonViewModel(string label, bool isCurrent, bool isGap, RelayCommand? command)
    {
        Label = label;
        IsCurrent = isCurrent;
        IsGap = isGap;
        Command = command;
    }

    public string Label { get; }

    public bool IsCurrent { get; }

    public bool IsGap { get; }

    public bool IsOtherNumber => !IsGap && !IsCurrent;

    public RelayCommand? Command { get; }
}
