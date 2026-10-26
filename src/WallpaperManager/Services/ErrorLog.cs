using System.IO;

namespace WallpaperManager.Services;

/// <summary>
/// Registro simples de erros em arquivo, para investigar problemas que a tela só resume em uma frase.
/// Nunca lança exceção: registrar um erro não pode causar outro.
/// </summary>
public static class ErrorLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static void Write(string context, Exception? exception)
    {
        try
        {
            lock (Gate)
            {
                string path = AppPaths.LogFile;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // Passou do limite: guarda o arquivo antigo como .old e recomeça
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }

                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}" +
                                         $"{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Sem onde registrar: seguir em frente
        }
    }
}
