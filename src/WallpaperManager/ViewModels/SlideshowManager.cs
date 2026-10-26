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
    private bool _isResuming;

    public SlideshowManager(
        Func<MonitorInfo, string, Task> applyAsync,
        SettingsService settingsService,
        IReadOnlyList<MonitorItemViewModel> monitors)
    {
        _applyAsync = applyAsync;
        _settingsService = settingsService;
        _monitors = monitors;
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
            _slideshows[deviceId] = slideshow;
        }

        return slideshow;
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
    public async Task<int> ResumeSavedAsync(LocalFolderService folderService)
    {
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

                    List<LocalImage> usable = candidates
                        .Where(i => CompatibilityChecker.IsCompatible(i.Width, i.Height, monitor.Info))
                        .ToList();
                    if (usable.Count == 0)
                    {
                        slideshow.ReportCannotResume("Não consegui retomar a apresentação: nenhuma imagem compatível foi encontrada (a pasta ou os arquivos podem não estar acessíveis agora).");
                        continue;
                    }

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
