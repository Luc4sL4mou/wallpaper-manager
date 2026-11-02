using WallpaperManager.Models;
using WallpaperManager.Services;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Guarda a apresentação de cada monitor, pelo ID do dispositivo. As apresentações vivem aqui (e não nos
/// objetos de monitor) porque a lista de monitores é recriada a cada atualização, e uma apresentação em
/// andamento não pode ser perdida por causa disso.
/// </summary>
public sealed class SlideshowManager
{
    private readonly Dictionary<string, MonitorSlideshowViewModel> _slideshows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<MonitorInfo, string, Task> _applyAsync;
    private readonly SettingsService _settingsService;
    private readonly IReadOnlyList<MonitorItemViewModel> _monitors;
    private readonly LocalFolderService _folderService;
    private bool _isResuming;
    private bool _darkOnly;

    public SlideshowManager(
        Func<MonitorInfo, string, Task> applyAsync,
        SettingsService settingsService,
        IReadOnlyList<MonitorItemViewModel> monitors,
        LocalFolderService folderService)
    {
        _applyAsync = applyAsync;
        _settingsService = settingsService;
        _monitors = monitors;
        _folderService = folderService;
        _darkOnly = settingsService.Current.IrritabilityFilter;
    }

    /// <summary>Disparado a cada troca bem-sucedida, em qualquer monitor.</summary>
    public event EventHandler<SlideshowChangedEventArgs>? WallpaperChanged;

    /// <summary>A apresentação do monitor (criada na primeira vez que é pedida).</summary>
    public MonitorSlideshowViewModel GetOrCreate(string deviceId)
    {
        if (!_slideshows.TryGetValue(deviceId, out MonitorSlideshowViewModel? slideshow))
        {
            slideshow = new MonitorSlideshowViewModel(deviceId, _monitors, _applyAsync, _settingsService);
            slideshow.WallpaperChanged += (_, e) => WallpaperChanged?.Invoke(this, e);
            slideshow.SetImageFilter(_darkOnly ? IsDark : null);
            _slideshows[deviceId] = slideshow;
        }

        return slideshow;
    }

    // ---------- Filtro de irritabilidade (um só, para todos os monitores) ----------

    /// <summary>Filtro de irritabilidade ligado: toda apresentação, em qualquer monitor, usa só imagens escuras.</summary>
    public bool DarkOnly => _darkOnly;

    private bool IsDark(LocalImage image) => _folderService.TryGetIsDark(image, out bool isDark) && isDark;

    /// <summary>
    /// Liga ou desliga o filtro em todos os monitores. As apresentações em andamento trocam de lista na hora
    /// (antes, mede o brilho das imagens delas que ainda não foram medidas). Ao desligar, as que pararam por
    /// falta de imagens escuras voltam sozinhas. Todo erro é tratado aqui dentro.
    /// </summary>
    public async Task SetDarkOnlyAsync(bool enabled)
    {
        if (_darkOnly == enabled)
        {
            return;
        }

        _darkOnly = enabled;
        _settingsService.Current.IrritabilityFilter = enabled;
        _settingsService.Save();

        try
        {
            if (enabled)
            {
                List<LocalImage> pending = _slideshows.Values
                    .Where(s => s.IsRunning)
                    .SelectMany(s => s.AllImages)
                    .Where(i => !_folderService.TryGetIsDark(i, out _))
                    .DistinctBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (pending.Count > 0)
                {
                    await _folderService.AnalyzeBrightnessAsync(pending, 2, null, CancellationToken.None);
                }

                if (!_darkOnly)
                {
                    return; // foi desligado de novo enquanto media
                }

                foreach (MonitorSlideshowViewModel slideshow in _slideshows.Values)
                {
                    slideshow.SetImageFilter(IsDark);
                }
            }
            else
            {
                foreach (MonitorSlideshowViewModel slideshow in _slideshows.Values)
                {
                    slideshow.SetImageFilter(null);
                }
                await ResumeSavedAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Erro ao aplicar o Filtro de irritabilidade nas apresentações", ex);
        }
    }

    /// <summary>
    /// Com o filtro ligado, devolve só as imagens escuras da lista (medindo antes as que faltam); desligado,
    /// devolve a lista como veio.
    /// </summary>
    public async Task<IReadOnlyList<LocalImage>> KeepDarkIfFilteredAsync(IReadOnlyList<LocalImage> images)
    {
        if (!_darkOnly)
        {
            return images;
        }

        List<LocalImage> pending = images.Where(i => !_folderService.TryGetIsDark(i, out _)).ToList();
        if (pending.Count > 0)
        {
            await _folderService.AnalyzeBrightnessAsync(pending, 4, null, CancellationToken.None);
        }

        return images.Where(IsDark).ToList();
    }

    /// <summary>Alguma apresentação está rodando agora?</summary>
    public bool AnyRunning => _slideshows.Values.Any(s => s.IsRunning);

    /// <summary>Para todas as apresentações em andamento (a pedido do usuário: elas não voltam no próximo início).</summary>
    public void StopAll()
    {
        foreach (MonitorSlideshowViewModel slideshow in _slideshows.Values.Where(s => s.IsRunning).ToList())
        {
            slideshow.Stop();
        }
    }

    /// <summary>
    /// Reinicia as apresentações que estavam rodando quando o app foi fechado (ou o PC desligado).
    /// Também serve para quando um monitor volta a aparecer. Só inicia o que não está rodando, só em monitores
    /// que existem agora, e só com imagens compatíveis (a regra de dimensões vale como sempre).
    /// Devolve quantas foram iniciadas. Todo erro é tratado aqui dentro.
    /// </summary>
    public async Task<int> ResumeSavedAsync()
    {
        LocalFolderService folderService = _folderService;
        if (_isResuming)
        {
            return 0;
        }

        _isResuming = true;
        int started = 0;
        try
        {
            foreach (MonitorItemViewModel monitor in _monitors.Where(m => m.IsRecognizedByWindows).ToList())
            {
                MonitorSlideshowViewModel slideshow = GetOrCreate(monitor.Info.DeviceId);
                if (!slideshow.ResumeWanted || slideshow.IsRunning || slideshow.IntervalSeconds == 0)
                {
                    continue;
                }

                try
                {
                    IReadOnlyList<LocalImage> candidates;
                    if (slideshow.UseWholeFolder)
                    {
                        if (slideshow.ResumeFolder is not { Length: > 0 } folder)
                        {
                            continue;
                        }

                        candidates = (await folderService.ScanAsync(folder, slideshow.ResumeIncludeSubfolders, null, CancellationToken.None)).Images;
                    }
                    else
                    {
                        candidates = await folderService.DescribeAsync(slideshow.MarkedPaths.ToList(), CancellationToken.None);
                    }

                    List<LocalImage> compatible = candidates
                        .Where(i => CompatibilityChecker.IsCompatible(i.Width, i.Height, monitor.Info))
                        .ToList();
                    if (compatible.Count == 0)
                    {
                        slideshow.ReportCannotResume("Não consegui retomar a apresentação: nenhuma imagem compatível foi encontrada (a pasta ou os arquivos podem não estar acessíveis agora).");
                        continue;
                    }

                    // A apresentação recebe a lista inteira e aplica o filtro ela mesma; aqui só confere se sobra alguma escura
                    IReadOnlyList<LocalImage> dark = await KeepDarkIfFilteredAsync(compatible);
                    if (!dark.Any(i => slideshow.IsIncluded(i.Path)))
                    {
                        slideshow.ReportCannotResume("Apresentação em espera: nenhuma imagem escura na lista (Filtro de irritabilidade). Ela volta sozinha ao desligar o filtro.");
                        continue;
                    }

                    List<LocalImage> usable = compatible;

                    await slideshow.StartAsync(usable, monitor.Info, slideshow.ResumeFolder, slideshow.ResumeIncludeSubfolders);
                    started++;
                }
                catch (Exception ex)
                {
                    ErrorLog.Write($"Não foi possível retomar a apresentação do monitor {monitor.Number}", ex);
                    slideshow.ReportCannotResume("Não consegui retomar a apresentação (detalhes no registro de erros).");
                }
            }
        }
        finally
        {
            _isResuming = false;
        }

        return started;
    }
}
