namespace WallpaperManager.Models;

/// <summary>
/// O último papel aplicado por este app em um monitor.
/// </summary>
public sealed class LastWallpaper
{
    public string ImagePath { get; set; } = "";

    /// <summary>Id no Wallhaven, quando veio do catálogo; null se foi uma imagem local.</summary>
    public string? WallhavenId { get; set; }

    public DateTime AppliedAt { get; set; }
}

/// <summary>
/// O que o app lembra entre uma execução e outra (arquivo settings.json).
/// </summary>
public sealed class AppSettings
{
    /// <summary>Chave de API do Wallhaven criptografada com DPAPI (Base64). Nunca em texto puro.</summary>
    public string? ProtectedApiKey { get; set; }

    public bool IncludeSketchy { get; set; }

    public bool IncludeNsfw { get; set; }

    // Filtros do catálogo, lembrados entre uma execução e outra
    public string CatalogSort { get; set; } = "toplist";

    /// <summary>Tags escolhidas no filtro do catálogo (a busca exige todas).</summary>
    public List<string> CatalogTags { get; set; } = new();

    /// <summary>Pastas do computador usadas como fonte de papéis de parede (aba Pasta local).</summary>
    public List<string> LocalFolders { get; set; } = new();

    public string? SelectedLocalFolder { get; set; }

    public bool LocalFolderIncludeSubfolders { get; set; } = true;

    /// <summary>Abrir junto com o Windows (em segundo plano, só com o ícone perto do relógio).</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Fechar a janela só a esconde: o app continua rodando perto do relógio. Desligado, fechar encerra o app.</summary>
    public bool RunInBackground { get; set; } = true;

    /// <summary>A dica "o app continua em segundo plano" já foi mostrada (só aparece uma vez).</summary>
    public bool TrayHintShown { get; set; }

    /// <summary>Pasta escolhida para salvar os papéis baixados do catálogo; null = a pasta padrão do app.</summary>
    public string? DownloadFolder { get; set; }

    /// <summary>
    /// Arquivos que ESTE app baixou (caminho completo). "Limpar downloads" só apaga estes, para nunca tocar
    /// em arquivos do usuário que estejam na mesma pasta.
    /// </summary>
    public List<string> DownloadedFiles { get; set; } = new();

    /// <summary>Transição suave (fade) ao trocar o papel na apresentação de slides.</summary>
    public bool SlideshowFade { get; set; } = true;

    /// <summary>Duração do fade, em segundos.</summary>
    public double SlideshowFadeSeconds { get; set; } = 1.5;

    /// <summary>Último papel aplicado por monitor; a chave é o ID do dispositivo.</summary>
    public Dictionary<string, LastWallpaper> LastWallpapers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Configuração da apresentação de slides de cada monitor; a chave é o ID do dispositivo.</summary>
    public Dictionary<string, MonitorSlideshowSettings> Slideshows { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A apresentação de slides de UM monitor. Cada monitor tem a sua própria configuração.
/// </summary>
public sealed class MonitorSlideshowSettings
{
    /// <summary>Verdadeiro: usa todas as imagens compatíveis da pasta. Falso: só as imagens marcadas.</summary>
    public bool UseWholeFolder { get; set; }

    /// <summary>Caminhos das imagens marcadas (podem vir de pastas diferentes).</summary>
    public List<string> MarkedPaths { get; set; } = new();

    /// <summary>Com a pasta inteira: imagens que o usuário desmarcou e que ficam de fora da apresentação.</summary>
    public List<string> ExcludedPaths { get; set; } = new();

    /// <summary>Tempo entre as trocas, em segundos (mínimo 10).</summary>
    public int IntervalSeconds { get; set; } = 300;

    public bool Shuffle { get; set; } = true;

    /// <summary>
    /// A apresentação estava rodando quando o app foi fechado (ou o PC desligado): ela volta sozinha na próxima
    /// vez que o app abrir. Só vira falso quando o usuário para a apresentação de propósito.
    /// </summary>
    public bool ResumeOnStartup { get; set; }

    /// <summary>Pasta usada quando a apresentação roda com a pasta inteira (precisa ser lembrada para retomar).</summary>
    public string? ResumeFolder { get; set; }

    public bool ResumeIncludeSubfolders { get; set; } = true;
}
