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
    private const int PageSize = 48;
    private const int ThumbnailDecodeWidth = 420;

    private readonly LocalFolderService _folderService;
    private readonly WallpaperService _wallpaperService;
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
    private IReadOnlyList<LocalImage> _compatible = Array.Empty<LocalImage>();

    private bool _isScanning;
    private double _scanProgress;
    private string _scanText = string.Empty;
    private string _summary = string.Empty;
    private string? _errorMessage;
    private string _emptyText = string.Empty;

    private IReadOnlyList<LocalImageViewModel> _items = Array.Empty<LocalImageViewModel>();
    private LocalImageViewModel? _selectedItem;
    private string _selectedItemInfo = string.Empty;
    private int _currentPage = 1;
    private int _lastPage = 1;

    private IReadOnlyList<MonitorItemViewModel> _applyTargets = Array.Empty<MonitorItemViewModel>();
    private MonitorItemViewModel? _selectedTarget;
    private bool _isApplying;
    private string _applyMessage = string.Empty;

    public LocalFolderViewModel(
        LocalFolderService folderService,
        WallpaperService wallpaperService,
        SettingsService settingsService,
        SlideshowManager slideshowManager,
        IReadOnlyList<MonitorItemViewModel> allMonitors)
    {
        _folderService = folderService;
        _wallpaperService = wallpaperService;
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
        PreviousPageCommand = new RelayCommand(() => GoToPage(CurrentPage - 1), () => CurrentPage > 1);
        NextPageCommand = new RelayCommand(() => GoToPage(CurrentPage + 1), () => CurrentPage < LastPage);
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), CanApply);

        MarkAllCommand = new RelayCommand(MarkAll, () => CurrentSlideshow is { IsStopped: true, UseWholeFolder: false } && _compatible.Count > 0);
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
        else if (IsScanning)
        {
            SlideshowHint = "Aguarde a análise da pasta terminar para poder iniciar.";
        }
        else if (slideshow.UseWholeFolder && _compatible.Count == 0)
        {
            SlideshowHint = "Nenhuma imagem desta pasta é compatível com este monitor, então não há o que apresentar.";
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

    /// <summary>Imagens compatíveis da página atual.</summary>
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

    public int CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(PageText));
            }
        }
    }

    public int LastPage
    {
        get => _lastPage;
        private set
        {
            if (SetProperty(ref _lastPage, value))
            {
                OnPropertyChanged(nameof(PageText));
            }
        }
    }

    public string PageText => $"Página {CurrentPage} de {LastPage}";

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

    public RelayCommand PreviousPageCommand { get; }

    public RelayCommand NextPageCommand { get; }

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
        if (e.PropertyName is nameof(MonitorSlideshowViewModel.UseWholeFolder) or nameof(MonitorSlideshowViewModel.IsRunning))
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
        return CurrentSlideshow is { } slideshow && (slideshow.UseWholeFolder || slideshow.IsMarked(path));
    }

    private bool CanEditMarks() => CurrentSlideshow is { UseWholeFolder: false, IsStopped: true };

    private void OnItemCheckedChanged(LocalImageViewModel item, bool isChecked)
    {
        CurrentSlideshow?.SetMarked(item.Image.Path, isChecked);
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

        return slideshow.UseWholeFolder ? _compatible.Count > 0 : slideshow.MarkedCount > 0;
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
                ? _compatible
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
        _scan = null;
        _compatible = Array.Empty<LocalImage>();
        MarkAllText = "Marcar todas";
        Items = Array.Empty<LocalImageViewModel>();
        SelectedItem = null;
        CurrentPage = 1;
        LastPage = 1;
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
            _compatible = Array.Empty<LocalImage>();
            Items = Array.Empty<LocalImageViewModel>();
            CurrentPage = 1;
            LastPage = 1;
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

        _compatible = compatible;
        MarkAllText = compatible.Count > 0 ? $"Marcar todas ({compatible.Count})" : "Marcar todas";
        LastPage = Math.Max(1, (compatible.Count + PageSize - 1) / PageSize);
        ShowPage(1);

        var hidden = new List<string>();
        if (wrongRatio > 0)
        {
            hidden.Add($"{wrongRatio} com outra proporção");
        }
        if (tooSmall > 0)
        {
            hidden.Add($"{tooSmall} menor(es) que o monitor");
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

        Summary = $"{_scan.Images.Count} imagem(ns) na pasta · {compatible.Count} compatível(is) com o monitor {_monitor.Number} ({info.ResolutionText})" +
                  (hidden.Count > 0 ? $" · ocultas: {string.Join(", ", hidden)}" : "") +
                  (notes.Count > 0 ? $" · {string.Join(" · ", notes)}" : "");
        UpdateEmptyText();
        RelayCommand.RaiseCanExecuteChanged();
        RefreshSlideshowHint();
    }

    private void GoToPage(int page)
    {
        if (page < 1 || page > LastPage)
        {
            return;
        }

        _thumbnailsCts?.Cancel();
        SelectedItem = null;
        ShowPage(page);
    }

    private void ShowPage(int page)
    {
        CurrentPage = page;
        Items = _compatible.Skip((page - 1) * PageSize).Take(PageSize)
            .Select(i => new LocalImageViewModel(i, isChecked: IsIncluded(i.Path), isMarkingEnabled: CanEditMarks(), OnItemCheckedChanged))
            .ToList();
        RelayCommand.RaiseCanExecuteChanged();
        _ = LoadThumbnailsAsync(Items);
    }

    /// <summary>
    /// Carrega as miniaturas da página em segundo plano (poucas por vez), para a tela não travar.
    /// </summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<LocalImageViewModel> items)
    {
        _thumbnailsCts?.Cancel();
        var cts = _thumbnailsCts = new CancellationTokenSource();
        CancellationToken token = cts.Token;

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
            // Trocou de página ou de monitor: as miniaturas antigas não importam mais
        }
    }

    private void UpdateEmptyText()
    {
        if (IsScanning || HasError)
        {
            EmptyText = string.Empty;
        }
        else if (Folders.Count == 0)
        {
            EmptyText = "Nenhuma pasta adicionada ainda.\nClique em \"Adicionar pasta…\" e escolha uma pasta com seus papéis de parede.";
        }
        else if (_scan is not null && _compatible.Count == 0 && _monitor is not null)
        {
            EmptyText = "Nenhuma imagem desta pasta é compatível com este monitor.\nO resumo acima mostra quantas foram ocultas e por quê.";
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
            await _wallpaperService.SetWallpaperAsync(target.Info, image.Path);
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
