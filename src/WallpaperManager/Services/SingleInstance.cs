using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WallpaperManager.Services;

/// <summary>
/// Garante uma única cópia do app por pasta de dados. Se uma segunda cópia for aberta (por exemplo, pelo atalho
/// enquanto o app já roda em segundo plano), ela avisa a primeira para mostrar a janela e encerra.
/// A pasta de dados entra no nome: uma cópia de teste (outra pasta de dados) convive com a de verdade.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showSignal;
    private volatile bool _disposed;

    private SingleInstance(Mutex mutex, EventWaitHandle showSignal, bool isFirst)
    {
        _mutex = mutex;
        _showSignal = showSignal;
        IsFirst = isFirst;
    }

    /// <summary>Verdadeiro se esta é a primeira (e única) cópia rodando.</summary>
    public bool IsFirst { get; }

    public static SingleInstance Acquire(string dataFolder)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(dataFolder.ToLowerInvariant())))[..12];
        var mutex = new Mutex(initiallyOwned: true, $@"Local\WallpaperManager-{hash}", out bool createdNew);
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\WallpaperManager-{hash}-show");
        return new SingleInstance(mutex, signal, createdNew);
    }

    /// <summary>Chamado pela segunda cópia: pede à primeira que mostre a janela.</summary>
    public void SignalFirstInstance()
    {
        AllowSetForegroundWindow(-1); // ASFW_ANY: deixa a primeira cópia trazer a janela para a frente
        _showSignal.Set();
    }

    /// <summary>Fica esperando, em segundo plano, o aviso de uma segunda cópia. onSignal roda em outra thread.</summary>
    public void ListenForSignal(Action onSignal)
    {
        var thread = new Thread(() =>
        {
            while (!_disposed)
            {
                _showSignal.WaitOne();
                if (_disposed)
                {
                    break;
                }

                onSignal();
            }
        })
        { IsBackground = true, Name = "WallpaperManager-SingleInstance" };
        thread.Start();
    }

    public void Dispose()
    {
        _disposed = true;
        _showSignal.Set(); // acorda a thread de escuta para ela terminar
        if (IsFirst)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* já liberado */ }
        }
        _mutex.Dispose();
        _showSignal.Dispose();
    }
}
