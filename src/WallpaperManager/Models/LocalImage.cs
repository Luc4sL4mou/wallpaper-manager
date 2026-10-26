using System.IO;

namespace WallpaperManager.Models;

/// <summary>
/// Uma imagem de uma pasta do computador, com a resolução REAL lida do arquivo.
/// </summary>
/// <param name="Path">Caminho completo do arquivo.</param>
/// <param name="Width">Largura em pixels.</param>
/// <param name="Height">Altura em pixels.</param>
/// <param name="FileSize">Tamanho do arquivo em bytes.</param>
public sealed record LocalImage(string Path, int Width, int Height, long FileSize)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public string ResolutionText => $"{Width} × {Height}";

    public string FileSizeText => FileSize >= 1024 * 1024
        ? $"{FileSize / 1024.0 / 1024.0:0.0} MB"
        : $"{FileSize / 1024.0:0} KB";
}
