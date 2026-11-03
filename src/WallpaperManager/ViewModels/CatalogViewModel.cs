using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
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
    private readonly WallpaperFader _wallpaperFader; // aplica com a transição suave (fade-out/fade-in)
    private readonly SettingsService _settingsService;
    private readonly IReadOnlyList<MonitorItemViewModel> _allMonitors;
    private CancellationTokenSource? _applyCts;
    private CancellationTokenSource? _thumbnailsCts;

    // Quais papéis (por id) são escuros, para a lua aparecer na hora quando uma página é revista
    private readonly ConcurrentDictionary<string, bool> _darkById = new();

    // Modo Só escuros: quantos escuros juntar por página, e quantas páginas do Wallhaven gastar no máximo para isso
    // (cada uma conta no limite de 40 buscas por minuto; páginas repetidas vêm do cache e não contam)
    private const int DarkPageTarget = 24;
    private const int MaxApiPagesPerDarkPage = 4;

    // Modo Só escuros: em que página do Wallhaven começa cada página do app
    private readonly Dictionary<int, int> _darkPageStarts = new();

    // Modo comum: cada página do app junta esta quantidade de páginas do Wallhaven (24 papéis cada),
    // carregadas aos poucos pela rolagem
    private const int ApiPagesPerPage = 4;

    // Estado da rolagem da página atual (null = nada a carregar: busca nova, modo Só escuros ou sem monitor)
    private Func<int, WallhavenQuery>? _scrollQuery;
    private Func<WallhavenPage, ISet<string>, List<CatalogTileViewModel>>? _scrollTiles;
    private HashSet<string> _scrollSeen = new();
    private int _scrollNextApiPage;
    private int _scrollLastApiPage;
    private int _scrollHidden;
    private int _scrollTotal;
    private string _scrollForText = string.Empty;
    private string _scrollProblem = string.Empty;
    private bool _isLoadingMore;

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
    private bool _darkOnly;
    private string _searchText = string.Empty;
    private readonly ObservableCollection<TagChipViewModel> _tags = new();

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
        WallpaperFader wallpaperFader,
        SettingsService settingsService,
        IReadOnlyList<MonitorItemViewModel> allMonitors)
    {
        _wallhavenService = wallhavenService;
        _downloadService = downloadService;
        _wallpaperFader = wallpaperFader;
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
        _darkOnly = saved.CatalogDarkOnly;
        foreach (string tag in saved.CatalogTags.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _tags.Add(new TagChipViewModel(tag, RemoveTag));
        }

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

    /// <summary>Pede à tela que abra a visualização em tela cheia do papel selecionado.</summary>
    public event EventHandler? PreviewRequested;

    private void Preview(CatalogTileViewModel tile)
    {
        SelectedTile = tile;
        PreviewRequested?.Invoke(this, EventArgs.Empty);
    }

    private void DownloadTile(CatalogTileViewModel tile)
    {
        SelectedTile = tile;
        _ = ApplyAsync(apply: false);
    }

    /// <summary>Na visualização: vai para o papel anterior (-1) ou seguinte (+1) da página.</summary>
    public void MoveSelection(int delta)
    {
        if (Items.Count == 0 || IsApplying)
        {
            return;
        }

        int index = _selectedTile is null ? 0 : Items.ToList().IndexOf(_selectedTile) + delta;
        SelectedTile = Items[(index % Items.Count + Items.Count) % Items.Count];
    }

    /// <summary>Texto da visualização, ex.: "3 de 24".</summary>
    public string PositionText => _selectedTile is null ? string.Empty : $"{Items.ToList().IndexOf(_selectedTile) + 1} de {Items.Count}";

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

    /// <summary>Texto da barra de pesquisa; só vale na próxima busca (Enter ou botão), não a cada tecla.</summary>
    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value ?? string.Empty);
    }

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

    /// <summary>
    /// Só papéis escuros: pede ao Wallhaven os que têm preto na paleta e, das miniaturas que chegam, mostra só
    /// as que passam na mesma medição de brilho do Filtro de irritabilidade (as que ganham a lua).
    /// </summary>
    public bool DarkOnly
    {
        get => _darkOnly;
        set
        {
            if (SetProperty(ref _darkOnly, value))
            {
                SaveFilters();
                SearchAgain();
            }
        }
    }

    /// <summary>Sugestões de tags para o seletor (o Wallhaven aceita qualquer tag; estas são as mais comuns).</summary>
    public IReadOnlyList<TagOption> TagOptions { get; } = TagOption.Defaults;

    /// <summary>Tags escolhidas: a busca traz só os papéis que têm TODAS elas.</summary>
    public ObservableCollection<TagChipViewModel> Tags => _tags;

    /// <summary>Acrescenta uma tag ao filtro e busca de novo. Ignora vazias e repetidas.</summary>
    public void AddTag(string? tag)
    {
        tag = tag?.Trim();
        if (string.IsNullOrEmpty(tag) || _tags.Any(t => string.Equals(t.Value, tag, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _tags.Add(new TagChipViewModel(tag, RemoveTag));
        SaveFilters();
        SearchAgain();
    }

    private void RemoveTag(TagChipViewModel tag)
    {
        if (_tags.Remove(tag))
        {
            SaveFilters();
            SearchAgain();
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
                OnPropertyChanged(nameof(PositionText));
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
        settings.CatalogDarkOnly = DarkOnly;
        settings.CatalogTags = _tags.Select(t => t.Value).ToList();
        _settingsService.Save(); // se falhar, o erro já vai para o registro e os filtros só não ficam lembrados
    }

    // Sintaxe do Wallhaven: +tag exige a tag; entre aspas quando tem espaço
    private string BuildSearchText()
    {
        IEnumerable<string> required = _tags.Select(t => t.Value.Contains(' ') ? $"+\"{t.Value}\"" : $"+{t.Value}");
        return string.Join(' ', required.Append(SearchText.Trim())).Trim();
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
        _thumbnailsCts?.Cancel();
        _scrollQuery = null;
        IsLoadingMore = false;
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

        _searchCts?.Cancel();
        _thumbnailsCts?.Cancel();
        _scrollQuery = null; // a rolagem da página anterior não continua
        IsLoadingMore = false;
        _searchCts = new CancellationTokenSource();
        CancellationToken token = _searchCts.Token;
        int id = ++_searchId;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            MonitorInfo info = monitor.Info;
            FilterSummary = DescribeFilter(info); // mostra as tags, a pesquisa e a cor desta busca
            WallhavenQuery QueryFor(int apiPage) => new(
                MinWidth: info.Width,
                MinHeight: info.Height,
                Ratio: WallhavenService.GetClosestRatio(info),
                Sorting: SelectedSort.Value,
                Categories: "111", // todos os nichos: quem restringe são as tags
                Purity: _purity,
                Page: apiPage,
                Text: BuildSearchText(),
                Color: DarkOnly ? "000000" : ""); // só escuros: pede ao Wallhaven papéis com preto na paleta

            // Segunda barreira: a API é um filtro aproximado, a regra local é a que vale
            List<CatalogTileViewModel> CompatibleTiles(WallhavenPage result, ISet<string> seen) => result.Items
                .Where(i => CompatibilityChecker.IsCompatible(i.Width, i.Height, info) && seen.Add(i.Id))
                .Select(i => new CatalogTileViewModel(i, _downloadService.IsDownloaded(i.Id), Preview, DownloadTile, () => !IsApplying)
                {
                    IsDark = _darkById.TryGetValue(i.Id, out bool dark) && dark,
                })
                .ToList();

            string searched = string.Join(" + ", _tags.Select(t => t.Value).Append(SearchText.Trim()).Where(t => t.Length > 0));
            string forText = searched.Length > 0 ? $" para \"{searched}\"" : "";
            _thumbnailsCts = new CancellationTokenSource();

            if (DarkOnly)
            {
                await SearchDarkPageAsync(page, QueryFor, CompatibleTiles, forText, id, token);
                return;
            }

            // Cada página do app cobre ApiPagesPerPage páginas do Wallhaven: abre com a primeira e as outras
            // entram pela rolagem (LoadMore), só quando o usuário chega perto do fim
            int firstApiPage = (page - 1) * ApiPagesPerPage + 1;
            WallhavenPage result = await _wallhavenService.SearchAsync(QueryFor(firstApiPage), token);
            var seen = new HashSet<string>();
            List<CatalogTileViewModel> tiles = CompatibleTiles(result, seen);

            _scrollQuery = QueryFor;
            _scrollTiles = CompatibleTiles;
            _scrollSeen = seen;
            _scrollNextApiPage = firstApiPage + 1;
            _scrollLastApiPage = Math.Min(firstApiPage + ApiPagesPerPage - 1, result.LastPage);
            _scrollHidden = result.Items.Count - tiles.Count;
            _scrollTotal = result.Total;
            _scrollForText = forText;
            _scrollProblem = string.Empty;

            Items = new ObservableCollection<CatalogTileViewModel>(tiles);
            _ = LoadThumbnailsAsync(tiles, _thumbnailsCts.Token);
            SelectedTile = null;
            CurrentPage = page;
            LastPage = Math.Max(1, (result.LastPage + ApiPagesPerPage - 1) / ApiPagesPerPage);
            UpdateScrollSummary();
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

    /// <summary>Carregando a próxima leva de papéis da página (pela rolagem).</summary>
    public bool IsLoadingMore
    {
        get => _isLoadingMore;
        private set => SetProperty(ref _isLoadingMore, value);
    }

    private bool HasMoreToScroll => _scrollQuery is not null && _scrollNextApiPage <= _scrollLastApiPage;

    /// <summary>
    /// Rolagem dentro da página: a tela chama isto quando a rolagem chega perto do fim (ou se a lista ainda
    /// não enche a tela). Busca a próxima página do Wallhaven que pertence a esta página do app e acrescenta.
    /// </summary>
    public void LoadMore()
    {
        if (!IsLoading && !IsLoadingMore && HasMoreToScroll)
        {
            _ = LoadMoreAsync();
        }
    }

    /// <summary>Todo erro é tratado aqui dentro, então é seguro chamar sem await.</summary>
    private async Task LoadMoreAsync()
    {
        int id = _searchId;
        CancellationToken token = _searchCts?.Token ?? CancellationToken.None;
        Func<int, WallhavenQuery> queryFor = _scrollQuery!;
        Func<WallhavenPage, ISet<string>, List<CatalogTileViewModel>> compatibleTiles = _scrollTiles!;
        int apiPage = _scrollNextApiPage;

        IsLoadingMore = true;
        try
        {
            WallhavenPage result = await _wallhavenService.SearchAsync(queryFor(apiPage), token);
            if (id != _searchId || Items is not ObservableCollection<CatalogTileViewModel> shown)
            {
                return; // trocou de página ou de busca enquanto carregava
            }

            List<CatalogTileViewModel> tiles = compatibleTiles(result, _scrollSeen);
            foreach (CatalogTileViewModel tile in tiles)
            {
                shown.Add(tile);
            }

            _scrollNextApiPage = apiPage + 1;
            _scrollLastApiPage = Math.Min(_scrollLastApiPage, result.LastPage);
            _scrollHidden += result.Items.Count - tiles.Count;
            _scrollProblem = string.Empty;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(PositionText));
            UpdateScrollSummary();

            if (_thumbnailsCts is not null)
            {
                _ = LoadThumbnailsAsync(tiles, _thumbnailsCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Trocou de página ou de busca: não importa mais
        }
        catch (Exception ex)
        {
            if (id == _searchId)
            {
                if (ex is not WallhavenException)
                {
                    ErrorLog.Write("Erro inesperado ao carregar mais papéis do catálogo", ex);
                }
                // A lista continua como está; rolar de novo tenta outra vez
                _scrollProblem = $"Não foi possível carregar mais: {ex.Message}";
                UpdateScrollSummary();
            }
        }
        finally
        {
            if (id == _searchId)
            {
                IsLoadingMore = false;
            }
        }
    }

    private void UpdateScrollSummary()
    {
        Summary = $"{_scrollTotal} papéis encontrados{_scrollForText} · {Items.Count} exibidos nesta página" +
                  (_scrollHidden > 0 ? $" ({_scrollHidden} ocultos por incompatibilidade)" : "") +
                  (HasMoreToScroll ? " · role para ver mais" : "") +
                  (_scrollProblem.Length > 0 ? $" · {_scrollProblem}" : "");
    }

    /// <summary>
    /// Uma página do modo Só escuros. Como só uma parte de cada página do Wallhaven é escura, junta páginas
    /// seguidas dele (até MaxApiPagesPerDarkPage) até ter DarkPageTarget papéis escuros. Mede o brilho antes de
    /// mostrar, para nenhum papel claro aparecer e sumir. Guarda onde cada página do app começa no Wallhaven,
    /// para Anterior/Próxima funcionarem. Se o limite de buscas do Wallhaven estourar no meio, mostra o que já tem.
    /// </summary>
    private async Task SearchDarkPageAsync(
        int page, Func<int, WallhavenQuery> queryFor, Func<WallhavenPage, ISet<string>, List<CatalogTileViewModel>> compatibleTiles,
        string forText, int id, CancellationToken token)
    {
        if (page == 1)
        {
            _darkPageStarts.Clear(); // toda busca nova começa na página 1
        }
        if (!_darkPageStarts.TryGetValue(page, out int apiPage))
        {
            page = 1; // página que ainda não foi alcançada (não deveria acontecer): recomeça
            apiPage = 1;
            _darkPageStarts.Clear();
        }
        _darkPageStarts[page] = apiPage;

        var shown = new List<CatalogTileViewModel>();
        var seen = new HashSet<string>();
        int analyzed = 0, incompatible = 0, apiPagesUsed = 0, total = 0;
        bool reachedEnd = false, rateLimited = false;

        while (shown.Count < DarkPageTarget && apiPagesUsed < MaxApiPagesPerDarkPage)
        {
            WallhavenPage result;
            try
            {
                result = await _wallhavenService.SearchAsync(queryFor(apiPage), token);
            }
            catch (WallhavenException) when (shown.Count > 0)
            {
                rateLimited = true; // já tem o que mostrar: melhor uma página menor do que um erro
                break;
            }

            apiPagesUsed++;
            total = result.Total;
            List<CatalogTileViewModel> tiles = compatibleTiles(result, seen);
            incompatible += result.Items.Count - tiles.Count;
            analyzed += tiles.Count;

            await LoadThumbnailsAsync(tiles, _thumbnailsCts!.Token);
            if (id != _searchId || token.IsCancellationRequested)
            {
                return; // substituída por uma busca mais nova
            }

            shown.AddRange(tiles.Where(t => t.IsDark));
            apiPage = result.CurrentPage + 1;
            if (result.CurrentPage >= result.LastPage)
            {
                reachedEnd = true;
                break;
            }
        }

        _darkPageStarts[page + 1] = apiPage;
        Items = shown;
        SelectedTile = null;
        CurrentPage = page;
        LastPage = reachedEnd ? page : page + 1; // o total de páginas escuras só se descobre andando

        int bright = analyzed - shown.Count;
        Summary = $"{total} papéis encontrados{forText} · {shown.Count} escuros nesta página" +
                  $" (vindos de {apiPagesUsed} página(s) do Wallhaven; ocultos: {bright} claro(s)" +
                  (incompatible > 0 ? $", {incompatible} por incompatibilidade" : "") + ")" +
                  (rateLimited ? " · limite de buscas do Wallhaven atingido: mostrando o que já chegou" : "");
    }

    /// <summary>
    /// Baixa as miniaturas da página (poucas por vez), mostra cada uma e mede o brilho para acender a lua nas
    /// escuras. Trocar de página ou de monitor cancela. Falhas só deixam a miniatura cinza.
    /// </summary>
    private async Task LoadThumbnailsAsync(IReadOnlyList<CatalogTileViewModel> tiles, CancellationToken token)
    {
        using var gate = new SemaphoreSlim(4);
        IEnumerable<Task> tasks = tiles.Select(async tile =>
        {
            await gate.WaitAsync(token);
            try
            {
                byte[]? data = await _wallhavenService.GetThumbnailAsync(tile.Item.ThumbnailUrl, token);
                if (data is null)
                {
                    return;
                }

                (ImageSource? image, bool? isDark) = await Task.Run(() =>
                {
                    ImageSource? decoded = ThumbnailLoader.TryLoadFromBytes(data);
                    return (decoded, BrightnessAnalyzer.IsDark(decoded as System.Windows.Media.Imaging.BitmapSource));
                }, token);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                tile.Thumbnail = image;
                if (isDark is { } dark)
                {
                    _darkById[tile.Item.Id] = dark;
                    tile.IsDark = dark;
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
            // Trocou de página, de monitor ou de filtro: as miniaturas antigas não importam mais
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
            await _wallpaperFader.ApplyAsync(target.Info, path);

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

    /// <summary>Tudo o que a busca manda ao Wallhaven, para o usuário ver exatamente o que está filtrando.</summary>
    private string DescribeFilter(MonitorInfo monitor)
    {
        string ratio = WallhavenService.GetClosestRatio(monitor);
        string text = $"Filtro enviado ao Wallhaven: atleast={monitor.Width}x{monitor.Height} · ratios={ratio}";

        string search = BuildSearchText();
        if (search.Length > 0)
        {
            text += $" · q={search} (tags e pesquisa)";
        }
        if (DarkOnly)
        {
            text += " · colors=000000 (Só escuros)";
        }

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
