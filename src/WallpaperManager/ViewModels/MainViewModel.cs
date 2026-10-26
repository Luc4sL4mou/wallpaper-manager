using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;
using WallpaperManager.Models;
using WallpaperManager.Services;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Estado da janela principal. Não conhece nenhum controle visual, só dados e comandos.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly MonitorService _monitorService;
    private readonly WallpaperService _wallpaperService;
    private readonly SettingsService _settingsService;
    private readonly SynchronizationContext? _uiContext;
    private MonitorItemViewModel? _selectedMonitor;
    private string _statusMessage = string.Empty;
    private string? _selectedImagePath;
    private string _selectedImageInfo = string.Empty;
    private bool _isBusy;
    private bool _isReloading;
    private int _selectedTabIndex;

    // Ordem das abas na janela: Monitores, Catálogo, Pasta local, Configurações
    private const int CatalogTabIndex = 1;
    private const int LocalFolderTabIndex = 2;

    private readonly SlideshowManager _slideshowManager;
    private readonly LocalFolderService _localFolderService;

    public MainViewModel(
        MonitorService monitorService,
        WallpaperService wallpaperService,
        WallhavenService wallhavenService,
        DownloadService downloadService,
        SettingsService settingsService,
        LocalFolderService localFolderService,
        AutostartService autostartService,
        Action exitApplication)
    {
        _monitorService = monitorService;
        _wallpaperService = wallpaperService;
        _settingsService = settingsService;
        _localFolderService = localFolderService;

        Catalog = new CatalogViewModel(wallhavenService, downloadService, wallpaperService, settingsService, Monitors);
        Catalog.WallpaperApplied += OnWallpaperApplied;

        // Cada monitor tem a sua apresentação de slides; o gerenciador as guarda pelo ID do monitor
        // As trocas da apresentação passam pelo "fader": transição suave (se ligada nas Configurações)
        var fader = new WallpaperFader(wallpaperService, settingsService);
        _slideshowManager = new SlideshowManager(fader.ApplyAsync, settingsService, Monitors);
        _slideshowManager.WallpaperChanged += OnSlideshowChanged;

        LocalFolder = new LocalFolderViewModel(localFolderService, wallpaperService, settingsService, _slideshowManager, Monitors);
        LocalFolder.WallpaperApplied += OnWallpaperApplied;

        // A aba Configurações também aplica a chave de API ao serviço e define a pureza das buscas
        Settings = new SettingsViewModel(settingsService, wallhavenService, downloadService,
            () => Monitors.Select(m => m.CurrentWallpaperPath).OfType<string>(), autostartService, exitApplication);
        Catalog.SetPurity(Settings.Purity);
        Settings.PurityChanged += (_, _) => Catalog.SetPurity(Settings.Purity);

        // Guarda a "thread da interface" para voltar a ela quando o Windows avisar de mudanças
        _uiContext = SynchronizationContext.Current;

        RefreshCommand = new RelayCommand(() => LoadMonitors("Lista atualizada"), () => !IsBusy);
        CopyDeviceIdCommand = new RelayCommand(CopyDeviceId, () => SelectedMonitor is not null);
        ChooseImageCommand = new RelayCommand(ChooseImage, () => !IsBusy);
        ApplyWallpaperCommand = new RelayCommand(ApplyWallpaper, CanApplyWallpaper);
        OpenCatalogCommand = new RelayCommand(() => SelectedTabIndex = CatalogTabIndex, () => SelectedMonitor is not null);
        ReapplyLastCommand = new RelayCommand(() => _ = ReapplyLastAsync(), CanReapplyLast);
        RestoreAllCommand = new RelayCommand(() => _ = RestoreAllAsync(),
            () => !IsBusy && Monitors.Any(m => m.IsRecognizedByWindows && m.HasLastWallpaper));

        _monitorService.MonitorsChanged += OnMonitorsChanged;
        LoadMonitors("Monitores detectados");

        // Se o arquivo de configurações estava ilegível, o aviso substitui a mensagem inicial
        if (settingsService.LoadWarning is not null)
        {
            StatusMessage = settingsService.LoadWarning;
        }

        // Imagem fixa para o teste da etapa 2: o papel de parede padrão do Windows
        SelectImage(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"Web\Wallpaper\Windows\img0.jpg"));
    }

    /// <summary>Tamanho da área de desenho do mapa (a tela reduz isso proporcionalmente se faltar espaço).</summary>
    public double MapWidth => 880;
    public double MapHeight => 250;

    // ObservableCollection avisa a tela quando itens entram ou saem da lista
    public ObservableCollection<MonitorItemViewModel> Monitors { get; } = new();

    public MonitorItemViewModel? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (SetProperty(ref _selectedMonitor, value))
            {
                OnPropertyChanged(nameof(HasSelection));

                // Durante a recarga a seleção passa por null; o catálogo só é avisado ao final
                if (!_isReloading)
                {
                    Catalog.SetMonitor(value);
                    LocalFolder.SetMonitor(value);
                }
            }
        }
    }

    public bool HasSelection => SelectedMonitor is not null;

    /// <summary>Catálogo do Wallhaven para o monitor selecionado.</summary>
    public CatalogViewModel Catalog { get; }

    /// <summary>Aba Pasta local: imagens de uma pasta do computador, filtradas para o monitor selecionado.</summary>
    public LocalFolderViewModel LocalFolder { get; }

    /// <summary>Aba Configurações (chave de API, tipos de conteúdo, downloads).</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>As apresentações de slides de todos os monitores (o ícone da bandeja consulta e para elas).</summary>
    public SlideshowManager Slideshows => _slideshowManager;

    /// <summary>
    /// Depois de um tempo (para o Windows terminar de iniciar a sessão), retoma as apresentações que estavam
    /// rodando quando o app foi fechado ou o PC desligado.
    /// </summary>
    public void ScheduleResume(TimeSpan delay)
    {
        _ = ResumeSlideshowsAsync(delay);
    }

    private async Task ResumeSlideshowsAsync(TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            int started = await _slideshowManager.ResumeSavedAsync(_localFolderService);
            if (started > 0)
            {
                StatusMessage = $"{started} apresentação(ões) de slides retomada(s) automaticamente.";
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Erro ao retomar as apresentações", ex);
        }
    }

    /// <summary>Aba aberta: 0 = Monitores, 1 = Catálogo, 2 = Pasta local, 3 = Configurações.</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
            {
                Catalog.SetActive(value == CatalogTabIndex);
                LocalFolder.SetActive(value == LocalFolderTabIndex);
            }
        }
    }

    /// <summary>Imagem local escolhida para aplicar.</summary>
    public string? SelectedImagePath
    {
        get => _selectedImagePath;
        private set => SetProperty(ref _selectedImagePath, value);
    }

    /// <summary>Nome e resolução real da imagem escolhida.</summary>
    public string SelectedImageInfo
    {
        get => _selectedImageInfo;
        private set => SetProperty(ref _selectedImageInfo, value);
    }

    /// <summary>Verdadeiro enquanto o Windows aplica o papel de parede.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand CopyDeviceIdCommand { get; }

    public RelayCommand ChooseImageCommand { get; }

    public RelayCommand ApplyWallpaperCommand { get; }

    public RelayCommand OpenCatalogCommand { get; }

    public RelayCommand ReapplyLastCommand { get; }

    public RelayCommand RestoreAllCommand { get; }

    /// <summary>
    /// A apresentação trocou o papel de um monitor: atualiza o "papel atual" mostrado na aba Monitores.
    /// O "último papel" salvo só muda quando o usuário aplica algo de propósito (não a cada troca automática).
    /// </summary>
    private void OnSlideshowChanged(object? sender, SlideshowChangedEventArgs e)
    {
        e.Monitor.CurrentWallpaperPath = TryGetWallpaper(e.Monitor);
        StatusMessage = $"Apresentação: monitor {e.Monitor.Number} agora mostra {Path.GetFileName(e.ImagePath)}.";
    }

    /// <summary>
    /// Um papel (do catálogo ou de uma pasta local) foi aplicado: atualiza o "papel atual" e o "último papel".
    /// </summary>
    private void OnWallpaperApplied(object? sender, WallpaperAppliedEventArgs e)
    {
        string? warning = RecordApplied(e.Monitor, e.ImagePath, e.WallhavenId);
        string origin = e.WallhavenId is null ? "Imagem da pasta local" : "Papel do catálogo";
        StatusMessage = $"{origin} aplicado no monitor {e.Monitor.Number} ({e.Monitor.Info.ResolutionText}), modo Preencher." +
                        (warning is null ? "" : " " + warning);
        Settings.RefreshDownloadsSummary();
    }

    /// <summary>
    /// Reaplica o último papel de cada monitor conectado, um por vez. A regra de compatibilidade é
    /// verificada de novo em cada um; o que não puder ser restaurado é listado no resumo.
    /// </summary>
    private async Task RestoreAllAsync()
    {
        IsBusy = true;
        int restored = 0, withoutHistory = 0;
        var problems = new List<string>();

        try
        {
            foreach (MonitorItemViewModel monitor in Monitors.ToList())
            {
                LastWallpaper? last = monitor.LastWallpaper;
                if (last is null)
                {
                    withoutHistory++;
                    continue;
                }

                if (!monitor.IsRecognizedByWindows)
                {
                    problems.Add($"monitor {monitor.Number}: o Windows não o reconhece");
                    continue;
                }

                if (!File.Exists(last.ImagePath))
                {
                    problems.Add($"monitor {monitor.Number}: o arquivo {Path.GetFileName(last.ImagePath)} não existe mais");
                    continue;
                }

                StatusMessage = $"Restaurando o monitor {monitor.Number}...";
                try
                {
                    await _wallpaperService.SetWallpaperAsync(monitor.Info, last.ImagePath);
                    monitor.Slideshow?.StopForManualChange();
                    monitor.CurrentWallpaperPath = TryGetWallpaper(monitor);
                    restored++;
                }
                catch (Exception ex)
                {
                    LogIfUnexpected($"Erro inesperado ao restaurar o monitor {monitor.Number}", ex);
                    problems.Add($"monitor {monitor.Number}: {ex.Message}");
                }
            }
        }
        finally
        {
            IsBusy = false;
        }

        StatusMessage = $"Restauração: {restored} monitor(es) restaurado(s)" +
                        (withoutHistory > 0 ? $", {withoutHistory} sem histórico" : "") +
                        (problems.Count > 0 ? $", {problems.Count} com problema ({string.Join("; ", problems)})" : "") + ".";
    }

    /// <summary>
    /// Depois de uma aplicação bem-sucedida: relê do Windows o papel atual (para a tela mostrar o que realmente
    /// ficou) e grava o "último papel" do monitor. Devolve um aviso se não conseguiu gravar.
    /// </summary>
    private string? RecordApplied(MonitorItemViewModel monitor, string imagePath, string? wallhavenId)
    {
        // Quem aplica um papel de propósito não quer que a apresentação o troque logo em seguida
        monitor.Slideshow?.StopForManualChange();
        monitor.CurrentWallpaperPath = TryGetWallpaper(monitor);

        string? error = _settingsService.RememberWallpaper(monitor.Info.DeviceId, imagePath, wallhavenId);
        monitor.LastWallpaper = _settingsService.GetLastWallpaper(monitor.Info.DeviceId);
        return error;
    }

    private bool CanReapplyLast()
    {
        return !IsBusy && SelectedMonitor is { IsRecognizedByWindows: true, HasLastWallpaper: true };
    }

    /// <summary>
    /// Reaplica o último papel deste app no monitor selecionado. A validação de compatibilidade roda de novo.
    /// </summary>
    private async Task ReapplyLastAsync()
    {
        MonitorItemViewModel? monitor = SelectedMonitor;
        LastWallpaper? last = monitor?.LastWallpaper;
        if (monitor is null || last is null)
        {
            return;
        }

        if (!File.Exists(last.ImagePath))
        {
            StatusMessage = $"O arquivo do último papel não existe mais ({Path.GetFileName(last.ImagePath)}). " +
                            "Provavelmente os downloads foram limpos; baixe de novo pelo catálogo.";
            return;
        }

        IsBusy = true;
        StatusMessage = $"Reaplicando o último papel no monitor {monitor.Number}...";
        try
        {
            await _wallpaperService.SetWallpaperAsync(monitor.Info, last.ImagePath);
            string? warning = RecordApplied(monitor, last.ImagePath, last.WallhavenId);
            StatusMessage = $"Último papel reaplicado no monitor {monitor.Number}." + (warning is null ? "" : " " + warning);
        }
        catch (Exception ex)
        {
            LogIfUnexpected($"Erro inesperado ao reaplicar o papel no monitor {monitor.Number}", ex);
            StatusMessage = $"Não foi possível reaplicar no monitor {monitor.Number}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Erros previstos (compatibilidade, arquivo, Windows) já têm mensagem clara; os demais vão ao registro.
    /// </summary>
    private static void LogIfUnexpected(string context, Exception ex)
    {
        if (ex is not (InvalidOperationException or FileNotFoundException or DownloadException))
        {
            ErrorLog.Write(context, ex);
        }
    }

    private void OnMonitorsChanged(object? sender, EventArgs e)
    {
        void Reload()
        {
            LoadMonitors("Mudança nas telas detectada, lista atualizada");

            // Um monitor que acabou de (re)aparecer pode ter uma apresentação esperando para voltar
            _ = ResumeSlideshowsAsync(TimeSpan.FromSeconds(3));
        }

        if (_uiContext is null)
        {
            Reload();
        }
        else
        {
            _uiContext.Post(_ => Reload(), null);
        }
    }

    private void LoadMonitors(string reason)
    {
        // Lembra a seleção pelo ID do dispositivo, não pela resolução
        string? previousId = SelectedMonitor?.Info.DeviceId;

        _isReloading = true;
        try
        {
            IReadOnlyList<MonitorInfo> monitors = _monitorService.GetMonitors();
            HashSet<string> windowsIds = GetWallpaperApiIds();

            Monitors.Clear();
            foreach (MonitorItemViewModel item in BuildMap(monitors, windowsIds))
            {
                item.CurrentWallpaperPath = TryGetWallpaper(item);
                item.LastWallpaper = _settingsService.GetLastWallpaper(item.Info.DeviceId);
                item.Slideshow = _slideshowManager.GetOrCreate(item.Info.DeviceId);
                Monitors.Add(item);
            }

            SelectedMonitor = Monitors.FirstOrDefault(m => m.Info.DeviceId == previousId)
                              ?? Monitors.FirstOrDefault(m => m.Info.IsPrimary)
                              ?? Monitors.FirstOrDefault();

            StatusMessage = $"{reason}: {Monitors.Count} monitor(es) às {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Erro ao detectar os monitores", ex);
            StatusMessage = $"Erro ao detectar monitores: {ex.Message}";
        }
        finally
        {
            _isReloading = false;
        }

        // Só agora o catálogo e a pasta local ficam sabendo; se for o mesmo monitor, mantêm os resultados
        Catalog.SetMonitor(SelectedMonitor);
        LocalFolder.SetMonitor(SelectedMonitor);
    }

    private HashSet<string> GetWallpaperApiIds()
    {
        try
        {
            return new HashSet<string>(_wallpaperService.GetMonitorIds(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Sem a API de papel de parede, os monitores aparecem como "não reconhecidos"
            return new HashSet<string>();
        }
    }

    private string? TryGetWallpaper(MonitorItemViewModel item)
    {
        if (!item.IsRecognizedByWindows)
        {
            return null;
        }

        try
        {
            return _wallpaperService.GetWallpaper(item.Info.DeviceId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Converte as coordenadas reais (pixels do desktop virtual) em blocos que cabem no mapa,
    /// mantendo proporções e posições relativas, e centralizando o conjunto.
    /// </summary>
    private IEnumerable<MonitorItemViewModel> BuildMap(IReadOnlyList<MonitorInfo> monitors, HashSet<string> windowsIds)
    {
        if (monitors.Count == 0)
        {
            yield break;
        }

        int minX = monitors.Min(m => m.X);
        int minY = monitors.Min(m => m.Y);
        int totalWidth = monitors.Max(m => m.X + m.Width) - minX;
        int totalHeight = monitors.Max(m => m.Y + m.Height) - minY;

        double scale = Math.Min(MapWidth / totalWidth, MapHeight / totalHeight);
        double offsetX = (MapWidth - totalWidth * scale) / 2;
        double offsetY = (MapHeight - totalHeight * scale) / 2;

        int number = 1;
        foreach (MonitorInfo monitor in monitors)
        {
            yield return new MonitorItemViewModel(monitor, number++)
            {
                TileLeft = offsetX + (monitor.X - minX) * scale,
                TileTop = offsetY + (monitor.Y - minY) * scale,
                TileWidth = monitor.Width * scale,
                TileHeight = monitor.Height * scale,
                IsRecognizedByWindows = windowsIds.Contains(monitor.DeviceId),
            };
        }
    }

    private void ChooseImage()
    {
        // Para um MVVM "de livro" isto ficaria atrás de uma interface de diálogo;
        // aqui mantemos simples e chamamos a janela de arquivo direto.
        var dialog = new OpenFileDialog
        {
            Title = "Escolha uma imagem para o papel de parede",
            Filter = "Imagens (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };

        if (dialog.ShowDialog() == true)
        {
            SelectImage(dialog.FileName);
        }
    }

    private void SelectImage(string path)
    {
        if (!File.Exists(path))
        {
            SelectedImagePath = null;
            SelectedImageInfo = "Nenhuma imagem escolhida.";
            return;
        }

        try
        {
            (int width, int height) = ImageInspector.ReadSize(path);
            SelectedImagePath = path;
            SelectedImageInfo = $"{Path.GetFileName(path)} · {width} × {height}";
        }
        catch (Exception ex)
        {
            SelectedImagePath = null;
            ErrorLog.Write($"Imagem ilegível: {path}", ex);
            SelectedImageInfo = $"\"{Path.GetFileName(path)}\" não parece ser uma imagem válida (jpg, png ou bmp).";
        }
    }

    private bool CanApplyWallpaper()
    {
        return !IsBusy
               && SelectedImagePath is not null
               && SelectedMonitor is { IsRecognizedByWindows: true };
    }

    // async void é aceitável aqui porque é o "ponto de entrada" de um clique, e todo erro é tratado dentro
    private async void ApplyWallpaper()
    {
        MonitorItemViewModel? monitor = SelectedMonitor;
        string? imagePath = SelectedImagePath;
        if (monitor is null || imagePath is null)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = $"Aplicando no monitor {monitor.Number}...";

        try
        {
            await _wallpaperService.SetWallpaperAsync(monitor.Info, imagePath);

            string? warning = RecordApplied(monitor, imagePath, wallhavenId: null);
            StatusMessage = $"Papel de parede aplicado no monitor {monitor.Number} ({monitor.Info.ResolutionText}), modo Preencher." +
                            (warning is null ? "" : " " + warning);
        }
        catch (Exception ex)
        {
            LogIfUnexpected($"Erro inesperado ao aplicar a imagem local no monitor {monitor.Number}", ex);
            StatusMessage = $"Não foi possível aplicar no monitor {monitor.Number}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopyDeviceId()
    {
        if (SelectedMonitor is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(SelectedMonitor.Info.DeviceId);
            StatusMessage = $"ID do monitor {SelectedMonitor.Number} copiado para a área de transferência.";
        }
        catch (ExternalException)
        {
            // Outro programa pode estar segurando a área de transferência
            StatusMessage = "Não foi possível copiar agora. Tente novamente.";
        }
    }
}
