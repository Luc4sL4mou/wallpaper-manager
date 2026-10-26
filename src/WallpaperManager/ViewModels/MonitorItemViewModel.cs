using System.IO;
using WallpaperManager.Models;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Um monitor como aparece na tela: os dados reais (Info), a posição do "bloco" no mapa
/// e o papel de parede atual. A lista inteira é recriada a cada atualização.
/// </summary>
public sealed class MonitorItemViewModel : ViewModelBase
{
    private string? _currentWallpaperPath;
    private LastWallpaper? _lastWallpaper;

    public MonitorItemViewModel(MonitorInfo info, int number)
    {
        Info = info;
        Number = number;
    }

    public MonitorInfo Info { get; }

    /// <summary>Número exibido no mapa (1, 2, 3...), da esquerda para a direita.</summary>
    public int Number { get; }

    // Posição e tamanho do bloco no mapa, já em escala
    public double TileLeft { get; init; }
    public double TileTop { get; init; }
    public double TileWidth { get; init; }
    public double TileHeight { get; init; }

    /// <summary>Se o IDesktopWallpaper reconhece o ID deste monitor (sem isso não dá para aplicar).</summary>
    public bool IsRecognizedByWindows { get; init; }

    public bool IsNotRecognizedByWindows => !IsRecognizedByWindows;

    /// <summary>Arquivo do papel de parede atual, ou null (cor sólida / desconhecido).</summary>
    public string? CurrentWallpaperPath
    {
        get => _currentWallpaperPath;
        set
        {
            if (SetProperty(ref _currentWallpaperPath, value))
            {
                OnPropertyChanged(nameof(CurrentWallpaperText));
            }
        }
    }

    public string CurrentWallpaperText => CurrentWallpaperPath ?? "Nenhum arquivo (cor sólida ou apresentação de slides)";

    /// <summary>Último papel que este app aplicou neste monitor (vem das configurações), ou null.</summary>
    public LastWallpaper? LastWallpaper
    {
        get => _lastWallpaper;
        set
        {
            if (SetProperty(ref _lastWallpaper, value))
            {
                OnPropertyChanged(nameof(HasLastWallpaper));
                OnPropertyChanged(nameof(LastWallpaperText));
            }
        }
    }

    public bool HasLastWallpaper => LastWallpaper is not null;

    /// <summary>A apresentação de slides deste monitor (guardada pelo SlideshowManager); null só em testes.</summary>
    public MonitorSlideshowViewModel? Slideshow { get; set; }

    public string LastWallpaperText
    {
        get
        {
            if (LastWallpaper is null)
            {
                return "Este app ainda não aplicou nenhum papel neste monitor.";
            }

            string text = $"{Path.GetFileName(LastWallpaper.ImagePath)} · aplicado em {LastWallpaper.AppliedAt:dd/MM/yyyy HH:mm}";
            return File.Exists(LastWallpaper.ImagePath) ? text : text + " · arquivo não existe mais";
        }
    }

    // Também é o nome que leitores de tela e automação de interface usam para este item
    public override string ToString() => $"Monitor {Number} · {Info.ResolutionText}";
}
