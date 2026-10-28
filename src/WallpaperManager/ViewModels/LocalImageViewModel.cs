using System.Windows.Media;
using WallpaperManager.Models;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Uma imagem da pasta local como aparece na tela. A miniatura é carregada depois, em segundo plano,
/// por isso a propriedade avisa a tela quando fica pronta. A caixa de marcação indica se a imagem
/// faz parte da apresentação do monitor selecionado.
/// </summary>
public sealed class LocalImageViewModel : ViewModelBase
{
    private readonly Action<LocalImageViewModel, bool> _checkedChanged;
    private ImageSource? _thumbnail;
    private bool _isChecked;
    private bool _isMarkingEnabled;

    public LocalImageViewModel(
        LocalImage image, bool isChecked, bool isMarkingEnabled, Action<LocalImageViewModel, bool> checkedChanged)
    {
        Image = image;
        _isChecked = isChecked;
        _isMarkingEnabled = isMarkingEnabled;
        _checkedChanged = checkedChanged;
    }

    public LocalImage Image { get; }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    /// <summary>Marcada para a apresentação do monitor selecionado (o usuário clica na caixa).</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
            {
                _checkedChanged(this, value);
            }
        }
    }

    /// <summary>Falso quando a apresentação usa a pasta inteira: aí as marcações não importam.</summary>
    public bool IsMarkingEnabled
    {
        get => _isMarkingEnabled;
        set
        {
            if (SetProperty(ref _isMarkingEnabled, value))
            {
                OnPropertyChanged(nameof(MarkingTooltip));
            }
        }
    }

    public string MarkingTooltip => "Incluir na apresentação de slides deste monitor (desmarque para tirar esta imagem)";

    /// <summary>Muda a marcação na tela sem avisar quem guarda a seleção (usado por "Marcar todas" e "Limpar").</summary>
    public void SetCheckedSilently(bool isChecked)
    {
        _isChecked = isChecked;
        OnPropertyChanged(nameof(IsChecked));
    }

    // Também é o nome que leitores de tela e automação de interface usam para este item
    public override string ToString() => $"{Image.FileName} · {Image.ResolutionText}";
}
