using System.IO;
using System.Runtime.InteropServices;
using WallpaperManager.Interop;
using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// Lê e aplica papéis de parede por monitor via IDesktopWallpaper.
/// Nunca usa SystemParametersInfo, que trocaria todas as telas de uma vez.
/// </summary>
public sealed class WallpaperService
{
    /// <summary>
    /// IDs de monitor que o IDesktopWallpaper conhece (pode incluir monitores desconectados).
    /// Chame a partir da thread da interface.
    /// </summary>
    public IReadOnlyList<string> GetMonitorIds()
    {
        return Use(desktop =>
        {
            uint count = desktop.GetMonitorDevicePathCount();
            var ids = new List<string>((int)count);
            for (uint i = 0; i < count; i++)
            {
                ids.Add(desktop.GetMonitorDevicePathAt(i));
            }
            return (IReadOnlyList<string>)ids;
        });
    }

    /// <summary>
    /// Caminho do papel de parede atual do monitor, ou null se for cor sólida ou não houver.
    /// Chame a partir da thread da interface.
    /// </summary>
    public string? GetWallpaper(string deviceId)
    {
        return Use(desktop =>
        {
            string path = desktop.GetWallpaper(ResolveMonitorId(desktop, deviceId));
            return string.IsNullOrWhiteSpace(path) ? null : path;
        });
    }

    /// <summary>
    /// Aplica a imagem SOMENTE no monitor indicado, no modo Preencher, e confirma com o Windows.
    /// Antes de qualquer coisa, valida a imagem contra o monitor usando a resolução REAL do arquivo:
    /// uma imagem incompatível nunca chega ao Windows, venha de onde vier.
    /// Roda numa thread STA própria para não travar a janela (o Windows pode levar ~1 s convertendo a imagem).
    /// </summary>
    public Task SetWallpaperAsync(MonitorInfo monitor, string imagePath)
    {
        string fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("A imagem não foi encontrada.", fullPath);
        }

        (int width, int height) size;
        try
        {
            size = ImageInspector.ReadSize(fullPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("O arquivo não é uma imagem válida.", ex);
        }
        CompatibilityChecker.EnsureCompatible(size.width, size.height, monitor);

        string deviceId = monitor.DeviceId;
        return RunOnStaThread(() => Use(desktop =>
        {
            string monitorId = ResolveMonitorId(desktop, deviceId);

            // O modo de ajuste vale para todas as telas. Só mexe nele se for preciso: regravar o mesmo modo a cada
            // troca é desnecessário (e é uma configuração global do Windows).
            if (desktop.GetPosition() != DesktopWallpaperPosition.Fill)
            {
                desktop.SetPosition(DesktopWallpaperPosition.Fill);
            }
            desktop.SetWallpaper(monitorId, fullPath);

            // Confirma: o Windows deve devolver o mesmo arquivo para este monitor
            string applied = desktop.GetWallpaper(monitorId);
            if (!string.Equals(applied, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"O Windows não confirmou a troca. Papel atual informado: \"{applied}\".");
            }
            return true;
        }));
    }

    /// <summary>
    /// Encontra o ID equivalente na lista do IDesktopWallpaper (comparação sem diferenciar maiúsculas).
    /// </summary>
    private static string ResolveMonitorId(IDesktopWallpaper desktop, string deviceId)
    {
        uint count = desktop.GetMonitorDevicePathCount();
        for (uint i = 0; i < count; i++)
        {
            string candidate = desktop.GetMonitorDevicePathAt(i);
            if (string.Equals(candidate, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "O Windows não reconheceu este monitor. Clique em Atualizar e tente de novo.");
    }

    /// <summary>
    /// Cria o objeto COM, executa a ação e o libera em seguida.
    /// </summary>
    private static T Use<T>(Func<IDesktopWallpaper, T> action)
    {
        Type type = Type.GetTypeFromCLSID(DesktopWallpaperClsid.Value)
                    ?? throw new InvalidOperationException("IDesktopWallpaper não está disponível neste Windows.");

        var desktop = (IDesktopWallpaper)Activator.CreateInstance(type)!;
        try
        {
            return action(desktop);
        }
        finally
        {
            Marshal.ReleaseComObject(desktop);
        }
    }

    /// <summary>
    /// Objetos COM do shell esperam uma thread STA (a mesma "regra" da thread da interface do WPF).
    /// Threads do Task.Run são MTA, por isso criamos uma thread STA dedicada.
    /// </summary>
    private static Task RunOnStaThread(Func<bool> action)
    {
        var completion = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (COMException ex)
            {
                // O código HRESULT cru não ajuda ninguém; ele vai para o registro de erros
                ErrorLog.Write("Falha do Windows ao aplicar o papel de parede", ex);
                completion.SetException(new InvalidOperationException(
                    $"O Windows recusou a troca do papel de parede (código 0x{ex.HResult:X8}). " +
                    "Tente outra imagem ou feche e abra o app.", ex));
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
    }
}
