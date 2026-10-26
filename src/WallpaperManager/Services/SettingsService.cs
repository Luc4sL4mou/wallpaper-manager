using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// Lê e grava as configurações do usuário em %LocalAppData%\WallpaperManager\settings.json.
/// Nenhum método lança exceção por problema de arquivo: o app deve continuar funcionando sem configurações.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsService(string? filePath = null)
    {
        FilePath = filePath ?? AppPaths.SettingsFile;
        Load();
    }

    public string FilePath { get; }

    public AppSettings Current { get; private set; } = new();

    /// <summary>Aviso para mostrar ao usuário se o arquivo existia mas não pôde ser lido.</summary>
    public string? LoadWarning { get; private set; }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            AppSettings loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();

            // O leitor de JSON cria o dicionário sem ignorar maiúsculas; recriamos como queremos
            loaded.LastWallpapers = new Dictionary<string, LastWallpaper>(
                loaded.LastWallpapers ?? new Dictionary<string, LastWallpaper>(), StringComparer.OrdinalIgnoreCase);
            loaded.Slideshows = new Dictionary<string, MonitorSlideshowSettings>(
                loaded.Slideshows ?? new Dictionary<string, MonitorSlideshowSettings>(), StringComparer.OrdinalIgnoreCase);
            Current = loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Guarda uma cópia do arquivo ruim e segue com configurações novas
            string backup = FilePath + ".corrompido";
            try
            {
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch (Exception)
            {
                // sem cópia, tudo bem
            }

            LoadWarning = $"O arquivo de configurações estava ilegível e foi ignorado (cópia em \"{backup}\").";
            ErrorLog.Write("Falha ao ler as configurações", ex);
        }
    }

    /// <summary>
    /// Grava no disco. Devolve null se deu certo, ou a mensagem de erro.
    /// </summary>
    public string? Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            // Escreve num temporário e troca: se faltar luz no meio, o arquivo antigo continua inteiro
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Falha ao salvar as configurações", ex);
            // A mensagem do .NET vem em inglês; o detalhe técnico fica no registro de erros
            return $"Não foi possível salvar as configurações em \"{FilePath}\". Verifique se você tem permissão nessa pasta e se há espaço no disco (detalhes no registro de erros).";
        }
    }

    /// <summary>
    /// A chave de API em texto, ou null se não houver (ou se não puder ser descriptografada,
    /// por exemplo se o arquivo veio de outro usuário ou de outro computador).
    /// </summary>
    public string? GetApiKey()
    {
        if (string.IsNullOrEmpty(Current.ProtectedApiKey))
        {
            return null;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(Current.ProtectedApiKey), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            ErrorLog.Write("Não foi possível descriptografar a chave de API", ex);
            return null;
        }
    }

    /// <summary>Define (ou remove, com null/vazio) a chave. Chame Save() depois.</summary>
    public void SetApiKey(string? apiKey)
    {
        Current.ProtectedApiKey = string.IsNullOrWhiteSpace(apiKey)
            ? null
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey.Trim()), null, DataProtectionScope.CurrentUser));
    }

    public LastWallpaper? GetLastWallpaper(string deviceId)
    {
        return Current.LastWallpapers.TryGetValue(deviceId, out LastWallpaper? last) ? last : null;
    }

    /// <summary>
    /// Registra o último papel aplicado em um monitor e grava. Devolve null se deu certo, ou a mensagem de erro.
    /// </summary>
    public string? RememberWallpaper(string deviceId, string imagePath, string? wallhavenId)
    {
        Current.LastWallpapers[deviceId] = new LastWallpaper
        {
            ImagePath = imagePath,
            WallhavenId = wallhavenId,
            AppliedAt = DateTime.Now,
        };
        return Save();
    }
}
