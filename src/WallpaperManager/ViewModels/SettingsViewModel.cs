using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using WallpaperManager.Services;

namespace WallpaperManager.ViewModels;

/// <summary>
/// Aba Configurações: chave de API do Wallhaven, tipos de conteúdo e pasta de downloads.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly WallhavenService _wallhavenService;
    private readonly DownloadService _downloadService;
    private readonly Func<IEnumerable<string>> _getPathsInUse;
    private readonly AutostartService _autostartService;
    private bool _startWithWindows;
    private bool _runInBackground;
    private string _autostartMessage = string.Empty;

    private string _apiKeyInput = string.Empty;
    private bool _hasApiKey;
    private bool _includeSketchy;
    private bool _includeNsfw;
    private bool _isBusy;
    private bool _fadeEnabled;
    private double? _fadeSeconds;
    private string _apiKeyMessage = string.Empty;
    private string _downloadsSummary = string.Empty;
    private string _downloadsMessage = string.Empty;

    /// <param name="getPathsInUse">Arquivos que o Windows está usando como papel agora (não podem ser apagados).</param>
    public SettingsViewModel(
        SettingsService settings,
        WallhavenService wallhavenService,
        DownloadService downloadService,
        Func<IEnumerable<string>> getPathsInUse,
        AutostartService autostartService,
        Action exitApplication)
    {
        _settings = settings;
        _wallhavenService = wallhavenService;
        _downloadService = downloadService;
        _getPathsInUse = getPathsInUse;
        _autostartService = autostartService;
        ExitCommand = new RelayCommand(exitApplication);

        // Deixa o registro do Windows igual ao que está salvo (ex.: o caminho do app mudou depois de reinstalar)
        _startWithWindows = settings.Current.StartWithWindows;
        _runInBackground = settings.Current.RunInBackground;
        _autostartMessage = autostartService.Apply(_startWithWindows, Environment.ProcessPath)
                            ?? (_startWithWindows ? "Vai abrir sozinho, em segundo plano, quando você entrar no Windows." : string.Empty);

        // Estado inicial a partir do arquivo salvo
        string? savedKey = settings.GetApiKey();
        _wallhavenService.ApiKey = savedKey;
        _hasApiKey = savedKey is not null;
        _includeSketchy = settings.Current.IncludeSketchy;
        _includeNsfw = settings.Current.IncludeNsfw && _hasApiKey; // NSFW exige chave
        _fadeEnabled = settings.Current.SlideshowFade;
        _fadeSeconds = settings.Current.SlideshowFadeSeconds;
        _apiKeyMessage = _hasApiKey ? "Chave salva e em uso." : "Nenhuma chave configurada (o catálogo funciona sem ela).";

        SaveApiKeyCommand = new RelayCommand(SaveApiKey, () => !IsBusy && ApiKeyInput.Trim().Length > 0);
        TestApiKeyCommand = new RelayCommand(() => _ = TestApiKeyAsync(), () => !IsBusy && (ApiKeyInput.Trim().Length > 0 || HasApiKey));
        RemoveApiKeyCommand = new RelayCommand(RemoveApiKey, () => !IsBusy && HasApiKey);
        OpenDownloadsFolderCommand = new RelayCommand(OpenDownloadsFolder);
        ClearDownloadsCommand = new RelayCommand(ClearDownloads);

        // A pasta de downloads também pode ser trocada pela aba Catálogo: mantém esta tela em dia
        _downloadService.FolderChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DownloadsFolder));
            RefreshDownloadsSummary();
        };

        RefreshDownloadsSummary();
    }

    /// <summary>Disparado quando o conjunto de purezas muda (ex.: "100" → "110").</summary>
    public event EventHandler? PurityChanged;

    /// <summary>
    /// Pureza enviada ao Wallhaven: três dígitos sfw/sketchy/nsfw. SFW está sempre ligado.
    /// </summary>
    public string Purity => $"1{(IncludeSketchy ? 1 : 0)}{(IncludeNsfw ? 1 : 0)}";

    public string SettingsFilePath => _settings.FilePath;

    public string LogFilePath => AppPaths.LogFile;

    public string DownloadsFolder => _downloadService.Folder;

    /// <summary>Texto digitado no campo da chave (o campo é limpo depois de salvar).</summary>
    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set
        {
            if (SetProperty(ref _apiKeyInput, value))
            {
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasApiKey
    {
        get => _hasApiKey;
        private set
        {
            if (SetProperty(ref _hasApiKey, value))
            {
                OnPropertyChanged(nameof(CanUseNsfw));
                RelayCommand.RaiseCanExecuteChanged();
            }
        }
    }

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

    public string ApiKeyMessage
    {
        get => _apiKeyMessage;
        private set => SetProperty(ref _apiKeyMessage, value);
    }

    public bool IncludeSketchy
    {
        get => _includeSketchy;
        set
        {
            if (SetProperty(ref _includeSketchy, value))
            {
                _settings.Current.IncludeSketchy = value;
                OnPurityChanged();
            }
        }
    }

    public bool IncludeNsfw
    {
        get => _includeNsfw;
        set
        {
            if (SetProperty(ref _includeNsfw, value))
            {
                _settings.Current.IncludeNsfw = value;
                OnPurityChanged();
            }
        }
    }

    /// <summary>O Wallhaven só libera conteúdo NSFW para quem tem chave de API.</summary>
    public bool CanUseNsfw => HasApiKey;

    /// <summary>Abrir junto com o Windows (em segundo plano, só com o ícone perto do relógio).</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (SetProperty(ref _startWithWindows, value))
            {
                _settings.Current.StartWithWindows = value;
                _settings.Save();
                AutostartMessage = _autostartService.Apply(value, Environment.ProcessPath)
                                   ?? (value ? "Vai abrir sozinho, em segundo plano, quando você entrar no Windows." : "Não vai mais abrir sozinho com o Windows.");
            }
        }
    }

    /// <summary>Fechar a janela só a esconde (o app continua perto do relógio). Desligado, fechar encerra o app.</summary>
    public bool RunInBackground
    {
        get => _runInBackground;
        set
        {
            if (SetProperty(ref _runInBackground, value))
            {
                _settings.Current.RunInBackground = value;
                _settings.Save();
            }
        }
    }

    public string AutostartMessage
    {
        get => _autostartMessage;
        private set => SetProperty(ref _autostartMessage, value);
    }

    /// <summary>Encerra o app de vez (as apresentações em andamento voltam sozinhas na próxima vez que ele abrir).</summary>
    public RelayCommand ExitCommand { get; }

    /// <summary>Transição suave (fade) entre as imagens da apresentação de slides.</summary>
    public bool FadeEnabled
    {
        get => _fadeEnabled;
        set
        {
            if (SetProperty(ref _fadeEnabled, value))
            {
                _settings.Current.SlideshowFade = value;
                _settings.Save();
            }
        }
    }

    /// <summary>Duração do fade, em segundos (valores fora de 0,3 a 5 são ignorados).</summary>
    public double? FadeSeconds
    {
        get => _fadeSeconds;
        set
        {
            if (SetProperty(ref _fadeSeconds, value) && value is >= 0.3 and <= 5)
            {
                _settings.Current.SlideshowFadeSeconds = value.Value;
                _settings.Save();
            }
        }
    }

    public string DownloadsSummary
    {
        get => _downloadsSummary;
        private set => SetProperty(ref _downloadsSummary, value);
    }

    public string DownloadsMessage
    {
        get => _downloadsMessage;
        private set => SetProperty(ref _downloadsMessage, value);
    }

    public RelayCommand SaveApiKeyCommand { get; }

    public RelayCommand TestApiKeyCommand { get; }

    public RelayCommand RemoveApiKeyCommand { get; }

    public RelayCommand OpenDownloadsFolderCommand { get; }

    public RelayCommand ClearDownloadsCommand { get; }

    [GeneratedRegex("^[A-Za-z0-9]{16,64}$")]
    private static partial Regex ApiKeyFormat();

    private void OnPurityChanged()
    {
        string? error = _settings.Save();
        if (error is not null)
        {
            ApiKeyMessage = error;
        }

        OnPropertyChanged(nameof(Purity));
        PurityChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string? ValidateKeyFormat(string key)
    {
        return ApiKeyFormat().IsMatch(key)
            ? null
            : "A chave deve ter só letras e números, sem espaços (normalmente 32 caracteres). Copie de wallhaven.cc/settings/account.";
    }

    private void SaveApiKey()
    {
        string key = ApiKeyInput.Trim();
        string? formatProblem = ValidateKeyFormat(key);
        if (formatProblem is not null)
        {
            ApiKeyMessage = formatProblem;
            return;
        }

        _settings.SetApiKey(key);
        string? error = _settings.Save();
        _wallhavenService.ApiKey = key;
        HasApiKey = true;
        ApiKeyInput = string.Empty; // a chave não fica visível na tela depois de salva
        ApiKeyMessage = error ?? "Chave salva (criptografada) e em uso. Use \"Testar\" para confirmar que o Wallhaven a aceita.";
    }

    private void RemoveApiKey()
    {
        _settings.SetApiKey(null);
        _wallhavenService.ApiKey = null;
        HasApiKey = false;
        ApiKeyInput = string.Empty;

        // Sem chave, NSFW volta a ficar indisponível
        if (IncludeNsfw)
        {
            IncludeNsfw = false; // já salva e avisa o catálogo
        }
        else
        {
            _settings.Save();
        }

        ApiKeyMessage = "Chave removida. O catálogo continua funcionando sem ela.";
    }

    /// <summary>
    /// Testa a chave digitada (ou, se o campo estiver vazio, a chave salva).
    /// </summary>
    private async Task TestApiKeyAsync()
    {
        string key = ApiKeyInput.Trim().Length > 0 ? ApiKeyInput.Trim() : _settings.GetApiKey() ?? string.Empty;
        string? formatProblem = ValidateKeyFormat(key);
        if (formatProblem is not null)
        {
            ApiKeyMessage = formatProblem;
            return;
        }

        IsBusy = true;
        ApiKeyMessage = "Testando a chave no Wallhaven...";
        try
        {
            bool valid = await _wallhavenService.IsApiKeyValidAsync(key, CancellationToken.None);
            ApiKeyMessage = valid
                ? "O Wallhaven aceitou a chave."
                : "O Wallhaven recusou esta chave. Confira se copiou inteira, sem espaços.";
        }
        catch (WallhavenException ex)
        {
            ApiKeyMessage = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Erro inesperado ao testar a chave de API", ex);
            ApiKeyMessage = $"Erro inesperado ao testar a chave: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void RefreshDownloadsSummary()
    {
        (int count, long bytes) = _downloadService.GetUsage();
        DownloadsSummary = count == 0
            ? "Este app ainda não baixou nenhum papel (ou eles já foram apagados)."
            : $"{count} papel(éis) baixado(s) por este app · {bytes / 1024.0 / 1024.0:0.0} MB";
    }

    private void OpenDownloadsFolder()
    {
        try
        {
            Directory.CreateDirectory(DownloadsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DownloadsFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Não foi possível abrir a pasta de downloads", ex);
            DownloadsMessage = $"Não foi possível abrir a pasta: {ex.Message}";
        }
    }

    private void ClearDownloads()
    {
        // Pergunta antes: apagar é definitivo (a confirmação fica aqui por simplicidade, como o seletor de arquivo)
        MessageBoxResult answer = MessageBox.Show(
            "Apagar os papéis que ESTE APP baixou?\n\nOs que estão em uso como papel de parede agora serão mantidos. " +
            "Arquivos que você mesmo colocou nas pastas nunca são apagados, mesmo com nome parecido. " +
            "Os apagados poderão ser baixados de novo pelo catálogo.",
            "Limpar downloads", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        (int deleted, long bytes, int failed) = _downloadService.Clear(_getPathsInUse());
        DownloadsMessage = $"{deleted} arquivo(s) apagado(s), {bytes / 1024.0 / 1024.0:0.0} MB liberados" +
                           (failed > 0 ? $"; {failed} não puderam ser apagados (veja o registro de erros)." : ".");
        RefreshDownloadsSummary();
    }
}
