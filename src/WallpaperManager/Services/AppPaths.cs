using System.IO;

namespace WallpaperManager.Services;

/// <summary>
/// Onde o app guarda seus arquivos. Tudo fica na pasta do usuário, então não precisa de permissão de administrador.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Pasta de dados do app. A variável de ambiente WALLPAPERMANAGER_DATA, se existir, tem prioridade
    /// (útil para testes e para uma instalação portátil); senão, %LocalAppData%\WallpaperManager.
    /// </summary>
    public static string DataFolder { get; } =
        Environment.GetEnvironmentVariable("WALLPAPERMANAGER_DATA") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WallpaperManager");

    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");

    public static string DownloadsFolder => Path.Combine(DataFolder, "Wallpapers");

    public static string LogFile => Path.Combine(DataFolder, "logs", "errors.log");
}
