using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using WallpaperManager.Models;
using WallpaperManager.Services;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Aba Pasta local: usa uma pasta do computador como fonte de papéis de parede, mostrando só as imagens
/// compatíveis com o monitor selecionado (mesma regra do catálogo, com a resolução real de cada arquivo).
/// </summary>
public sealed class LocalFolderViewModel : ViewModelBase
{
    private const int BatchSize = 60; // quantas miniaturas entram de cada vez enquanto o usuário rola
    private const int ThumbnailDecodeWidth = 420;

    private readonly LocalFolderService _folderService;
    private readonly WallpaperFader _wallpaperFader; // aplica com o fade para preto
    private readonly SettingsService _settingsService;
    private readonly SlideshowManager _slideshows;
    private readonly IReadOnlyList<MonitorItemViewModel> _allMonitors;

    private MonitorSlideshowViewModel? _currentSlideshow;
    private bool _isStartingSlideshow;
    private string _slideshowMessage = string.Empty;
    private string _markAllText = "Marcar todas";
    private string _slideshowHint = string.Empty;

    private MonitorItemViewModel? _monitor;
    private bool _isActive;
    private bool _needsScan;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _thumbnailsCts;
    private int _scanId;

    private string? _selectedFolder;
    private bool _includeSubfolders;
    private LocalScanResult? _scan;
    private IReadOnlyList<LocalImage> _sizeCompatible = Array.Empty<LocalImage>(); // compatíveis com o monitor
    private IReadOnlyList<LocalImage> _compatible = Array.Empty<LocalImage>();     // as que aparecem na tela (após o filtro de brilho)
    private int _wrongRatio;
    private int _tooSmall;

    private bool _isAnalyzing;
    private double _analysisProgress;
    private string _analysisText = string.Empty;
    private CancellationTokenSource? _analysisCts;
    private int _analysisId;

    private bool _isScanning;
    private double _scanProgress;
    private string _scanText = string.Empty;
    private string _summary = string.Empty;
    private string? _errorMessage;
    private string _emptyText = string.Empty;

    private IReadOnlyList<LocalImageViewModel> _items = Array.Empty<LocalImageViewModel>();
    private LocalImageViewModel? _selectedItem;
    private string _selectedItemInfo = string.Empty;
    private int _shownCount;

    private IReadOnlyList<MonitorItemViewModel> _applyTargets = Array.Empty<MonitorItemViewModel>();
    private MonitorItemViewModel? _selectedTarget;
    private bool _isApplying;
    private string _applyMessage = string.Empty;

    public LocalFolderViewModel(
        LocalFolderService folderService,
        WallpaperFader wallpaperFader,
        SettingsService settingsService,
        SlideshowManager slideshowManager,
        IReadOnlyList<MonitorItemViewModel> allMonitors)
    {
        _folderService = folderService;
        _wallpaperFader = wallpaperFader;
        _settingsService = settingsService;
        _slideshows = slideshowManager;
        _allMonitors = allMonitors;

        // Estado inicial a partir das configurações (nos campos: não dispara análise nem gravação)
        AppSettings saved = settingsService.Current;
        foreach (string folder in saved.LocalFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Folders.Add(folder);
        }
        _selectedFolder = Folders.FirstOrDefault(f => string.Equals(f, saved.SelectedLocalFolder, StringComparison.OrdinalIgnoreCase))
                          ?? Folders.FirstOrDefault();
        _includeSubfolders = saved.LocalFolderIncludeSubfolders;
        _needsScan = _selectedFolder is not null;
        UpdateEmptyText();

        AddFolderCommand = new RelayCommand(AddFolder);
        RemoveFolderCommand = new RelayCommand(RemoveFolder, () => SelectedFolder is not null);
        RescanCommand = new RelayCommand(() => _ = ScanAsync(), () => !IsScanning && SelectedFolder is not null);
        CancelScanCommand = new RelayCommand(() => _scanCts?.Cancel(), () => IsScanning);
        CancelAnalysisCommand = new RelayCommand(() => _analysisCts?.Cancel(), () => IsAnalyzing);
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), CanApply);

        MarkAllCommand = new RelayCommand(
            MarkAll, () => CurrentSlideshow is { IsStopped: true, UseWholeFolder: false } && _compatible.Count > 0 && !IsAnalyzing);
        ClearMarksCommand = new RelayCommand(ClearMarks, () => CurrentSlideshow is { IsStopped: true, UseWholeFolder: false, MarkedCount: > 0 });
        StartSlideshowCommand = new RelayCommand(() => _ = StartSlideshowAsync(), CanStartSlideshow);
    }

    /// <summary>
    /// A apresentação do monitor selecionado (a configuração é por monitor: ao trocar de monitor, troca também).
    /// </summary>
    public MonitorSlideshowViewModel? CurrentSlideshow
    {
        get => _currentSlideshow;
        private set
        {
            MonitorSlideshowViewModel? old = _currentSlideshow;
            if (SetProperty(ref _currentSlideshow, value))
            {
                if (old is not null)
                {
                    old.PropertyChanged -= OnSlideshowPropertyChanged;
                }
                if (value is not null)
                {
                    value.PropertyChanged += OnSlideshowPropertyChanged;
                }
                RefreshMarkingState();
            }
        }
    }

    /// <summary>Aviso sobre o último "Iniciar" (ex.: imagens marcadas que não puderam ser usadas).</summary>
    public string SlideshowMessage
    {
        get => _slideshowMessage;
        private set => SetProperty(ref _slideshowMessage, value);
    }

    public string MarkAllText
    {
        get => _markAllText;
        private set => SetProperty(ref _markAllText, value);
    }

    /// <summary>
    /// Por que o botão Iniciar está desabilitado (ou um problema com o tempo). Vazio quando está tudo certo.
    /// Mostrado ao lado dos botões: um botão cinza nunca deve ficar sem explicação.
    /// </summary>
    public string SlideshowHint
    {
        get => _slideshowHint;
        private set
        {
            if (SetProperty(ref _slideshowHint, value))
            {
                OnPropertyChanged(nameof(HasSlideshowHint));
            }
        }
    }

    public bool HasSlideshowHint => SlideshowHint.Length > 0;

    private void RefreshSlideshowHint()
    {
        MonitorSlideshowViewModel? slideshow = CurrentSlideshow;

        if (slideshow is null || _monitor is null)
        {
            SlideshowHint = string.Empty;
        }
        else if (slideshow.HasIntervalProblem)
        {
            SlideshowHint = slideshow.IntervalProblem + (slideshow.IsRunning
                ? " A apresentação segue com o último tempo válido."
                : " Corrija o tempo para poder iniciar.");
        }
        else if (slideshow.IsRunning)
        {
            SlideshowHint = string.Empty;
        }
        else if (IsScanning || IsAnalyzing)
        {
            SlideshowHint = "Aguarde a análise da pasta terminar para poder iniciar.";
        }
        else if (slideshow.UseWholeFolder && _sizeCompatible.Count == 0)
        {
            SlideshowHint = "Nenhuma imagem desta pasta é compatível com este monitor, então não há o que apresentar.";
        }
        else if (slideshow.UseWholeFolder && IncludedCompatibleCount() == 0)
        {
            SlideshowHint = "Todas as imagens desta pasta estão desmarcadas: marque pelo menos uma para iniciar.";
        }
        else if (!slideshow.UseWholeFolder && slideshow.MarkedCount == 0)
        {
            SlideshowHint = "Para iniciar: marque pelo menos uma imagem (caixinha no canto da miniatura) ou ligue \"Usar a pasta inteira\".";
        }
        else
        {
            SlideshowHint = string.Empty;
        }
    }

    public RelayCommand MarkAllCommand { get; }

    public RelayCommand ClearMarksCommand { get; }

    public RelayCommand StartSlideshowCommand { get; }

    /// <summary>Disparado depois que uma imagem da pasta foi aplicada com sucesso em um monitor.</summary>
    public event EventHandler<WallpaperAppliedEventArgs>? WallpaperApplied;

    public ObservableCollection<string> Folders { get; } = new();

    public string? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (SetProperty(ref _selectedFolder, value))
            {
                SaveSettings();
                ClearResults();
                _needsScan = value is not null;
                RelayCommand.RaiseCanExecuteChanged();
                ScanIfNeeded();
            }
        }
    }

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (SetProperty(ref _includeSubfolders, value))
            {
                SaveSettings();
                ClearResults();
                _needsScan = SelectedFolder is not null;
                ScanIfNeeded();
            }
        }
    }

    /// <summary>
    /// Filtro de irritabilidade: só imagens escuras (pouca luz branca na tela). É um só para todos os monitores:
    /// vale para a lista, para "Marcar todas" e para as apresentações de slides de qualquer monitor, inclusive
    /// as que já estão rodando e as retomadas ao abrir o app (quem guarda o estado é o SlideshowManager).
    /// </summary>
    public bool IrritabilityFilter
    {
        get => _slideshows.DarkOnly;
        set
        {
            if (value == _slideshows.DarkOnly)
            {
                return;
            }

            _ = _slideshows.SetDarkOnlyAsync(value); // grava e já muda o critério; o resto da troca é em segundo plano
            OnPropertyChanged();
            if (_scan is not null && _monitor is not null)
            {
                ApplyBrightnessFilter();
            }
        }
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
                RefreshSlideshowHint();
                UpdateEmptyText();
            }
        }
    }

    /// <summary>Progresso da medição de brilho, de 0 a 100.</summary>
    public double AnalysisProgress
    {
        get => _analysisProgress;
        private set => SetProperty(ref _analysisProgress, value);
    }

    public string AnalysisText
    {
        get => _analysisText;
        private set => SetProperty(ref _analysisText, value);
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
                RefreshSlideshowHint();
            }
        }
    }

    /// <summary>Progresso da análise, de 0 a 100.</summary>
    public double ScanProgress
    {
        get => _scanProgress;
        private set => SetProperty(ref _scanProgress, value);
    }

    public string ScanText
    {
        get => _scanText;
        private set => SetProperty(ref _scanText, value);
    }

    /// <summary>Resumo do filtro, ex.: "120 imagens · 40 compatíveis · ocultas: 70 com outra proporção...".</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => ErrorMessage is not null;

    /// <summary>Texto mostrado no lugar da lista quando não há nada para exibir.</summary>
    public string EmptyText
    {
        get => _emptyText;
        private set
        {
            if (SetProperty(ref _emptyText, value))
            {
                OnPropertyChanged(nameof(HasEmptyText));
            }
        }
    }

    public bool HasEmptyText => EmptyText.Length > 0;

    /// <summary>Imagens compatíveis já colocadas na tela (a lista cresce conforme o usuário rola).</summary>
    public IReadOnlyList<LocalImageViewModel> Items
    {
        get => _items;
        private set => SetProperty(ref _items, value);
    }

    public LocalImageViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (SetProperty(ref _selectedItem, value))
            {
                SelectedItemInfo = value is null
                    ? "Selecione uma imagem para ver os detalhes."
                    : $"{value.Image.FileName} · {value.Image.ResolutionText} · {value.Image.FileSizeText}";
                UpdateApplyTargets(value?.Image);
                if (!IsApplying)
                {
                    ApplyMessage = string.Empty;
                }
            }
        }
    }

    public string SelectedItemInfo
    {
        get => _selectedItemInfo;
        private set => SetProperty(ref _selectedItemInfo, value);
    }

    /// <summary>Monitores em que a imagem selecionada pode ser aplicada (compatíveis e reconhecidos pelo Windows).</summary>
    public IReadOnlyList<MonitorItemViewModel> ApplyTargets
    {
        get => _applyTargets;
        private set => SetProperty(ref _applyTargets, value);
    }

    public MonitorItemViewModel? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsApplying
    {
        get => _isApplying;
        private set
        {
            if (SetProperty(ref _isApplying, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ApplyMessage
    {
        get => _applyMessage;
        private set => SetProperty(ref _applyMessage, value);
    }

    public RelayCommand AddFolderCommand { get; }

    public RelayCommand RemoveFolderCommand { get; }

    public RelayCommand RescanCommand { get; }

    public RelayCommand CancelScanCommand { get; }

    public RelayCommand CancelAnalysisCommand { get; }

    public RelayCommand ApplyCommand { get; }

    /// <summary>
    /// Informa qual monitor está selecionado. Trocar de monitor só refiltra as imagens já analisadas.
    /// </summary>
    public void SetMonitor(MonitorItemViewModel? monitor)
    {
        bool sameMonitor = monitor is not null && _monitor is not null
                           && monitor.Info.DeviceId == _monitor.Info.DeviceId
                           && monitor.Info.Width == _monitor.Info.Width
                           && monitor.Info.Height == _monitor.Info.Height;

        _monitor = monitor;
        CurrentSlideshow = monitor is null ? null : _slideshows.GetOrCreate(monitor.Info.DeviceId);
        if (sameMonitor)
        {
            return; // a lista de monitores foi recriada, mas é o mesmo monitor
        }

        SlideshowMessage = string.Empty;
        ApplyFilter();
        RefreshSlideshowHint();
    }

    private void OnSlideshowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorSlideshowViewModel.UseWholeFolder) or nameof(MonitorSlideshowViewModel.IsRunning)
            or nameof(MonitorSlideshowViewModel.ExcludedCount))
        {
            RefreshMarkingState();
        }

        RelayCommand.RaiseCanExecuteChanged();
        RefreshSlideshowHint();
    }

    /// <summary>
    /// Atualiza as caixas de marcação da página para o monitor selecionado. Com a pasta inteira, todas as
    /// imagens aparecem marcadas (todas entram); com a apresentação em andamento, as caixas ficam travadas.
    /// </summary>
    private void RefreshMarkingState()
    {
        foreach (LocalImageViewModel item in Items)
        {
            item.SetCheckedSilently(IsIncluded(item.Image.Path));
            item.IsMarkingEnabled = CanEditMarks();
        }

        RelayCommand.RaiseCanExecuteChanged();
        RefreshSlideshowHint();
    }

    private bool IsIncluded(string path)
    {
        return CurrentSlideshow?.IsIncluded(path) == true;
    }

    // Sempre editável: com a pasta inteira, desmarcar exclui a imagem; com a apresentação rodando, vale na hora
    private bool CanEditMarks() => CurrentSlideshow is not null;

    private int IncludedCompatibleCount() => CurrentSlideshow is { } s ? _sizeCompatible.Count(i => s.IsIncluded(i.Path)) : 0;

    private void OnItemCheckedChanged(LocalImageViewModel item, bool isChecked)
    {
        CurrentSlideshow?.SetIncluded(item.Image.Path, isChecked);
    }

    /// <summary>Marca todas as imagens compatíveis da pasta (de todas as páginas) para este monitor.</summary>
    private void MarkAll()
    {
        CurrentSlideshow?.AddMarks(_compatible.Select(i => i.Path));
        RefreshMarkingState();
    }

    private void ClearMarks()
    {
        CurrentSlideshow?.ClearMarks();
        RefreshMarkingState();
    }

    private bool CanStartSlideshow()
    {
        MonitorSlideshowViewModel? slideshow = CurrentSlideshow;
        if (slideshow is null || _monitor is null || _isStartingSlideshow || slideshow.IsRunning || slideshow.IntervalSeconds == 0)
        {
            return false;
        }

        return slideshow.UseWholeFolder ? IncludedCompatibleCount() > 0 : slideshow.MarkedCount > 0;
    }

    /// <summary>
    /// Monta a lista deste monitor (as marcadas ou a pasta inteira), mantém só as compatíveis com ele e inicia.
    /// Todo erro é tratado aqui dentro, então é seguro chamar sem await.
    /// </summary>
    private async Task StartSlideshowAsync()
    {
        MonitorSlideshowViewModel? slideshow = CurrentSlideshow;
        MonitorItemViewModel? monitor = _monitor;
        if (slideshow is null || monitor is null)
        {
            return;
        }

        _isStartingSlideshow = true;
        RelayCommand.RaiseCanExecuteChanged();
        SlideshowMessage = string.Empty;
        try
        {
            // Pasta inteira: a análise que já está na tela (compatíveis com este monitor).
            // Marcadas: lê a resolução real de cada arquivo marcado, em qualquer pasta.
            IReadOnlyList<LocalImage> candidates = slideshow.UseWholeFolder
                ? _sizeCompatible
                : await _folderService.DescribeAsync(slideshow.MarkedPaths.ToList(), CancellationToken.None);

            // Última barreira antes de começar: só entra o que serve neste monitor
            List<LocalImage> usable = candidates
                .Where(i => CompatibilityChecker.IsCompatible(i.Width, i.Height, monitor.Info))
                .ToList();

            if (usable.Count == 0)
            {
                SlideshowMessage = "Nenhuma das imagens serve neste monitor (arquivos removidos ou com proporção diferente).";
                return;
            }

            // Filtro de irritabilidade: mede o brilho do que falta e confere se sobra alguma escura. A apresentação
            // recebe a lista inteira e ela mesma aplica o filtro, para voltar a usar as claras se ele for desligado.
            IReadOnlyList<LocalImage> dark = await _slideshows.KeepDarkIfFilteredAsync(usable);
            if (!dark.Any(i => slideshow.IsIncluded(i.Path)))
            {
                SlideshowMessage = "Nenhuma imagem escura entre as escolhidas: desligue o Filtro de irritabilidade ou marque imagens com a lua.";
                return;
            }

            int skipped = slideshow.UseWholeFolder ? 0 : slideshow.MarkedCount - usable.Count;
            if (skipped > 0)
            {
                SlideshowMessage = $"{skipped} imagem(ns) marcada(s) ficaram de fora: o arquivo não existe mais ou não serve neste monitor.";
            }

            await slideshow.StartAsync(usable, monitor.Info, SelectedFolder, IncludeSubfolders);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Erro inesperado ao iniciar a apresentação", ex);
            SlideshowMessage = $"Não foi possível iniciar a apresentação: {ex.Message} (detalhes no registro de erros).";
        }
        finally
        {
            _isStartingSlideshow = false;
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Chamado quando a aba é aberta ou fechada. A análise só começa com a aba aberta.
    /// </summary>
    public void SetActive(bool isActive)
    {
        _isActive = isActive;
        ScanIfNeeded();
    }

    private void ScanIfNeeded()
    {
        if (_isActive && _needsScan)
        {
            _ = ScanAsync();
        }
    }

    private void SaveSettings()
    {
        AppSettings settings = _settingsService.Current;
        settings.LocalFolders = Folders.ToList();
        settings.SelectedLocalFolder = SelectedFolder;
        settings.LocalFolderIncludeSubfolders = IncludeSubfolders;
        _settingsService.Save(); // se falhar, o erro vai para o registro e a escolha só não é lembrada
    }

    /// <summary>
    /// Abre o seletor de pasta do Windows e acrescenta a pasta escolhida à lista (compartilhada com a aba
    /// Apresentação). Devolve o caminho da pasta na lista, ou null se o usuário cancelou.
    /// </summary>
    public string? PickAndAddFolder()
    {
        // Para um MVVM "de livro" isto ficaria atrás de uma interface de diálogo;
        // aqui mantemos simples, como no seletor de arquivo da aba Monitores.
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha uma pasta com papéis de parede",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };

        if (dialog.ShowDialog() != true)
        {
            return null;
        }

        string folder = Path.GetFullPath(dialog.FolderName);
        string? existing = Folders.FirstOrDefault(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            Folders.Add(folder);
            existing = folder;
            SaveSettings();
        }

        return existing;
    }

    private void AddFolder()
    {
        string? existing = PickAndAddFolder();
        if (existing is null)
        {
            return;
        }

        if (string.Equals(SelectedFolder, existing, StringComparison.OrdinalIgnoreCase))
        {
            SaveSettings();
            _needsScan = true;
            ScanIfNeeded();
        }
        else
        {
            SelectedFolder = existing; // grava e analisa
        }
    }

    /// <summary>
    /// Tira a pasta da lista do app. Nenhum arquivo é apagado do computador.
    /// </summary>
    private void RemoveFolder()
    {
        string? folder = SelectedFolder;
        if (folder is null)
        {
            return;
        }

        Folders.Remove(folder); // a caixa de seleção pode zerar SelectedFolder sozinha
        SelectedFolder = Folders.FirstOrDefault();
        SaveSettings();
    }

    /// <summary>
    /// Zera tudo o que depende da análise: resultados, página, seleção, mensagens.
    /// </summary>
    private void ClearResults()
    {
        _scanCts?.Cancel();
        _thumbnailsCts?.Cancel();
        CancelAnalysis();
        _scan = null;
        _sizeCompatible = Array.Empty<LocalImage>();
        _compatible = Array.Empty<LocalImage>();
        MarkAllText = "Marcar todas";
        Items = Array.Empty<LocalImageViewModel>();
        SelectedItem = null;
        _shownCount = 0;
        ErrorMessage = null;
        Summary = string.Empty;
        IsScanning = false;
        UpdateEmptyText();
        RefreshSlideshowHint();
    }

    /// <summary>
    /// Analisa a pasta selecionada. Todo erro é tratado aqui dentro, então é seguro chamar sem await.
    /// </summary>
    private async Task ScanAsync()
    {
        string? folder = SelectedFolder;
        _needsScan = false;
        if (folder is null)
        {
            return;
        }

        ClearResults();
        _scanCts = new CancellationTokenSource();
        CancellationToken token = _scanCts.Token;
        int id = ++_scanId;

        IsScanning = true;
        ScanProgress = 0;
        ScanText = "Procurando imagens...";
        UpdateEmptyText();

        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                if (id == _scanId)
                {
                    ScanProgress = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                    ScanText = $"Lendo a resolução de cada imagem: {p.Done} de {p.Total}";
                }
            });

            LocalScanResult result = await _folderService.ScanAsync(folder, IncludeSubfolders, progress, token);
            if (id != _scanId)
            {
                return; // substituída por uma análise mais nova
            }

            _scan = result;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            if (id == _scanId)
            {
                Summary = "Análise cancelada.";
            }
        }
        catch (InvalidOperationException ex)
        {
            if (id == _scanId)
            {
                ErrorMessage = ex.Message;
            }
        }
        catch (Exception ex)
        {
            if (id == _scanId)
            {
                ErrorLog.Write($"Erro inesperado ao analisar a pasta {folder}", ex);
                ErrorMessage = $"Erro inesperado ao analisar a pasta: {ex.Message} (detalhes no registro de erros).";
            }
        }
        finally
        {
            if (id == _scanId)
            {
                IsScanning = false;
                UpdateEmptyText();
            }
        }
    }

    /// <summary>
    /// Separa as imagens já analisadas em compatíveis e ocultas para o monitor atual, sem tocar no disco.
    /// </summary>
    private void ApplyFilter()
    {
        _thumbnailsCts?.Cancel();
        SelectedItem = null;

        if (_scan is null || _monitor is null)
        {
            CancelAnalysis();
            _sizeCompatible = Array.Empty<LocalImage>();
            _compatible = Array.Empty<LocalImage>();
            Items = Array.Empty<LocalImageViewModel>();
            _shownCount = 0;
            UpdateEmptyText();
            return;
        }

        MonitorInfo info = _monitor.Info;
        var compatible = new List<LocalImage>();
        int wrongRatio = 0, tooSmall = 0;

        foreach (LocalImage image in _scan.Images)
        {
            switch (CompatibilityChecker.Classify(image.Width, image.Height, info.Width, info.Height))
            {
                case CompatibilityResult.Compatible:
                    compatible.Add(image);
                    break;
                case CompatibilityResult.TooSmall:
                    tooSmall++;
                    break;
                default:
                    wrongRatio++;
                    break;
            }
        }

        _sizeCompatible = compatible;
        _wrongRatio = wrongRatio;
        _tooSmall = tooSmall;
        ApplyBrightnessFilter();
    }

    /// <summary>
    /// Segunda etapa do filtro: com o Filtro de irritabilidade ligado, mede o brilho das imagens compatíveis
    /// (só as que ainda não foram medidas) e deixa na tela apenas as escuras.
    /// </summary>
    private void ApplyBrightnessFilter()
    {
        _thumbnailsCts?.Cancel();
        SelectedItem = null;
        CancelAnalysis();

        List<LocalImage> pending = _sizeCompatible.Where(i => !_folderService.TryGetIsDark(i, out _)).ToList();

        if (!IrritabilityFilter)
        {
            // Filtro desligado: a lista aparece já, e o brilho é medido por trás só para mostrar a lua nas escuras
            ShowCompatible(_sizeCompatible, brightHidden: 0);
            if (pending.Count > 0)
            {
                _ = AnalyzeBrightnessAsync(pending, blocking: false);
            }
            return;
        }

        if (pending.Count == 0)
        {
            ShowDarkOnly();
            return;
        }

        _ = AnalyzeBrightnessAsync(pending, blocking: true);
    }

    // Atualiza a lua das miniaturas que já estão na tela com o que a medição já descobriu
    private void RefreshMoons()
    {
        foreach (LocalImageViewModel item in Items)
        {
            item.IsDark = _folderService.TryGetIsDark(item.Image, out bool isDark) && isDark;
        }
    }

    private void ShowDarkOnly()
    {
        List<LocalImage> dark = _sizeCompatible.Where(i => _folderService.TryGetIsDark(i, out bool isDark) && isDark).ToList();
        ShowCompatible(dark, _sizeCompatible.Count - dark.Count);
    }

    private void CancelAnalysis()
    {
        _analysisCts?.Cancel();
        _analysisId++; // o resultado de uma medição cancelada ou substituída é ignorado
        IsAnalyzing = false;
    }

    /// <summary>
    /// Mede o brilho em segundo plano. Todo erro é tratado aqui dentro, então é seguro chamar sem await.
    /// Com blocking (filtro ligado) mostra progresso e, se o usuário cancelar ou der erro, o filtro é desligado
    /// e a lista volta a mostrar tudo. Sem blocking, é uma medição discreta (poucas leituras por vez) que só
    /// vai acendendo a lua nas miniaturas.
    /// </summary>
    private async Task AnalyzeBrightnessAsync(List<LocalImage> pending, bool blocking)
    {
        _analysisCts = new CancellationTokenSource();
        CancellationToken token = _analysisCts.Token;
        int id = ++_analysisId;

        if (blocking)
        {
            AnalysisProgress = 0;
            AnalysisText = $"Medindo o brilho das imagens: 0 de {pending.Count}";
            IsAnalyzing = true;
        }

        try
        {
            var progress = new Progress<(int Done, int Total)>(p =>
            {
                if (id != _analysisId)
                {
                    return;
                }

                RefreshMoons();
                if (blocking)
                {
                    AnalysisProgress = p.Total == 0 ? 0 : p.Done * 100.0 / p.Total;
                    AnalysisText = $"Medindo o brilho das imagens: {p.Done} de {p.Total}";
                }
            });

            await _folderService.AnalyzeBrightnessAsync(pending, blocking ? 4 : 2, progress, token);
            if (id != _analysisId)
            {
                return;
            }

            if (blocking)
            {
                IsAnalyzing = false;
                ShowDarkOnly();
            }
            RefreshMoons();
        }
        catch (OperationCanceledException)
        {
            if (id == _analysisId && blocking)
            {
                DisableFilterAfterFailure(null);
            }
        }
        catch (Exception ex)
        {
            if (id == _analysisId)
            {
                ErrorLog.Write("Erro inesperado ao medir o brilho das imagens", ex);
                if (blocking)
                {
                    DisableFilterAfterFailure($"Não foi possível medir o brilho: {ex.Message} (detalhes no registro de erros).");
                }
            }
        }
    }

    private void DisableFilterAfterFailure(string? message)
    {
        IsAnalyzing = false;
        _ = _slideshows.SetDarkOnlyAsync(false); // desliga em todos os monitores
        OnPropertyChanged(nameof(IrritabilityFilter));
        ShowCompatible(_sizeCompatible, brightHidden: 0);
        if (message is not null)
        {
            Summary += " · " + message;
        }
    }

    /// <summary>
    /// Coloca na tela as imagens escolhidas pelos filtros e refaz o resumo. Não toca no disco.
    /// </summary>
    private void ShowCompatible(IReadOnlyList<LocalImage> shown, int brightHidden)
    {
        if (_scan is null || _monitor is null)
        {
            return;
        }

        MonitorInfo info = _monitor.Info;
        _compatible = shown;
        MarkAllText = shown.Count > 0 ? $"Marcar todas ({shown.Count})" : "Marcar todas";
        ShowFirstBatch();

        var hidden = new List<string>();
        if (_wrongRatio > 0)
        {
            hidden.Add($"{_wrongRatio} com outra proporção");
        }
        if (_tooSmall > 0)
        {
            hidden.Add($"{_tooSmall} menor(es) que o monitor");
        }
        if (brightHidden > 0)
        {
            hidden.Add($"{brightHidden} clara(s) pelo filtro de irritabilidade");
        }

        var notes = new List<string>();
        if (_scan.Unreadable > 0)
        {
            notes.Add($"{_scan.Unreadable} ilegível(is)");
        }
        if (_scan.CloudOnly > 0)
        {
            notes.Add($"{_scan.CloudOnly} só na nuvem (ignoradas para não serem baixadas)");
        }
        if (_scan.Truncated)
        {
            notes.Add($"limite de {LocalFolderService.MaxImages} imagens atingido");
        }

        Summary = $"{_scan.Images.Count} imagem(ns) na pasta · {_sizeCompatible.Count} compatível(is) com o monitor {_monitor.Number} ({info.ResolutionText})" +
                  (hidden.Count > 0 ? $" · ocultas: {string.Join(", ", hidden)}" : "") +
                  (notes.Count > 0 ? $" · {string.Join(" · ", notes)}" : "");
        UpdateEmptyText();
        RelayCommand.RaiseCanExecuteChanged();
        RefreshSlideshowHint();
    }

    // Recomeça a lista com o primeiro lote; os seguintes entram por LoadMore, conforme o usuário rola
    private void ShowFirstBatch()
    {
        _thumbnailsCts?.Cancel();
        _thumbnailsCts = new CancellationTokenSource();
        _shownCount = 0;
        Items = new ObservableCollection<LocalImageViewModel>();
        LoadMore();
    }

    /// <summary>
    /// Rolagem infinita: a tela chama isto quando a rolagem chega perto do fim. Acrescenta o próximo lote
    /// de imagens (sem paginação) e carrega as miniaturas dele em segundo plano.
    /// </summary>
    public void LoadMore()
    {
        if (Items is not ObservableCollection<LocalImageViewModel> shown || _shownCount >= _compatible.Count
            || _thumbnailsCts is null)
        {
            return;
        }

        var batch = _compatible.Skip(_shownCount).Take(BatchSize)
            .Select(i => new LocalImageViewModel(
                i, isChecked: IsIncluded(i.Path), isMarkingEnabled: CanEditMarks(), OnItemCheckedChanged,
                isDark: _folderService.TryGetIsDark(i, out bool isDark) && isDark))
            .ToList();
        _shownCount += batch.Count;
        foreach (LocalImageViewModel item in batch)
        {
            shown.Add(item);
        }

        RelayCommand.RaiseCanExecuteChanged();
        _ = LoadThumbnailsAsync(batch, _thumbnailsCts.Token);
    }

    /// <summary>
    /// Carrega as miniaturas de um lote em segundo plano (poucas por vez), para a tela não travar.
    /// O token é o da lista atual: trocar de monitor ou de pasta cancela todos os lotes.
    /// </summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<LocalImageViewModel> items, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(4);
        IEnumerable<Task> tasks = items.Select(async item =>
        {
            await gate.WaitAsync(token);
            try
            {
                var thumbnail = await Task.Run(() => ThumbnailLoader.TryLoad(item.Image.Path, ThumbnailDecodeWidth), token);
                if (!token.IsCancellationRequested)
                {
                    item.Thumbnail = thumbnail;
                }
            }
            finally
            {
                gate.Release();
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Trocou de pasta ou de monitor: as miniaturas antigas não importam mais
        }
    }

    private void UpdateEmptyText()
    {
        if (IsScanning || IsAnalyzing || HasError)
        {
            EmptyText = string.Empty;
        }
        else if (Folders.Count == 0)
        {
            EmptyText = "Nenhuma pasta adicionada ainda.\nClique em \"Adicionar pasta…\" e escolha uma pasta com seus papéis de parede.";
        }
        else if (_scan is not null && _sizeCompatible.Count == 0 && _monitor is not null)
        {
            EmptyText = "Nenhuma imagem desta pasta é compatível com este monitor.\nO resumo acima mostra quantas foram ocultas e por quê.";
        }
        else if (_scan is not null && _compatible.Count == 0 && _monitor is not null)
        {
            EmptyText = "Nenhuma imagem escura nesta seleção.\nDesligue o Filtro de irritabilidade para ver todas as compatíveis.";
        }
        else
        {
            EmptyText = string.Empty;
        }
    }

    private void UpdateApplyTargets(LocalImage? image)
    {
        ApplyTargets = image is null
            ? Array.Empty<MonitorItemViewModel>()
            : _allMonitors
                .Where(m => m.IsRecognizedByWindows && CompatibilityChecker.IsCompatible(image.Width, image.Height, m.Info))
                .ToList();

        // Sugere o monitor do filtro; o usuário pode trocar por outro compatível
        SelectedTarget = ApplyTargets.FirstOrDefault(m => m.Info.DeviceId == _monitor?.Info.DeviceId)
                         ?? ApplyTargets.FirstOrDefault();
    }

    private bool CanApply() => !IsApplying && SelectedItem is not null && SelectedTarget is not null;

    /// <summary>
    /// Aplica a imagem escolhida no monitor de destino. O WallpaperService valida a regra de novo,
    /// com a resolução real do arquivo, antes de chamar o Windows.
    /// </summary>
    private async Task ApplyAsync()
    {
        LocalImage? image = SelectedItem?.Image;

        // Procura de novo pelo ID: a lista de monitores pode ter sido recriada desde a seleção
        MonitorItemViewModel? target = _allMonitors.FirstOrDefault(m => m.Info.DeviceId == SelectedTarget?.Info.DeviceId);
        if (image is null || target is null)
        {
            ApplyMessage = "Escolha uma imagem e um monitor de destino (o monitor pode ter sido desconectado).";
            return;
        }

        IsApplying = true;
        ApplyMessage = $"Aplicando no monitor {target.Number}...";
        try
        {
            await _wallpaperFader.ApplyAsync(target.Info, image.Path);
            ApplyMessage = $"{image.FileName} aplicada no monitor {target.Number} ({target.Info.ResolutionText}).";
            WallpaperApplied?.Invoke(this, new WallpaperAppliedEventArgs(target, image.Path, WallhavenId: null));
        }
        catch (Exception ex)
        {
            if (ex is not (InvalidOperationException or FileNotFoundException))
            {
                ErrorLog.Write($"Erro inesperado ao aplicar {image.Path}", ex);
            }
            ApplyMessage = $"Não foi possível aplicar: {ex.Message}";
        }
        finally
        {
            IsApplying = false;
        }
    }
}
