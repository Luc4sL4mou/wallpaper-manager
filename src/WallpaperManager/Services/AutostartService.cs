using System.IO;
using Microsoft.Win32;

namespace WallpaperManager.Services;

/// <summary>
/// Liga e desliga a abertura do app junto com o Windows, pela chave "Run" do usuário atual
/// (não precisa de administrador). O Windows também lista a entrada em Configurações → Aplicativos → Inicialização,
/// onde o usuário pode desativá-la por lá; essa escolha é respeitada.
/// </summary>
public sealed class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Argumento que faz o app abrir escondido (só o ícone perto do relógio).</summary>
    public const string MinimizedArgument = "--minimized";

    private readonly string _valueName;

    /// <param name="valueName">Nome da entrada no registro (outro nome só em testes).</param>
    public AutostartService(string valueName = "WallpaperManager")
    {
        _valueName = valueName;
    }

    /// <summary>
    /// Só o app INSTALADO (em %LocalAppData%\Programs\WallpaperManager) mexe no registro. Assim, rodar a versão
    /// de desenvolvimento (bin\Debug) ou um teste nunca troca o caminho que o Windows vai abrir no logon.
    /// </summary>
    public static bool IsInstalledLocation(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || Environment.GetEnvironmentVariable("WALLPAPERMANAGER_DATA") is { Length: > 0 })
        {
            return false;
        }

        string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WallpaperManager");
        return Path.GetFullPath(exePath).StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>O comando registrado hoje (ex.: "C:\...\WallpaperManager.exe" --minimized), ou null.</summary>
    public string? RegisteredCommand()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(_valueName) as string;
    }

    public bool IsEnabled() => !string.IsNullOrEmpty(RegisteredCommand());

    public void Enable(string exePath)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(_valueName, $"\"{exePath}\" {MinimizedArgument}");
    }

    public void Disable()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// Deixa o registro igual ao que o usuário escolheu. Devolve uma explicação se não pôde (ex.: versão de teste),
    /// ou null se está tudo certo.
    /// </summary>
    public string? Apply(bool enabled, string? exePath)
    {
        try
        {
            if (!enabled)
            {
                Disable();
                return null;
            }

            if (!IsInstalledLocation(exePath))
            {
                return "Não registrei a abertura com o Windows porque este app não está rodando da pasta instalada " +
                       "(provavelmente é uma versão de teste). Abra pelo atalho da área de trabalho.";
            }

            string expected = $"\"{exePath}\" {MinimizedArgument}";
            if (!string.Equals(RegisteredCommand(), expected, StringComparison.OrdinalIgnoreCase))
            {
                Enable(exePath!);
            }

            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            ErrorLog.Write("Não foi possível atualizar a abertura com o Windows", ex);
            return "Não foi possível alterar a abertura com o Windows (detalhes no registro de erros).";
        }
    }
}
