using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// Troca o papel de parede de um monitor com uma transição suave: o papel antigo some aos poucos (fade-out)
/// enquanto o novo aparece (fade-in), já que o Windows troca de uma vez só.
/// Como funciona: o app fotografa a área de trabalho do monitor e mostra a foto numa janela sem borda, logo acima
/// da área de trabalho (abaixo de todas as outras janelas). Como a foto é idêntica ao que já está na tela, nada
/// muda para o usuário. O papel é trocado por baixo dela e a foto vai sumindo, revelando o papel novo.
/// Se a foto ou a janela não puderem ser criadas, a troca acontece normalmente, sem animação. Vale para toda
/// troca: apresentação e aplicações à mão. Deve ser chamada na thread da interface.
/// </summary>
public sealed class WallpaperFader
{
    // Tempo para a foto chegar à tela antes de o papel ser trocado por baixo dela
    private static readonly TimeSpan ShowDelay = TimeSpan.FromMilliseconds(60);

    // Depois que o Windows confirma a troca, ele ainda leva um instante para desenhar o papel novo
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(300);

    private readonly WallpaperService _wallpaperService;
    private readonly SettingsService _settingsService;

    public WallpaperFader(WallpaperService wallpaperService, SettingsService settingsService)
    {
        _wallpaperService = wallpaperService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Aplica a imagem no monitor, com a transição suave se estiver ligada nas configurações e se fizer sentido.
    /// A validação de compatibilidade acontece dentro do WallpaperService, como sempre.
    /// </summary>
    public async Task ApplyAsync(MonitorInfo monitor, string imagePath)
    {
        AppSettings settings = _settingsService.Current;

        // Com uma janela cobrindo o monitor (jogo em tela cheia, programa maximizado) ninguém veria a transição
        if (!settings.SlideshowFade || settings.TransitionSeconds <= 0 || DesktopCapture.IsDesktopCovered(monitor))
        {
            await _wallpaperService.SetWallpaperAsync(monitor, imagePath);
            return;
        }

        TimeSpan duration = TimeSpan.FromSeconds(Math.Clamp(settings.TransitionSeconds, 0.2, 3));

        SnapshotOverlay? overlay = null;
        try
        {
            overlay = SnapshotOverlay.TryCreate(monitor);
            if (overlay is not null)
            {
                await Dispatcher.Yield(DispatcherPriority.Render);
                await Task.Delay(ShowDelay);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Falha ao preparar a transição; a troca será direta", ex);
            overlay?.Dispose();
            overlay = null;
        }

        try
        {
            await _wallpaperService.SetWallpaperAsync(monitor, imagePath);
        }
        catch
        {
            // A troca falhou: some com a foto na hora (ela mostra o papel antigo, que continua valendo)
            overlay?.Dispose();
            throw;
        }

        if (overlay is null)
        {
            return;
        }

        try
        {
            await Task.Delay(SettleDelay);
            await overlay.FadeOutAsync(duration);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Falha durante a transição", ex);
        }
        finally
        {
            overlay.Dispose();
        }
    }

    /// <summary>
    /// A janela da transição: mostra a foto da área de trabalho tirada antes da troca. Sem borda, sem foco,
    /// transparente ao mouse, logo acima da área de trabalho.
    /// </summary>
    private sealed class SnapshotOverlay : IDisposable
    {
        private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
        private const int WmNcHitTest = 0x0084, HtTransparent = -1;
        private const int GwHwndPrev = 3, GwlExStyle = -20;
        private const int WsExTopmost = 0x8;
        private const int SrcCopy = 0x00CC0020;

        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

        private readonly HwndSource _source;
        private readonly Image _snapshot;

        private SnapshotOverlay(HwndSource source, Image snapshot)
        {
            _source = source;
            _snapshot = snapshot;
        }

        public static SnapshotOverlay? TryCreate(MonitorInfo monitor)
        {
            IntPtr desktop = DesktopCapture.FindDesktopWindow();
            if (desktop == IntPtr.Zero)
            {
                return null;
            }

            // A janela cobre só a área útil do monitor, SEM a faixa da barra de tarefas: assim ela nunca fica
            // por cima da barra e não tem o tamanho exato do monitor (o que o Windows pode tratar como "tela cheia"
            // e reagir escondendo a barra). Com a barra oculta automaticamente a área útil é o monitor todo; aí
            // a janela fica 1 pixel mais baixa só para não ter o tamanho exato.
            (int x, int y, int width, int height) = DesktopCapture.GetWorkArea(monitor);
            if (width == monitor.Width && height == monitor.Height)
            {
                height -= 1;
            }

            if (width <= 0 || height <= 0)
            {
                return null;
            }

            // A foto inclui os ícones da área de trabalho e as janelas abertas por cima; como a janela da transição
            // fica abaixo das janelas do usuário, só a parte da área de trabalho aparece de fato
            BitmapSource? photo = CaptureScreen(x, y, width, height);
            if (photo is null)
            {
                return null;
            }

            var snapshot = new Image { Source = photo, Stretch = Stretch.Fill };

            // Janela "popup" com fundo transparente que nasce ESCONDIDA: assim ela é posicionada na ordem certa
            // antes de aparecer, sem nem por um instante ficar acima das janelas do usuário.
            var parameters = new HwndSourceParameters("WallpaperManagerFade")
            {
                WindowStyle = unchecked((int)0x80000000),            // WS_POPUP (sem WS_VISIBLE)
                ExtendedWindowStyle = 0x80 | 0x08000000,              // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                PositionX = x,
                PositionY = y,
                Width = width,
                Height = height,
                UsesPerPixelTransparency = true,                       // a opacidade da foto vale na tela
            };

            var source = new HwndSource(parameters) { RootVisual = snapshot };
            source.AddHook(HitTestHook);

            // Logo ACIMA da área de trabalho = logo abaixo da janela que estiver imediatamente acima dela
            IntPtr above = GetWindow(desktop, GwHwndPrev);
            SetWindowPos(source.Handle, above, x, y, width, height, SwpNoActivate | SwpShowWindow);

            // Se por algum motivo ela virou "sempre no topo", cobriria as janelas do usuário: desiste
            if ((GetWindowLong(source.Handle, GwlExStyle) & WsExTopmost) != 0)
            {
                source.Dispose();
                return null;
            }

            return new SnapshotOverlay(source, snapshot);
        }

        /// <summary>Fotografa um retângulo da tela (em pixels do desktop virtual).</summary>
        private static BitmapSource? CaptureScreen(int x, int y, int width, int height)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
            {
                return null;
            }

            IntPtr memory = IntPtr.Zero, bitmap = IntPtr.Zero;
            try
            {
                memory = CreateCompatibleDC(screen);
                bitmap = CreateCompatibleBitmap(screen, width, height);
                if (memory == IntPtr.Zero || bitmap == IntPtr.Zero)
                {
                    return null;
                }

                IntPtr previous = SelectObject(memory, bitmap);
                bool copied = BitBlt(memory, 0, 0, width, height, screen, x, y, SrcCopy);
                SelectObject(memory, previous);
                if (!copied)
                {
                    return null;
                }

                BitmapSource photo = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                photo.Freeze();
                return photo;
            }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // O mouse atravessa a janela: nada muda no comportamento da área de trabalho
        private static IntPtr HitTestHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WmNcHitTest)
            {
                handled = true;
                return new IntPtr(HtTransparent);
            }

            return IntPtr.Zero;
        }

        /// <summary>Faz a foto do papel antigo sumir aos poucos, revelando o papel novo por baixo.</summary>
        public Task FadeOutAsync(TimeSpan duration)
        {
            var finished = new TaskCompletionSource();
            var animation = new DoubleAnimation(0, duration)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd,
            };
            animation.Completed += (_, _) => finished.TrySetResult();
            _snapshot.BeginAnimation(UIElement.OpacityProperty, animation);

            // Segurança: se a animação não avisar que terminou (ex.: janela destruída), não trava a troca
            return Task.WhenAny(finished.Task, Task.Delay(duration + TimeSpan.FromSeconds(1)));
        }

        public void Dispose()
        {
            _source.RemoveHook(HitTestHook);
            _source.Dispose();
        }
    }
}
