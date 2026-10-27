using System.IO;
using Microsoft.Win32;
using WallpaperManager.Models;
using WallpaperManager.Services;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Opção de ordenação exibida na tela; Value é o texto que a API espera.
/// </summary>
public sealed record SortOption(string Value, string Label);

/// <summary>
/// Um papel foi aplicado com sucesso em um monitor.
/// </summary>
/// <param name="Monitor">Monitor que recebeu o papel.</param>
/// <param name="ImagePath">Arquivo local aplicado.</param>
/// <param name="WallhavenId">Id do papel no Wallhaven.</param>
public sealed record WallpaperAppliedEventArgs(MonitorItemViewModel Monitor, string ImagePath, string? WallhavenId);

/// <summary>
/// Catálogo do Wallhaven filtrado para o monitor selecionado: busca, filtros e paginação.
/// </summary>
public sealed class CatalogViewModel : ViewModelBase
{
    private readonly WallhavenService _wallhavenService;
    private readonly DownloadService _downloadService;
    private readonly WallpaperService _wallpaperService;
    private readonly SettingsService _settingsService;
    private readonly IReadOnlyList<MonitorItemViewModel> _allMonitors;
    private CancellationTokenSource? _applyCts;

    private string _downloadFolderMessage = string.Empty;
    private IReadOnlyList<MonitorItemViewModel> _applyTargets = Array.Empty<MonitorItemViewModel>();
    private MonitorItemViewModel? _selectedTarget;
    private bool _isApplying;
    private double _downloadProgress;
    private string _applyMessage = string.Empty;

    private MonitorItemViewModel? _monitor;
    private string _purity = "100"; // padrão SFW; as Configurações podem liberar sketchy/NSFW
    private bool _isActive;
    private bool _needsSearch;
    private CancellationTokenSource? _searchCts;
    private int _searchId;

    private SortOption _selectedSort;
    private bool _includeGeneral = true;
    private bool _includeAnime = true;
    private bool _includePeople = true;

    private IReadOnlyList<CatalogTileViewModel> _items = Array.Empty<CatalogTileViewModel>();
    private IReadOnlyList<PageButtonViewModel> _pageButtons = Array.Empty<PageButtonViewModel>();
    private CatalogTileViewModel? _selectedTile;
    private string _selectedItemInfo = string.Empty;
    private int _currentPage = 1;
    private int _lastPage = 1;
    private bool _isLoading;
    private string? _errorMessage;
    private string _summary = string.Empty;
    private string _filterSummary = string.Empty;

    public CatalogViewModel(
        WallhavenService wallhavenService,
        DownloadService downloadService,
        WallpaperService wallpaperService,
        SettingsService settingsService,
        IReadOnlyList<MonitorItemViewModel> allMonitors)
    {
        _wallhavenService = wallhavenService;
        _downloadService = downloadService;
        _wallpaperService = wallpaperService;
        _settingsService = settingsService;
        _allMonitors = allMonitors;

        SortOptions = new[]
        {
            new SortOption("toplist", "Mais bem avaliados"),
            new SortOption("date_added", "Mais recentes"),
            new SortOption("relevance", "Relevância"),
            new SortOption("views", "Mais vistos"),
            new SortOption("favorites", "Mais favoritados"),
        };

        // Filtros lembrados da última vez (atribuídos aos campos, não às propriedades: não disparam busca nem gravação)
        AppSettings saved = settingsService.Current;
        _selectedSort = SortOptions.FirstOrDefault(o => o.Value == saved.CatalogSort) ?? SortOptions[0];
        _includeGeneral = saved.CatalogIncludeGeneral;
        _includeAnime = saved.CatalogIncludeAnime;
        _includePeople = saved.CatalogIncludePeople;

        PreviousPageCommand = new RelayCommand(() => _ = SearchAsync(CurrentPage - 1), () => !IsLoading && CurrentPage > 1);
        NextPageCommand = new RelayCommand(() => _ = SearchAsync(CurrentPage + 1), () => !IsLoading && CurrentPage < LastPage);
        SearchCommand = new RelayCommand(() => _ = SearchAsync(1), () => !IsLoading && Monitor is not null);
        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(apply: true), CanApply);
        DownloadOnlyCommand = new RelayCommand(() => _ = ApplyAsync(apply: false), CanDownloadOnly);
        CancelApplyCommand = new RelayCommand(() => _applyCts?.Cancel(), () => IsApplying);

        BuildPageButtons();

        ChooseDownloadFolderCommand = new RelayCommand(ChooseDownloadFolder, () => !IsApplying);
        ResetDownloadFolderCommand = new RelayCommand(ResetDownloadFolder, () => !IsApplying && !_downloadService.IsDefaultFolder);
        OpenDownloadFolderCommand = new RelayCommand(OpenDownloadFolder);
        _downloadService.FolderChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DownloadFolder));
            RefreshDownloadedMarks(); // a nova pasta pode já ter (ou não ter) os papéis da página
            RelayCommand.RaiseCanExecuteChanged();
        };
    }

    /// <summary>Pasta onde os papéis baixados do catálogo são salvos.</summary>
    public string DownloadFolder => _downloadService.Folder;

    /// <summary>Resultado da última troca de pasta (ou o motivo da falha).</summary>
    public string DownloadFolderMessage
    {
        get => _downloadFolderMessage;
        private set => SetProperty(ref _downloadFolderMessage, value);
    }

    public RelayCommand ChooseDownloadFolderCommand { get; }

    public RelayCommand ResetDownloadFolderCommand { get; }

    public RelayCommand OpenDownloadFolderCommand { get; }

    private void ChooseDownloadFolder()
    {
        // Para um MVVM "de livro" isto ficaria atrás de uma interface de diálogo; aqui mantemos simples.
        var dialog = new OpenFolderDialog
        {
            Title = "Escolha a pasta onde salvar os papéis de parede baixados",
            InitialDirectory = Directory.Exists(DownloadFolder) ? DownloadFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _downloadService.SetFolder(dialog.FolderName);
            DownloadFolderMessage = $"Os próximos papéis serão salvos em \"{DownloadFolder}\". Os já baixados continuam na pasta anterior.";
        }
        catch (InvalidOperationException ex)
        {
            DownloadFolderMessage = ex.Message; // a pasta anterior continua valendo
        }
    }

    private void ResetDownloadFolder()
    {
        try
        {
            _downloadService.SetFolder(null);
            DownloadFolderMessage = $"Voltou para a pasta padrão: \"{DownloadFolder}\".";
        }
        catch (InvalidOperationException ex)
        {
            DownloadFolderMessage = ex.Message;
        }
    }

    private void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(DownloadFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{DownloadFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Não foi possível abrir a pasta de downloads", ex);
            DownloadFolderMessage = "Não foi possível abrir a pasta (detalhes no registro de erros).";
        }
    }

    /// <summary>Disparado depois que um papel do catálogo foi aplicado com sucesso em um monitor.</summary>
    public event EventHandler<WallpaperAppliedEventArgs>? WallpaperApplied;

    /// <summary>
    /// Define a pureza das buscas (três dígitos sfw/sketchy/nsfw, ex.: 100). Se mudou, o catálogo é buscado de novo.
    /// </summary>
    public void SetPurity(string purity)
    {
        if (_purity == purity)
        {
            return;
        }

        _purity = purity;
        SearchAgain();
    }

    public IReadOnlyList<SortOption> SortOptions { get; }

    public SortOption SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (SetProperty(ref _selectedSort, value))
            {
                SaveFilters();
                SearchAgain();
            }
        }
    }

    public bool IncludeGeneral
    {
        get => _includeGeneral;
        set
        {
            if (SetProperty(ref _includeGeneral, value))
            {
                SaveFilters();
                SearchAgain();
            }
        }
    }

    public bool IncludeAnime
    {
        get => _includeAnime;
        set
        {
            if (SetProperty(ref _includeAnime, value))
            {
                SaveFilters();
                SearchAgain();
            }
        }
    }

    public bool IncludePeople
    {
        get => _includePeople;
        set
        {
            if (SetProperty(ref _includePeople, value))
            {
                SaveFilters();
                SearchAgain();
            }
        }
    }

    /// <summary>Papéis da página atual que são compatíveis com o monitor.</summary>
    public IReadOnlyList<CatalogTileViewModel> Items
    {
        get => _items;
        private set
        {
            if (SetProperty(ref _items, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public CatalogTileViewModel? SelectedTile
    {
        get => _selectedTile;
        set
        {
            if (SetProperty(ref _selectedTile, value))
            {
                SelectedItemInfo = DescribeSelectedItem(value?.Item);
                UpdateApplyTargets(value?.Item);
                if (!IsApplying)
                {
                    ApplyMessage = string.Empty;
                }
            }
        }
    }

    private WallpaperItem? SelectedItem => _selectedTile?.Item;

    /// <summary>Monitores em que o papel selecionado pode ser aplicado (compatíveis e reconhecidos pelo Windows).</summary>
    public IReadOnlyList<MonitorItemViewModel> ApplyTargets
    {
        get => _applyTargets;
        private set => SetProperty(ref _applyTargets, value);
    }

    /// <summary>O monitor escolhido pelo usuário para receber o papel.</summary>
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

    /// <summary>Verdadeiro enquanto o papel é baixado e aplicado.</summary>
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

    /// <summary>Progresso do download, de 0 a 100.</summary>
    public double DownloadProgress
    {
        get => _downloadProgress;
        private set => SetProperty(ref _downloadProgress, value);
    }

    /// <summary>Resultado do último "baixar e aplicar" (sucesso ou motivo da falha).</summary>
    public string ApplyMessage
    {
        get => _applyMessage;
        private set => SetProperty(ref _applyMessage, value);
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
                BuildPageButtons();
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
                BuildPageButtons();
            }
        }
    }

    public string PageText => $"Página {CurrentPage} de {LastPage}";

    /// <summary>Botões numerados do paginador: 1 … 4 5 [6] 7 8 … 120.</summary>
    public IReadOnlyList<PageButtonViewModel> PageButtons
    {
        get => _pageButtons;
        private set => SetProperty(ref _pageButtons, value);
    }

    private void BuildPageButtons()
    {
        const int Around = 2; // quantas páginas mostrar de cada lado da atual

        var numbers = new SortedSet<int> { 1, LastPage };
        for (int n = CurrentPage - Around; n <= CurrentPage + Around; n++)
        {
            if (n >= 1 && n <= LastPage)
            {
                numbers.Add(n);
            }
        }

        var buttons = new List<PageButtonViewModel>();
        int previous = 0;
        foreach (int n in numbers)
        {
            if (n - previous > 1)
            {
                buttons.Add(new PageButtonViewModel("…", isCurrent: false, isGap: true, command: null));
            }

            int target = n; // cópia: cada botão guarda a sua página
            buttons.Add(new PageButtonViewModel(
                n.ToString(),
                isCurrent: n == CurrentPage,
                isGap: false,
                new RelayCommand(() => _ = SearchAsync(target), () => !IsLoading && target != CurrentPage)));
            previous = n;
        }

        PageButtons = buttons;
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Mensagem de erro da última busca, ou null.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool HasError => ErrorMessage is not null;

    /// <summary>Verdadeiro quando a busca terminou sem nada para mostrar (e sem erro).</summary>
    public bool IsEmpty => !IsLoading && !HasError && Items.Count == 0 && Monitor is not null;

    /// <summary>Resumo do resultado, ex.: "1052 papéis encontrados · 24 exibidos".</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>O que foi enviado ao Wallhaven para este monitor (útil para entender o filtro).</summary>
    public string FilterSummary
    {
        get => _filterSummary;
        private set => SetProperty(ref _filterSummary, value);
    }

    public MonitorItemViewModel? Monitor => _monitor;

    public RelayCommand PreviousPageCommand { get; }

    public RelayCommand NextPageCommand { get; }

    public RelayCommand SearchCommand { get; }

    public RelayCommand ApplyCommand { get; }

    /// <summary>Só baixa o papel selecionado para a pasta de downloads, sem aplicar em nenhum monitor.</summary>
    public RelayCommand DownloadOnlyCommand { get; }

    public RelayCommand CancelApplyCommand { get; }

    /// <summary>
    /// Informa qual monitor está selecionado. Se for outro monitor (ou outra resolução),
    /// o catálogo é zerado e uma nova busca é marcada como pendente.
    /// </summary>
    public void SetMonitor(MonitorItemViewModel? monitor)
    {
        bool sameMonitor = monitor is not null && _monitor is not null
                           && monitor.Info.DeviceId == _monitor.Info.DeviceId
                           && monitor.Info.Width == _monitor.Info.Width
                           && monitor.Info.Height == _monitor.Info.Height;

        _monitor = monitor;
        OnPropertyChanged(nameof(Monitor));
        RelayCommand.RaiseCanExecuteChanged();

        if (sameMonitor)
        {
            return; // a lista de monitores foi recriada, mas é o mesmo monitor
        }

        ResetResults();
        FilterSummary = monitor is null ? string.Empty : DescribeFilter(monitor.Info);
        _needsSearch = monitor is not null;
        SearchIfNeeded();
    }

    /// <summary>
    /// Chamado quando a aba do catálogo é aberta ou fechada. A busca só acontece com a aba aberta,
    /// para não gastar requisições à toa enquanto o usuário navega pelos monitores.
    /// </summary>
    public void SetActive(bool isActive)
    {
        _isActive = isActive;
        SearchIfNeeded();
    }

    private void SearchIfNeeded()
    {
        if (_isActive && _needsSearch)
        {
            _ = SearchAsync(1);
        }
    }

    private void SaveFilters()
    {
        AppSettings settings = _settingsService.Current;
        settings.CatalogSort = SelectedSort.Value;
        settings.CatalogIncludeGeneral = IncludeGeneral;
        settings.CatalogIncludeAnime = IncludeAnime;
        settings.CatalogIncludePeople = IncludePeople;
        _settingsService.Save(); // se falhar, o erro já vai para o registro e os filtros só não ficam lembrados
    }

    private void SearchAgain()
    {
        if (Monitor is null)
        {
            return;
        }

        _needsSearch = true;
        SearchIfNeeded();
    }

    private void ResetResults()
    {
        _searchCts?.Cancel(); // descarta qualquer busca em andamento do monitor anterior
        Items = Array.Empty<CatalogTileViewModel>();
        SelectedTile = null;
        CurrentPage = 1;
        LastPage = 1;
        ErrorMessage = null;
        Summary = string.Empty;
        IsLoading = false;
    }

    /// <summary>
    /// Busca uma página. Todo erro é tratado aqui dentro, então é seguro chamar sem await.
    /// </summary>
    private async Task SearchAsync(int page)
    {
        MonitorItemViewModel? monitor = Monitor;
        if (monitor is null)
        {
            return;
        }

        _needsSearch = false;

        string categories = $"{(IncludeGeneral ? 1 : 0)}{(IncludeAnime ? 1 : 0)}{(IncludePeople ? 1 : 0)}";
        if (categories == "000")
        {
            _searchCts?.Cancel();
            Items = Array.Empty<CatalogTileViewModel>();
            SelectedTile = null;
            Summary = string.Empty;
            IsLoading = false;
            ErrorMessage = "Marque ao menos uma categoria (Geral, Anime ou Pessoas).";
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        CancellationToken token = _searchCts.Token;
        int id = ++_searchId;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            MonitorInfo info = monitor.Info;
            var query = new WallhavenQuery(
                MinWidth: info.Width,
                MinHeight: info.Height,
                Ratio: WallhavenService.GetClosestRatio(info),
                Sorting: SelectedSort.Value,
                Categories: categories,
                Purity: _purity,
                Page: page);

            WallhavenPage result = await _wallhavenService.SearchAsync(query, token);

            // Segunda barreira: a API é um filtro aproximado, a regra local é a que vale
            var compatible = result.Items
                .Where(i => CompatibilityChecker.IsCompatible(i.Width, i.Height, info))
                .ToList();
            int hidden = result.Items.Count - compatible.Count;

            Items = compatible.Select(i => new CatalogTileViewModel(i, _downloadService.IsDownloaded(i.Id))).ToList();
            SelectedTile = null;
            CurrentPage = result.CurrentPage;
            LastPage = result.LastPage;
            Summary = $"{result.Total} papéis encontrados · {compatible.Count} exibidos nesta página" +
                      (hidden > 0 ? $" ({hidden} ocultos por incompatibilidade)" : "");
        }
        catch (OperationCanceledException)
        {
            // Substituída por uma busca mais nova: não mexe na tela
        }
        catch (Exception ex)
        {
            Items = Array.Empty<CatalogTileViewModel>();
            SelectedTile = null;
            Summary = string.Empty;
            if (ex is WallhavenException)
            {
                ErrorMessage = ex.Message;
            }
            else
            {
                ErrorLog.Write("Erro inesperado na busca do catálogo", ex);
                ErrorMessage = $"Erro inesperado na busca: {ex.Message} (detalhes no registro de erros).";
            }
        }
        finally
        {
            if (id == _searchId)
            {
                IsLoading = false;
            }
        }
    }

    private void UpdateApplyTargets(WallpaperItem? item)
    {
        ApplyTargets = item is null
            ? Array.Empty<MonitorItemViewModel>()
            : _allMonitors
                .Where(m => m.IsRecognizedByWindows && CompatibilityChecker.IsCompatible(item.Width, item.Height, m.Info))
                .ToList();

        // Sugere o monitor do filtro; o usuário pode trocar por outro compatível
        SelectedTarget = ApplyTargets.FirstOrDefault(m => m.Info.DeviceId == Monitor?.Info.DeviceId)
                         ?? ApplyTargets.FirstOrDefault();
    }

    private bool CanApply() => !IsApplying && SelectedItem is not null && SelectedTarget is not null;

    // Para só baixar não é preciso um monitor de destino; vale o do filtro para checar a compatibilidade
    private bool CanDownloadOnly() => !IsApplying && SelectedItem is not null && (SelectedTarget is not null || Monitor is not null);

    // Reconfere na pasta atual quais papéis da página já foram baixados (a pasta mudou ou um download terminou)
    private void RefreshDownloadedMarks()
    {
        foreach (CatalogTileViewModel tile in Items)
        {
            tile.IsDownloaded = _downloadService.IsDownloaded(tile.Item.Id);
        }
    }

    /// <summary>
    /// Baixa o papel selecionado (se ainda não estiver na pasta), valida e, com apply, aplica no monitor escolhido.
    /// Todo erro é tratado aqui dentro, então é seguro chamar sem await.
    /// </summary>
    private async Task ApplyAsync(bool apply)
    {
        CatalogTileViewModel? tile = _selectedTile;
        WallpaperItem? item = tile?.Item;

        // Procura de novo pelo ID: a lista de monitores pode ter sido recriada desde a seleção
        MonitorItemViewModel? target = _allMonitors.FirstOrDefault(m => m.Info.DeviceId == SelectedTarget?.Info.DeviceId);
        MonitorInfo? checkAgainst = target?.Info ?? (apply ? null : Monitor?.Info);
        if (tile is null || item is null || checkAgainst is null)
        {
            ApplyMessage = apply
                ? "Escolha um papel e um monitor de destino (o monitor pode ter sido desconectado)."
                : "Escolha um papel para baixar.";
            return;
        }

        bool alreadyDownloaded = _downloadService.IsDownloaded(item.Id);

        _applyCts = new CancellationTokenSource();
        IsApplying = true;
        DownloadProgress = 0;
        ApplyMessage = alreadyDownloaded
            ? $"O papel {item.Id} já está baixado; conferindo o arquivo..."
            : $"Baixando {item.Id} ({item.FileSizeText})...";

        try
        {
            var progress = new Progress<double>(percent => DownloadProgress = percent);
            string path = await _downloadService.DownloadAsync(item, checkAgainst, progress, _applyCts.Token);
            tile.IsDownloaded = true;

            if (!apply || target is null)
            {
                ApplyMessage = alreadyDownloaded
                    ? $"O papel {item.Id} já estava baixado em \"{path}\"; não baixei de novo."
                    : $"Papel {item.Id} baixado em \"{path}\".";
                return;
            }

            ApplyMessage = $"Aplicando no monitor {target.Number}...";
            await _wallpaperService.SetWallpaperAsync(target.Info, path);

            ApplyMessage = $"Papel {item.Id} aplicado no monitor {target.Number} ({target.Info.ResolutionText})" +
                           (alreadyDownloaded ? " (já estava baixado)." : ".");
            WallpaperApplied?.Invoke(this, new WallpaperAppliedEventArgs(target, path, item.Id));
        }
        catch (OperationCanceledException)
        {
            ApplyMessage = "Download cancelado.";
        }
        catch (Exception ex)
        {
            // Erros previstos (regra de compatibilidade, rede, disco) já têm mensagem clara; os demais vão ao registro
            if (ex is not (InvalidOperationException or DownloadException or FileNotFoundException))
            {
                ErrorLog.Write($"Erro inesperado ao {(apply ? "aplicar" : "baixar")} o papel {item.Id}", ex);
            }
            ApplyMessage = $"Não foi possível {(apply ? "aplicar" : "baixar")}: {ex.Message}";
        }
        finally
        {
            IsApplying = false;
            _applyCts.Dispose();
            _applyCts = null;
        }
    }

    private static string DescribeFilter(MonitorInfo monitor)
    {
        string ratio = WallhavenService.GetClosestRatio(monitor);
        string text = $"Filtro enviado ao Wallhaven: atleast={monitor.Width}x{monitor.Height} · ratios={ratio}";

        if (WallhavenService.GetRatioDeviation(monitor) > CompatibilityChecker.AspectTolerance)
        {
            text += $" (a proporção deste monitor, {monitor.AspectRatio:0.000}, não existe no Wallhaven; usei a mais próxima e a validação local faz o resto)";
        }

        return text;
    }

    private string DescribeSelectedItem(WallpaperItem? item)
    {
        if (item is null)
        {
            return "Selecione um papel para ver os detalhes.";
        }

        // Um mesmo papel pode servir em mais de um monitor
        string compatibleMonitors = string.Join(", ", _allMonitors
            .Where(m => CompatibilityChecker.IsCompatible(item.Width, item.Height, m.Info))
            .Select(m => m.Number));

        return $"{item.Id} · {item.ResolutionText} · {item.FileSizeText} · compatível com o(s) monitor(es): " +
               (compatibleMonitors.Length > 0 ? compatibleMonitors : "nenhum");
    }
}
