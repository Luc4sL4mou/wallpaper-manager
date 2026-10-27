using System.IO;
using System.Net.Http;
using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// Baixa a imagem original de um papel de parede para uma pasta local e a valida contra o monitor de destino.
/// </summary>
public sealed class DownloadService
{
    // Trava de segurança: nenhum papel de parede razoável chega perto disso
    private const long MaxFileBytes = 150L * 1024 * 1024;

    private static readonly string[] AllowedExtensions = { ".jpg", ".jpeg", ".png" };

    private readonly HttpClient _http;
    private readonly SettingsService _settingsService;
    private string _folder;

    /// <param name="settingsService">Guarda a pasta escolhida e a lista de arquivos que este app baixou.</param>
    /// <param name="folderOverride">Pasta fixa (para testes); senão vale a das configurações ou a padrão.</param>
    public DownloadService(SettingsService settingsService, string? folderOverride = null)
    {
        _settingsService = settingsService;
        _folder = folderOverride ?? settingsService.Current.DownloadFolder ?? AppPaths.DownloadsFolder;

        // O corpo da resposta é lido aos poucos, então o prazo vale só até chegarem os cabeçalhos
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WallpaperManager/1.0");
    }

    /// <summary>Disparado quando a pasta de downloads muda.</summary>
    public event EventHandler? FolderChanged;

    /// <summary>Pasta onde os próximos papéis serão salvos.</summary>
    public string Folder => _folder;

    /// <summary>Verdadeiro se é a pasta privada do app (%LocalAppData%\WallpaperManager\Wallpapers).</summary>
    public bool IsDefaultFolder => IsSamePath(_folder, AppPaths.DownloadsFolder);

    /// <summary>
    /// Muda a pasta dos próximos downloads (null = voltar à pasta padrão). Testa se dá para gravar nela
    /// antes de aceitar, e lança InvalidOperationException (mensagem em português) se não der.
    /// Os papéis já baixados NÃO são movidos: continuam onde estão.
    /// </summary>
    public void SetFolder(string? folder)
    {
        string target = string.IsNullOrWhiteSpace(folder) ? AppPaths.DownloadsFolder : Path.GetFullPath(folder);
        EnsureWritable(target);

        _folder = target;
        _settingsService.Current.DownloadFolder = IsDefaultFolder ? null : target;
        _settingsService.Save(); // se falhar, o erro vai para o registro e a escolha só não é lembrada
        FolderChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsSamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static void EnsureWritable(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string probe = Path.Combine(folder, $".wallpapermanager-teste-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "teste");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"Não consigo gravar na pasta \"{folder}\". Escolha outra pasta ou verifique a permissão e se o disco está conectado.", ex);
        }
    }

    /// <summary>
    /// Quantos papéis ESTE APP baixou (e ainda existem) e quanto espaço ocupam.
    /// </summary>
    public (int Count, long Bytes) GetUsage()
    {
        List<FileInfo> files = ListOwnedFiles();
        return (files.Count, files.Sum(f => f.Length));
    }

    /// <summary>
    /// Apaga os papéis que este app baixou, exceto os caminhos em keepPaths (os que o Windows está usando agora).
    /// Arquivos que o usuário colocou na pasta nunca são tocados, mesmo com nome parecido.
    /// Devolve quantos foram apagados, quantos bytes liberou e quantos não puderam ser apagados.
    /// </summary>
    public (int Deleted, long Bytes, int Failed) Clear(IEnumerable<string> keepPaths)
    {
        var keep = new HashSet<string>(
            keepPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        int deleted = 0, failed = 0;
        long bytes = 0;
        foreach (FileInfo file in ListOwnedFiles())
        {
            if (keep.Contains(file.FullName))
            {
                continue;
            }

            try
            {
                long length = file.Length;
                file.Delete();
                deleted++;
                bytes += length;
                Untrack(file.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
                ErrorLog.Write($"Não foi possível apagar {file.FullName}", ex);
            }
        }

        return (deleted, bytes, failed);
    }

    // Arquivos que o app baixou: os registrados na lista (em qualquer pasta que já foi usada) e, na pasta
    // privada do app, qualquer "wallhaven-*" (inclusive os de versões antigas, que ainda não eram registrados).
    private List<FileInfo> ListOwnedFiles()
    {
        var owned = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in _settingsService.Current.DownloadedFiles.ToList())
        {
            var file = new FileInfo(path);
            if (file.Exists)
            {
                owned[file.FullName] = file;
            }
        }

        string privateFolder = AppPaths.DownloadsFolder;
        if (Directory.Exists(privateFolder))
        {
            foreach (FileInfo file in new DirectoryInfo(privateFolder).EnumerateFiles("wallhaven-*")
                         .Where(f => AllowedExtensions.Contains(f.Extension.ToLowerInvariant())))
            {
                owned[file.FullName] = file;
            }
        }

        return owned.Values.ToList();
    }

    /// <summary>
    /// Verdadeiro se o arquivo deste papel já está na pasta de downloads atual (não baixa nem abre o arquivo).
    /// </summary>
    public bool IsDownloaded(string id) => id.All(char.IsAsciiLetterOrDigit) && FindInFolder(Folder, id).Any();

    // Onde o papel pode já estar: a pasta atual primeiro, depois os arquivos que o app registrou em pastas anteriores
    private IEnumerable<string> FindDownloadedPaths(string id)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string path in FindInFolder(Folder, id))
        {
            if (seen.Add(path))
            {
                yield return path;
            }
        }

        foreach (string tracked in _settingsService.Current.DownloadedFiles.ToList())
        {
            string name = Path.GetFileName(tracked);
            if (name.StartsWith($"wallhaven-{id}.", StringComparison.OrdinalIgnoreCase)
                && File.Exists(tracked) && seen.Add(Path.GetFullPath(tracked)))
            {
                yield return tracked;
            }
        }
    }

    private static IEnumerable<string> FindInFolder(string folder, string id)
    {
        foreach (string extension in AllowedExtensions)
        {
            // "-baixado" é o nome usado quando já havia na pasta um arquivo de mesmo nome que não servia
            foreach (string suffix in new[] { string.Empty, "-baixado" })
            {
                string path = Path.Combine(folder, $"wallhaven-{id}{suffix}{extension}");
                if (File.Exists(path))
                {
                    yield return Path.GetFullPath(path);
                }
            }
        }
    }

    private void Track(string path)
    {
        List<string> tracked = _settingsService.Current.DownloadedFiles;
        if (!tracked.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            tracked.Add(path);
            _settingsService.Save();
        }
    }

    private void Untrack(string path)
    {
        if (_settingsService.Current.DownloadedFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            _settingsService.Save();
        }
    }

    /// <summary>
    /// Baixa o papel (ou reaproveita o arquivo já baixado) e devolve o caminho local.
    /// Valida a compatibilidade duas vezes: antes, com a resolução informada pela API,
    /// e depois, com a resolução REAL do arquivo. Se falhar, o arquivo baixado é apagado.
    /// </summary>
    public async Task<string> DownloadAsync(
        WallpaperItem item, MonitorInfo monitor, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // 1) Antes de gastar internet: a resolução que o Wallhaven informou já deve servir
        CompatibilityChecker.EnsureCompatible(item.Width, item.Height, monitor);

        (string fileName, Uri uri) = ResolveSource(item);
        string path = Path.Combine(Folder, fileName);

        // Já baixado antes (na pasta atual ou em outra que o app já usou) e serve: não baixa de novo
        foreach (string existing in FindDownloadedPaths(item.Id))
        {
            if (IsValidFor(existing, monitor))
            {
                progress?.Report(100);
                return existing;
            }
        }

        // Existe mas não serve (corrompido ou com outro tamanho): é um arquivo que pode ser do usuário, então
        // nunca é sobrescrito nem apagado. O papel é baixado com outro nome.
        if (File.Exists(path))
        {
            path = Path.Combine(Folder, Path.GetFileNameWithoutExtension(fileName) + "-baixado" + Path.GetExtension(fileName));
            if (File.Exists(path) && IsValidFor(path, monitor))
            {
                progress?.Report(100);
                return path;
            }
        }

        await DownloadToFileAsync(uri, path, progress, cancellationToken);

        // 2) Com o arquivo em mãos: a resolução REAL é a que vale
        try
        {
            (int width, int height) = ImageInspector.ReadSize(path);
            CompatibilityChecker.EnsureCompatible(width, height, monitor);
        }
        catch (InvalidOperationException)
        {
            TryDelete(path);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(path);
            throw new DownloadException("O arquivo baixado não é uma imagem válida.", ex);
        }

        Track(path); // a partir daqui este arquivo é "do app": só ele pode ser apagado por "Limpar downloads"
        return path;
    }

    /// <summary>
    /// Só aceitamos o que o Wallhaven realmente serve: https, domínio wallhaven.cc, imagem jpg/png,
    /// e um Id alfanumérico (o nome do arquivo local é montado a partir dele).
    /// </summary>
    private static (string FileName, Uri Uri) ResolveSource(WallpaperItem item)
    {
        if (!Uri.TryCreate(item.ImageUrl, UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host == "wallhaven.cc" || uri.Host.EndsWith(".wallhaven.cc", StringComparison.Ordinal)))
        {
            throw new DownloadException("O endereço da imagem não é do Wallhaven; o download foi bloqueado por segurança.");
        }

        string extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            throw new DownloadException($"Formato de imagem não suportado ({(extension.Length > 0 ? extension : "desconhecido")}).");
        }

        if (item.Id.Length == 0 || !item.Id.All(char.IsAsciiLetterOrDigit))
        {
            throw new DownloadException("Identificador de papel de parede inválido.");
        }

        return ($"wallhaven-{item.Id}{extension}", uri);
    }

    private async Task DownloadToFileAsync(Uri uri, string path, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // Baixa para um arquivo temporário: se algo falhar no meio, nunca sobra uma imagem pela metade com nome final
        string partPath = path + ".part";

        try
        {
            Directory.CreateDirectory(Folder);

            using HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadException($"O Wallhaven recusou o download (erro {(int)response.StatusCode}).");
            }

            long? total = response.Content.Headers.ContentLength;
            if (total > MaxFileBytes)
            {
                throw new DownloadException("O arquivo é grande demais (limite de 150 MB).");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += read;
                    if (received > MaxFileBytes)
                    {
                        throw new DownloadException("O arquivo é grande demais (limite de 150 MB).");
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (total > 0)
                    {
                        progress?.Report(received * 100.0 / total.Value);
                    }
                }
            }

            File.Move(partPath, path, overwrite: true);
            progress?.Report(100);
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException("Não foi possível baixar a imagem. Verifique sua internet.", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DownloadException("O Wallhaven demorou demais para responder.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write($"Falha ao gravar o download em {Folder}", ex);
            throw new DownloadException(
                $"Não foi possível gravar o arquivo em \"{Folder}\". Verifique a permissão da pasta e o espaço em disco.", ex);
        }
        finally
        {
            TryDelete(partPath);
        }
    }

    private static bool IsValidFor(string path, MonitorInfo monitor)
    {
        try
        {
            (int width, int height) = ImageInspector.ReadSize(path);
            return CompatibilityChecker.IsCompatible(width, height, monitor);
        }
        catch (Exception)
        {
            return false; // corrompido ou ilegível: baixa de novo
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Não conseguir limpar um temporário não deve esconder o erro de verdade
        }
    }
}
