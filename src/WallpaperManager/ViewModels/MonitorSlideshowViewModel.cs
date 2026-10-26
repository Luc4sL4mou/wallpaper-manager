using System.IO;
using System.Windows.Threading;
using WallpaperManager.Models;
using WallpaperManager.Services;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Unidade de tempo exibida ao lado do número do intervalo.
/// </summary>
public sealed record IntervalUnit(string Label, int Seconds);

/// <summary>
/// Uma troca da apresentação: o monitor e o arquivo que ele passou a mostrar.
/// </summary>
public sealed record SlideshowChangedEventArgs(MonitorItemViewModel Monitor, string ImagePath);

/// <summary>
/// A apresentação de slides de UM monitor: quais imagens (marcadas ou a pasta inteira), de quanto em quanto
/// tempo, e o andamento. Cada monitor tem a sua, com configuração e temporizador próprios, então dois monitores
/// podem rodar ao mesmo tempo com listas e tempos diferentes.
/// A troca roda dentro do app: enquanto ele estiver aberto (mesmo minimizado), a apresentação continua.
/// </summary>
public sealed class MonitorSlideshowViewModel : ViewModelBase
{
    /// <summary>Intervalo mínimo: trocar o papel leva cerca de 1 s.</summary>
    public const int MinIntervalSeconds = 10;

    private const int MaxIntervalSeconds = 24 * 3600;
    private const int MaxAttempts = 3;

    private readonly Func<MonitorInfo, string, Task> _applyAsync;
    private readonly SettingsService _settingsService;
    private readonly IReadOnlyList<MonitorItemViewModel> _allMonitors;
    private readonly MonitorSlideshowSettings _config;
    private readonly HashSet<string> _marked;
    private readonly DispatcherTimer _timer;

    private IReadOnlyList<LocalImage> _images = Array.Empty<LocalImage>();
    private SlideshowPlaylist? _playlist;
    private int _startedWidth;
    private int _startedHeight;

    private bool _useWholeFolder;
    private bool _shuffle;
    private double? _intervalValue;
    private IntervalUnit _selectedUnit;
    private bool _isRunning;
    private bool _isAdvancing;
    private DateTime _nextChangeAt;
    private string _statusText = "Apresentação parada.";
    private string _currentFile = string.Empty;

    public MonitorSlideshowViewModel(
        string deviceId,
        IReadOnlyList<MonitorItemViewModel> allMonitors,
        Func<MonitorInfo, string, Task> applyAsync,
        SettingsService settingsService)
    {
        DeviceId = deviceId;
        _allMonitors = allMonitors;
        _applyAsync = applyAsync;
        _settingsService = settingsService;

        Units = new[]
        {
            new IntervalUnit("segundos", 1),
            new IntervalUnit("minutos", 60),
            new IntervalUnit("horas", 3600),
        };

        // Estado inicial a partir das configurações (nos campos: não dispara gravação)
        _config = settingsService.Current.Slideshows.TryGetValue(deviceId, out MonitorSlideshowSettings? saved)
            ? saved
            : new MonitorSlideshowSettings();
        _marked = new HashSet<string>(_config.MarkedPaths, StringComparer.OrdinalIgnoreCase);
        _useWholeFolder = _config.UseWholeFolder;
        _shuffle = _config.Shuffle;

        int seconds = Math.Clamp(_config.IntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds);
        _selectedUnit = seconds % 3600 == 0 ? Units[2] : seconds % 60 == 0 ? Units[1] : Units[0];
        _intervalValue = seconds / _selectedUnit.Seconds;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;

        StopCommand = new RelayCommand(() => Stop(), () => IsRunning);
        NextNowCommand = new RelayCommand(() => _ = AdvanceAsync(), () => IsRunning && !_isAdvancing);
    }

    /// <summary>Disparado a cada troca bem-sucedida.</summary>
    public event EventHandler<SlideshowChangedEventArgs>? WallpaperChanged;

    public string DeviceId { get; }

    public IReadOnlyList<IntervalUnit> Units { get; }

    // ---------- Quais imagens ----------

    /// <summary>Verdadeiro: usa todas as imagens compatíveis da pasta. Falso: só as marcadas.</summary>
    public bool UseWholeFolder
    {
        get => _useWholeFolder;
        set
        {
            if (SetProperty(ref _useWholeFolder, value))
            {
                Persist();
            }
        }
    }

    /// <summary>Caminhos das imagens marcadas para este monitor.</summary>
    public IReadOnlyCollection<string> MarkedPaths => _marked;

    public int MarkedCount => _marked.Count;

    public string MarkedText => _marked.Count switch
    {
        0 => "nenhuma imagem marcada",
        1 => "1 imagem marcada",
        _ => $"{_marked.Count} imagens marcadas",
    };

    public bool IsMarked(string path) => _marked.Contains(path);

    public void SetMarked(string path, bool marked)
    {
        if (marked ? _marked.Add(path) : _marked.Remove(path))
        {
            OnMarksChanged();
        }
    }

    public void AddMarks(IEnumerable<string> paths)
    {
        int before = _marked.Count;
        foreach (string path in paths)
        {
            _marked.Add(path);
        }

        if (_marked.Count != before)
        {
            OnMarksChanged();
        }
    }

    public void ClearMarks()
    {
        if (_marked.Count > 0)
        {
            _marked.Clear();
            OnMarksChanged();
        }
    }

    private void OnMarksChanged()
    {
        OnPropertyChanged(nameof(MarkedCount));
        OnPropertyChanged(nameof(MarkedText));
        Persist();
    }

    // ---------- Tempo e ordem ----------

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (SetProperty(ref _shuffle, value))
            {
                Persist();
                if (IsRunning && _images.Count > 0)
                {
                    _playlist = new SlideshowPlaylist(_images, value); // vale já na próxima troca
                }
            }
        }
    }

    /// <summary>O número digitado no campo do intervalo (a unidade fica ao lado).</summary>
    public double? IntervalValue
    {
        get => _intervalValue;
        set
        {
            if (SetProperty(ref _intervalValue, value))
            {
                OnIntervalChanged();
            }
        }
    }

    public IntervalUnit SelectedUnit
    {
        get => _selectedUnit;
        set
        {
            if (SetProperty(ref _selectedUnit, value))
            {
                KeepValueInRange();
                OnIntervalChanged();
            }
        }
    }

    /// <summary>
    /// Ao trocar a unidade o número digitado pode virar um tempo inválido (ex.: "5" de minutos para segundos
    /// daria 5 s, abaixo do mínimo). Em vez de deixar o botão Iniciar travado, o número é ajustado para o
    /// valor válido mais próximo.
    /// </summary>
    private void KeepValueInRange()
    {
        if (_intervalValue is not { } value || value <= 0)
        {
            return;
        }

        double seconds = value * _selectedUnit.Seconds;
        double? adjusted = seconds < MinIntervalSeconds
            ? Math.Ceiling((double)MinIntervalSeconds / _selectedUnit.Seconds)
            : seconds > MaxIntervalSeconds
                ? Math.Floor((double)MaxIntervalSeconds / _selectedUnit.Seconds)
                : null;

        if (adjusted is not null)
        {
            _intervalValue = adjusted;
            OnPropertyChanged(nameof(IntervalValue));
        }
    }

    /// <summary>
    /// O tempo realmente usado: o digitado, se for válido; senão, o último que foi salvo como válido.
    /// </summary>
    public int EffectiveIntervalSeconds => IntervalSeconds > 0 ? IntervalSeconds : Math.Max(_config.IntervalSeconds, MinIntervalSeconds);

    /// <summary>O intervalo em segundos, ou 0 se o valor digitado não for válido.</summary>
    public int IntervalSeconds
    {
        get
        {
            double seconds = (IntervalValue ?? 0) * SelectedUnit.Seconds;
            return seconds is >= MinIntervalSeconds and <= MaxIntervalSeconds ? (int)Math.Round(seconds) : 0;
        }
    }

    public string? IntervalProblem => IntervalSeconds > 0
        ? null
        : $"O intervalo deve ficar entre {MinIntervalSeconds} segundos e 24 horas.";

    public bool HasIntervalProblem => IntervalProblem is not null;

    private void OnIntervalChanged()
    {
        OnPropertyChanged(nameof(IntervalSeconds));
        OnPropertyChanged(nameof(IntervalProblem));
        OnPropertyChanged(nameof(HasIntervalProblem));
        RelayCommand.RaiseCanExecuteChanged();

        if (IntervalSeconds > 0)
        {
            Persist();
            if (IsRunning)
            {
                // Vale já para a próxima troca, contando a partir de agora
                _nextChangeAt = DateTime.Now.AddSeconds(EffectiveIntervalSeconds);
                UpdateStatus();
            }
        }
    }

    // ---------- Andamento ----------

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsStopped));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsStopped => !IsRunning;

    /// <summary>Ex.: "Em andamento · 12 imagens · a cada 5 min · próxima troca em 04:31".</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Nome do arquivo que o monitor está mostrando por causa da apresentação.</summary>
    public string CurrentFile
    {
        get => _currentFile;
        private set => SetProperty(ref _currentFile, value);
    }

    public RelayCommand StopCommand { get; }

    public RelayCommand NextNowCommand { get; }

    /// <summary>
    /// Começa a apresentação com estas imagens (já filtradas para o monitor) e troca o papel na hora.
    /// A lista é uma "foto" do momento: arquivos novos na pasta só entram na próxima vez que iniciar.
    /// </summary>
    /// <param name="folder">Pasta usada no modo "pasta inteira"; lembrada para a apresentação voltar sozinha depois.</param>
    public async Task StartAsync(IReadOnlyList<LocalImage> images, MonitorInfo monitor, string? folder = null, bool includeSubfolders = true)
    {
        if (IsRunning || images.Count == 0 || IntervalSeconds == 0)
        {
            return;
        }

        _images = images;
        _playlist = new SlideshowPlaylist(images, Shuffle);
        _startedWidth = monitor.Width;
        _startedHeight = monitor.Height;
        CurrentFile = string.Empty;

        // Lembra que esta apresentação está rodando: se o app for fechado ou o PC desligado, ela volta sozinha
        _config.ResumeOnStartup = true;
        _config.ResumeFolder = UseWholeFolder ? folder : null;
        _config.ResumeIncludeSubfolders = includeSubfolders;
        Persist();

        IsRunning = true;
        _timer.Start();
        await AdvanceAsync();
    }

    /// <summary>Esta apresentação deve voltar sozinha quando o app abrir? (estava rodando ao fechar)</summary>
    public bool ResumeWanted => _config.ResumeOnStartup;

    /// <summary>Mostra por que a retomada automática não pôde acontecer (a apresentação continua "desejada").</summary>
    public void ReportCannotResume(string message)
    {
        if (!IsRunning)
        {
            StatusText = message;
        }
    }

    public string? ResumeFolder => _config.ResumeFolder;

    public bool ResumeIncludeSubfolders => _config.ResumeIncludeSubfolders;

    /// <param name="keepResume">
    /// Parada automática (ex.: a resolução do monitor mudou): a apresentação continua "desejada" e volta a ser
    /// iniciada sozinha quando der. Parada pelo usuário: esquece, e ela não volta no próximo início.
    /// </param>
    public void Stop(string? reason = null, bool keepResume = false)
    {
        _timer.Stop();
        IsRunning = false;
        StatusText = reason ?? "Apresentação parada.";

        if (!keepResume && _config.ResumeOnStartup)
        {
            _config.ResumeOnStartup = false;
            Persist();
        }
    }

    /// <summary>
    /// O usuário aplicou um papel de propósito neste monitor: a apresentação para, senão ela o sobrescreveria.
    /// </summary>
    public void StopForManualChange()
    {
        if (IsRunning)
        {
            Stop("Apresentação parada: você aplicou um papel neste monitor.");
        }
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        try
        {
            if (!IsRunning || _isAdvancing)
            {
                return;
            }

            // Compara com a hora do relógio: depois de o computador acordar do repouso, troca uma vez só
            if (DateTime.Now >= _nextChangeAt)
            {
                await AdvanceAsync();
            }
            else
            {
                UpdateStatus();
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Erro no temporizador da apresentação", ex);
        }
    }

    private void UpdateStatus()
    {
        TimeSpan remaining = _nextChangeAt - DateTime.Now;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        string time = remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
        StatusText = $"Em andamento · {_images.Count} imagem(ns) · a cada {FormatInterval(EffectiveIntervalSeconds)} · próxima troca em {time}";
    }

    private static string FormatInterval(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        < 3600 when seconds % 60 == 0 => $"{seconds / 60} min",
        < 3600 => $"{seconds / 60} min {seconds % 60} s",
        _ when seconds % 3600 == 0 => $"{seconds / 3600} h",
        _ => $"{seconds / 3600} h {seconds % 3600 / 60} min",
    };

    /// <summary>
    /// Troca agora o papel deste monitor, na ordem da lista. Não deixa duas trocas acontecerem ao mesmo tempo.
    /// Se uma imagem falhar (arquivo apagado, recusada pelo Windows), tenta a seguinte, até 3 vezes.
    /// </summary>
    public async Task AdvanceAsync()
    {
        if (!IsRunning || _isAdvancing || _playlist is null)
        {
            return;
        }

        _isAdvancing = true;
        RelayCommand.RaiseCanExecuteChanged();
        try
        {
            StatusText = "Trocando o papel de parede...";
            await AdvanceCoreAsync(_playlist);
        }
        finally
        {
            _isAdvancing = false;
            if (IsRunning)
            {
                _nextChangeAt = DateTime.Now.AddSeconds(EffectiveIntervalSeconds);
                UpdateStatus();
            }
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task AdvanceCoreAsync(SlideshowPlaylist playlist)
    {
        // Procura o monitor de novo pelo ID: a lista pode ter sido recriada ou o monitor desconectado
        MonitorItemViewModel? monitor = _allMonitors.FirstOrDefault(m => m.Info.DeviceId == DeviceId);
        if (monitor is null || !monitor.IsRecognizedByWindows)
        {
            CurrentFile = "Monitor indisponível no momento; tento de novo na próxima troca.";
            return;
        }

        // A lista foi montada para a resolução de quando começou: se mudou, ela pode não servir mais
        if (monitor.Info.Width != _startedWidth || monitor.Info.Height != _startedHeight)
        {
            // Parada automática: refaz a lista sozinha na próxima oportunidade (a lista antiga era da resolução antiga)
            Stop("Apresentação parada: a resolução do monitor mudou; ela será refeita sozinha.", keepResume: true);
            return;
        }

        string? lastProblem = null;
        for (int attempt = 0; attempt < Math.Min(MaxAttempts, playlist.Count); attempt++)
        {
            LocalImage image = playlist.Next();
            if (!File.Exists(image.Path))
            {
                lastProblem = $"{image.FileName} não existe mais";
                continue;
            }

            try
            {
                // O WallpaperService valida a regra de novo, com a resolução real do arquivo, antes de aplicar
                await _applyAsync(monitor.Info, image.Path);
                CurrentFile = image.FileName;
                WallpaperChanged?.Invoke(this, new SlideshowChangedEventArgs(monitor, image.Path));
                return;
            }
            catch (Exception ex)
            {
                if (ex is not (InvalidOperationException or FileNotFoundException))
                {
                    ErrorLog.Write($"Erro inesperado na apresentação ({image.Path})", ex);
                }
                lastProblem = ex.Message;
            }
        }

        CurrentFile = $"Não foi possível trocar: {lastProblem}";
    }

    private void Persist()
    {
        _config.UseWholeFolder = UseWholeFolder;
        _config.MarkedPaths = _marked.Order(StringComparer.OrdinalIgnoreCase).ToList();
        _config.Shuffle = Shuffle;
        if (IntervalSeconds > 0)
        {
            _config.IntervalSeconds = IntervalSeconds;
        }

        _settingsService.Current.Slideshows[DeviceId] = _config;
        _settingsService.Save(); // se falhar, o erro vai para o registro e a escolha só não é lembrada
    }
}
