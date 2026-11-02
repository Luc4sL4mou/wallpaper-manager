using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// Troca o papel de parede de um monitor com uma transição "fade para preto" (o Windows troca de uma vez só).
/// Como funciona: uma janela preta sem borda, logo acima da área de trabalho (abaixo de todas as outras janelas),
/// aparece aos poucos; com o monitor escuro, o papel é trocado por baixo; depois a janela some aos poucos.
/// Não depende de fotografar a tela, então funciona em qualquer versão do Windows. Se a janela não puder ser
/// criada, a troca acontece normalmente, sem animação. Vale para toda troca: apresentação e aplicações à mão.
/// Deve ser chamada na thread da interface.
/// </summary>
public sealed class WallpaperFader
{
    // Depois que o Windows confirma a troca, ele ainda leva um instante para desenhar o papel novo
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    // Fatia do tempo configurado gasta para escurecer (o resto é para clarear, que pede mais calma)
    private const double FadeOutShare = 0.4;

    private readonly WallpaperService _wallpaperService;
    private readonly SettingsService _settingsService;

    public WallpaperFader(WallpaperService wallpaperService, SettingsService settingsService)
    {
        _wallpaperService = wallpaperService;
        _settingsService = settingsService;
    }

    /// <summary>
    /// Aplica a imagem no monitor, com o fade para preto se estiver ligado nas configurações e se fizer sentido.
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

        TimeSpan total = TimeSpan.FromSeconds(Math.Clamp(settings.TransitionSeconds, 0.2, 3));
        TimeSpan fadeOut = total * FadeOutShare;
        TimeSpan fadeIn = total - fadeOut;

        BlackOverlay? overlay = null;
        try
        {
            overlay = BlackOverlay.TryCreate(monitor);
            if (overlay is not null)
            {
                await overlay.AnimateAsync(1, fadeOut);
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
            // A troca falhou: some com o preto na hora, para o usuário não ficar com o monitor escuro
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
            await overlay.AnimateAsync(0, fadeIn);
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
    /// A janela preta da transição: sem borda, sem foco, transparente ao mouse, logo acima da área de trabalho.
    /// </summary>
    private sealed class BlackOverlay : IDisposable
    {
        private const uint SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
        private const int WmNcHitTest = 0x0084, HtTransparent = -1;
        private const int GwHwndPrev = 3, GwlExStyle = -20;
        private const int WsExTopmost = 0x8;

        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);

        private readonly HwndSource _source;
        private readonly Rectangle _black;

        private BlackOverlay(HwndSource source, Rectangle black)
        {
            _source = source;
            _black = black;
        }

        public static BlackOverlay? TryCreate(MonitorInfo monitor)
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

            // Começa invisível (opacidade 0) e vai escurecendo
            var black = new Rectangle { Fill = Brushes.Black, Opacity = 0 };

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
                UsesPerPixelTransparency = true,                       // a opacidade do retângulo vale na tela
            };

            var source = new HwndSource(parameters) { RootVisual = black };
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

            return new BlackOverlay(source, black);
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

        /// <summary>Leva o preto até a opacidade pedida (1 = monitor todo escuro, 0 = some).</summary>
        public Task AnimateAsync(double opacity, TimeSpan duration)
        {
            var finished = new TaskCompletionSource();
            var animation = new DoubleAnimation(opacity, duration)
            {
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.HoldEnd,
            };
            animation.Completed += (_, _) => finished.TrySetResult();
            _black.BeginAnimation(UIElement.OpacityProperty, animation);

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
