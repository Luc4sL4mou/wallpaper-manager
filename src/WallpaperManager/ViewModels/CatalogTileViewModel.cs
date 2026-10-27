using WallpaperManager.Models;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Uma miniatura do catálogo: o papel do Wallhaven mais a informação "já está baixado?", que muda
/// com a tela aberta (depois de um download, ou quando a pasta de downloads é trocada).
/// </summary>
public sealed class CatalogTileViewModel : ViewModelBase
{
    private bool _isDownloaded;

    public CatalogTileViewModel(
        WallpaperItem item, bool isDownloaded, Action<CatalogTileViewModel> preview, Action<CatalogTileViewModel> download,
        Func<bool> canDownload)
    {
        Item = item;
        _isDownloaded = isDownloaded;
        PreviewCommand = new RelayCommand(() => preview(this));
        DownloadCommand = new RelayCommand(() => download(this), canDownload);
    }

    public WallpaperItem Item { get; }

    /// <summary>Micro botão "olho": abre a visualização em tela cheia.</summary>
    public RelayCommand PreviewCommand { get; }

    /// <summary>Micro botão de download: baixa este papel para a pasta, sem aplicar.</summary>
    public RelayCommand DownloadCommand { get; }

    public string ThumbnailUrl => Item.ThumbnailUrl;

    public string ResolutionText => Item.ResolutionText;

    /// <summary>Verdadeiro se o arquivo deste papel já existe na pasta de downloads.</summary>
    public bool IsDownloaded
    {
        get => _isDownloaded;
        set
        {
            if (SetProperty(ref _isDownloaded, value))
            {
                OnPropertyChanged(nameof(IsNotDownloaded));
            }
        }
    }

    public bool IsNotDownloaded => !IsDownloaded;
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
