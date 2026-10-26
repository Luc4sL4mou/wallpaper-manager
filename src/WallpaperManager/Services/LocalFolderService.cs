using System.Collections.Concurrent;
using System.IO;
using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// Resultado da análise de uma pasta.
/// </summary>
/// <param name="Images">Imagens legíveis, com a resolução real.</param>
/// <param name="Unreadable">Arquivos com extensão de imagem, mas corrompidos ou ilegíveis.</param>
/// <param name="CloudOnly">Arquivos só na nuvem (ex.: OneDrive), ignorados para não serem baixados.</param>
/// <param name="Truncated">Verdadeiro se o limite de imagens foi atingido e a análise parou.</param>
public sealed record LocalScanResult(IReadOnlyList<LocalImage> Images, int Unreadable, int CloudOnly, bool Truncated);

/// <summary>
/// Analisa uma pasta do computador: encontra as imagens e lê a resolução real de cada uma.
/// </summary>
public sealed class LocalFolderService
{
    /// <summary>Trava de segurança: apontar para um disco inteiro não deve travar o app.</summary>
    public const int MaxImages = 20000;

    private const int MaxFolderDepth = 12;

    private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".bmp" };

    // Atributos de "arquivo só na nuvem": ler o cabeçalho de um deles faria o OneDrive baixá-lo inteiro
    private const FileAttributes RecallOnOpen = (FileAttributes)0x40000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x400000;
    private const FileAttributes CloudOnlyMask = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    // Resoluções já lidas, para uma nova análise da mesma pasta ser quase instantânea
    private readonly ConcurrentDictionary<string, (long Length, DateTime LastWriteUtc, int Width, int Height)> _sizeCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Analisa a pasta em segundo plano. O progresso é informado como (feitos, total).
    /// </summary>
    public Task<LocalScanResult> ScanAsync(
        string folder, bool includeSubfolders, IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() => Scan(folder, includeSubfolders, progress, cancellationToken), cancellationToken);
    }

    private LocalScanResult Scan(
        string folder, bool includeSubfolders, IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
        {
            throw new InvalidOperationException("A pasta não existe mais ou não está acessível.");
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = includeSubfolders,
            MaxRecursionDepth = MaxFolderDepth, // evita ficar preso em atalhos de pasta que apontam para si mesmos
            IgnoreInaccessible = true,          // pastas sem permissão são puladas em vez de causar erro
        };

        var files = new List<FileInfo>();
        int cloudOnly = 0;
        bool truncated = false;

        foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((file.Attributes & CloudOnlyMask) != 0)
            {
                cloudOnly++;
                continue;
            }

            if (files.Count >= MaxImages)
            {
                truncated = true;
                break;
            }

            files.Add(file);
        }

        var images = new ConcurrentBag<LocalImage>();
        int unreadable = 0;
        int done = 0;

        // Ler cabeçalhos é trabalho de disco: poucas leituras em paralelo já bastam
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(6, Environment.ProcessorCount),
            CancellationToken = cancellationToken,
        };

        Parallel.ForEach(files, parallel, file =>
        {
            if (TryReadSize(file, out int width, out int height))
            {
                images.Add(new LocalImage(file.FullName, width, height, file.Length));
            }
            else
            {
                Interlocked.Increment(ref unreadable);
            }

            int count = Interlocked.Increment(ref done);
            if (count % 25 == 0 || count == files.Count)
            {
                progress?.Report((count, files.Count));
            }
        });

        List<LocalImage> sorted = images.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return new LocalScanResult(sorted, unreadable, cloudOnly, truncated);
    }

    /// <summary>
    /// Lê a resolução real de uma lista de arquivos já conhecidos (por exemplo, as imagens marcadas pelo usuário).
    /// Arquivos que não existem mais, só na nuvem ou ilegíveis são deixados de fora.
    /// </summary>
    public Task<IReadOnlyList<LocalImage>> DescribeAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        List<string> list = paths.ToList();
        return Task.Run<IReadOnlyList<LocalImage>>(() =>
        {
            var images = new ConcurrentBag<LocalImage>();
            var parallel = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Min(6, Environment.ProcessorCount),
                CancellationToken = cancellationToken,
            };

            Parallel.ForEach(list, parallel, path =>
            {
                var file = new FileInfo(path);
                if (file.Exists
                    && Extensions.Contains(file.Extension, StringComparer.OrdinalIgnoreCase)
                    && (file.Attributes & CloudOnlyMask) == 0
                    && TryReadSize(file, out int width, out int height))
                {
                    images.Add(new LocalImage(file.FullName, width, height, file.Length));
                }
            });

            return images.OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
        }, cancellationToken);
    }

    private bool TryReadSize(FileInfo file, out int width, out int height)
    {
        DateTime lastWrite = file.LastWriteTimeUtc;
        if (_sizeCache.TryGetValue(file.FullName, out var cached) && cached.Length == file.Length && cached.LastWriteUtc == lastWrite)
        {
            (width, height) = (cached.Width, cached.Height);
            return true;
        }

        try
        {
            (width, height) = ImageInspector.ReadSize(file.FullName);
            _sizeCache[file.FullName] = (file.Length, lastWrite, width, height);
            return width > 0 && height > 0;
        }
        catch (Exception)
        {
            // Corrompido, formato sem codec ou arquivo trocado durante a leitura: conta como ilegível
            (width, height) = (0, 0);
            return false;
        }
    }
}
