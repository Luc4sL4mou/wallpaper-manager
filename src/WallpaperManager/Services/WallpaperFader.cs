using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// Troca o papel de parede de um monitor com uma transição suave (o Windows troca de uma vez só).
/// Como funciona: tira uma foto da área de trabalho, cobre o monitor com ela numa janela sem borda que fica
/// logo acima da área de trabalho (abaixo de todas as outras janelas), troca o papel de verdade por baixo,
/// tira a foto nova e faz a janela passar da foto antiga para a nova. Os ícones aparecem nas duas fotos,
/// então ficam parados. Se qualquer passo falhar, a troca acontece normalmente, sem animação.
/// Deve ser chamada na thread da interface.
/// </summary>
public sealed class WallpaperFader
{
    private static readonly TimeSpan FirstPaintDelay = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SwapTimeout = TimeSpan.FromSeconds(3);

    private readonly WallpaperService _wallpaperService;
    private readonly SettingsService _settingsService;

    public WallpaperFader(WallpaperService wallpaperService, SettingsService settingsService)
    {
        _wallpaperService = wallpaperService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Aplica a imagem no monitor, com transição suave se estiver ligada nas configurações e se fizer sentido.
    /// A validação de compatibilidade acontece dentro do WallpaperService, como sempre.
    /// </summary>
    public async Task ApplyAsync(MonitorInfo monitor, string imagePath)
    {
        AppSettings settings = _settingsService.Current;
        if (!settings.SlideshowFade || settings.SlideshowFadeSeconds <= 0 || DesktopCapture.IsDesktopCovered(monitor))
        {
            await _wallpaperService.SetWallpaperAsync(monitor, imagePath);
            return;
        }

        FadeOverlay? overlay = null;
        BitmapSource? before = null;
        try
        {
            before = PrepareBeforeFrame(monitor);
            if (before is not null)
            {
                overlay = FadeOverlay.TryCreate(monitor, before);
            }

            if (overlay is not null)
            {
                await Task.Delay(FirstPaintDelay); // dá tempo de a janela pintar a foto antes de trocar o papel
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Falha ao preparar a transição suave; a troca será direta", ex);
            overlay?.Dispose();
            overlay = null;
        }

        try
        {
            await _wallpaperService.SetWallpaperAsync(monitor, imagePath);
        }
        catch
        {
            overlay?.Dispose();
            throw;
        }

        if (overlay is null || before is null)
        {
            return;
        }

        try
        {
            BitmapSource? after = await WaitForNewFrameAsync(monitor, before, imagePath);
            if (after is not null)
            {
                await overlay.FadeToAsync(after, TimeSpan.FromSeconds(Math.Clamp(settings.SlideshowFadeSeconds, 0.2, 10)));
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Falha durante a transição suave", ex);
        }
        finally
        {
            overlay.Dispose();
        }
    }

    // A foto "de antes" só serve se parecer mesmo com o papel que está no monitor
    private BitmapSource? PrepareBeforeFrame(MonitorInfo monitor)
    {
        string? currentPath = _wallpaperService.GetWallpaper(monitor.DeviceId);
        if (currentPath is null || !File.Exists(currentPath))
        {
            return null;
        }

        BitmapSource? frame = DesktopCapture.CaptureMonitor(monitor);
        return frame is not null && DesktopCapture.LooksLike(frame, currentPath) ? frame : null;
    }

    /// <summary>
    /// O Windows aplica o papel novo uns 0,5 s depois do pedido. Espera até a foto mudar e devolve a foto nova.
    /// Devolve null se não mudou a tempo ou se a foto nova não parece ser a imagem aplicada (sem animação).
    /// </summary>
    private static async Task<BitmapSource?> WaitForNewFrameAsync(MonitorInfo monitor, BitmapSource before, string newImagePath)
    {
        DateTime deadline = DateTime.UtcNow + SwapTimeout;
        await Task.Delay(300);

        while (DateTime.UtcNow < deadline)
        {
            BitmapSource? frame = DesktopCapture.CaptureMonitor(monitor);
            if (frame is not null && DesktopCapture.Differs(frame, before))
            {
                await Task.Delay(150); // deixa a troca terminar de assentar
                frame = DesktopCapture.CaptureMonitor(monitor) ?? frame;
                return DesktopCapture.LooksLike(frame, newImagePath) ? frame : null;
            }

            await Task.Delay(100);
        }

        return null;
    }

    /// <summary>
    /// A janela de transição: sem borda, sem foco, transparente ao mouse, logo acima da área de trabalho.
    /// </summary>
    private sealed class FadeOverlay : IDisposable
    {
        private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
        private const int WmNcHitTest = 0x0084, HtTransparent = -1;
        private const int GwHwndPrev = 3, GwlExStyle = -20;
        private const int WsExTopmost = 0x8;

        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);

        private readonly HwndSource _source;
        private readonly Rectangle _top;
        private readonly Rect _view;

        private FadeOverlay(HwndSource source, Rectangle top, Rect view)
        {
            _source = source;
            _top = top;
            _view = view;
        }

        // Pincel que mostra, da foto do monitor inteiro, só a parte que a janela cobre
        private static ImageBrush BrushFor(BitmapSource frame, Rect view) => new(frame)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
            Viewbox = view,
        };

        public static FadeOverlay? TryCreate(MonitorInfo monitor, BitmapSource before)
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

            var view = new Rect(
                (x - monitor.X) / (double)monitor.Width, (y - monitor.Y) / (double)monitor.Height,
                width / (double)monitor.Width, height / (double)monitor.Height);

            var bottom = new Rectangle { Fill = BrushFor(before, view) };
            var top = new Rectangle { Opacity = 0 };
            var root = new Grid { SnapsToDevicePixels = true, UseLayoutRounding = true };
            RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.NearestNeighbor); // 1 pixel da foto = 1 pixel da tela
            root.Children.Add(bottom);
            root.Children.Add(top);

            // Janela "popup" que nasce ESCONDIDA: assim ela é posicionada na ordem certa antes de aparecer,
            // sem nem por um instante ficar acima das janelas do usuário.
            var parameters = new HwndSourceParameters("WallpaperManagerFade")
            {
                WindowStyle = unchecked((int)0x80000000),            // WS_POPUP (sem WS_VISIBLE)
                ExtendedWindowStyle = 0x80 | 0x08000000,              // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                PositionX = x,
                PositionY = y,
                Width = width,
                Height = height,
            };

            var source = new HwndSource(parameters) { RootVisual = root };
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

            return new FadeOverlay(source, top, view);
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

        public Task FadeToAsync(BitmapSource after, TimeSpan duration)
        {
            _top.Fill = BrushFor(after, _view);

            var finished = new TaskCompletionSource();
            var animation = new DoubleAnimation(0, 1, duration)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            animation.Completed += (_, _) => finished.TrySetResult();
            _top.BeginAnimation(UIElement.OpacityProperty, animation);

            // Segurança: se a animação não avisar que terminou (ex.: janela destruída), não trava a apresentação
            return Task.WhenAny(finished.Task, Task.Delay(duration + TimeSpan.FromSeconds(1)));
        }

        public void Dispose()
        {
            _source.RemoveHook(HitTestHook);
            _source.Dispose();
        }
    }
}
