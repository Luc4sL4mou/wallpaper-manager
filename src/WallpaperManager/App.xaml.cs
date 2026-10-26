using System.ComponentModel;
using System.Windows;
using WallpaperManager.Services;
using WallpaperManager.ViewModels;
using WallpaperManager.Views;
using Wpf.Ui.Appearance;

namespace WallpaperManager;

public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private SettingsService? _settingsService;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        RegisterGlobalErrorHandlers();

        // Uma só cópia por vez: se o app já roda em segundo plano, esta cópia só pede para ele mostrar a janela
        _singleInstance = SingleInstance.Acquire(AppPaths.DataFolder);
        if (!_singleInstance.IsFirst)
        {
            _singleInstance.SignalFirstInstance();
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        // O app é de segundo plano: fechar a janela não encerra o programa. Só "Sair" (ou o fim da sessão do Windows).
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SessionEnding += (_, _) => _exiting = true;

        // Aplica o tema do Windows antes de abrir a janela, para não "piscar" claro no modo escuro
        ApplicationThemeManager.ApplySystemTheme();

        // Composição manual: cria os serviços e entrega ao ViewModel pelo construtor
        var monitorService = new MonitorService();
        var wallpaperService = new WallpaperService();
        var wallhavenService = new WallhavenService();
        _settingsService = new SettingsService();
        var downloadService = new DownloadService(_settingsService);
        var localFolderService = new LocalFolderService();
        var autostartService = new AutostartService();
        _viewModel = new MainViewModel(
            monitorService, wallpaperService, wallhavenService, downloadService, _settingsService,
            localFolderService, autostartService, ExitApplication);

        _window = new MainWindow { DataContext = _viewModel };
        _window.Closing += OnMainWindowClosing;

        // Ícone perto do relógio. Se não for possível criá-lo, o app NÃO pode ficar invisível: sem ícone, ele se
        // comporta como um programa comum (a janela aparece e fechar a janela encerra o app).
        try
        {
            TrayIcon tray = new(() => new TrayMenuState(_viewModel.Slideshows.AnyRunning, _viewModel.Settings.StartWithWindows));
            tray.OpenRequested += (_, _) => ShowMainWindow();
            tray.StopAllRequested += (_, _) => _viewModel.Slideshows.StopAll();
            tray.ToggleStartWithWindowsRequested += (_, _) => _viewModel.Settings.StartWithWindows = !_viewModel.Settings.StartWithWindows;
            tray.ExitRequested += (_, _) => ExitApplication();
            _tray = tray;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Não foi possível criar o ícone da bandeja", ex);
            _tray = null;
        }

        // Uma segunda cópia (ex.: o atalho) avisa por aqui; a janela é mostrada na thread da interface
        _singleInstance.ListenForSignal(() => Dispatcher.BeginInvoke(ShowMainWindow));

        // Aberto pelo Windows no logon (--minimized): fica só o ícone, sem janela (se houver ícone)
        bool startHidden = _tray is not null
                           && e.Args.Contains(AutostartService.MinimizedArgument, StringComparer.OrdinalIgnoreCase);
        if (!startHidden)
        {
            ShowMainWindow();
        }

        // No logon o Windows ainda está montando a sessão: espera um pouco antes de retomar as apresentações
        _viewModel.ScheduleResume(startHidden ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Mostra a janela principal (e a traz para a frente), esteja ela escondida, minimizada ou aberta.
    /// </summary>
    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Maximized;
        }

        _window.Activate();
        _window.Topmost = true; // truque para o Windows realmente trazer a janela para a frente...
        _window.Topmost = false; // ...sem deixá-la "sempre no topo"
        _window.Focus();
    }

    /// <summary>
    /// Fechar a janela (X) só a esconde, a menos que o usuário tenha desligado "continuar em segundo plano".
    /// </summary>
    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _settingsService is null)
        {
            return; // fechando de verdade (Sair, fim da sessão do Windows)
        }

        if (_tray is null || !_settingsService.Current.RunInBackground)
        {
            ExitApplication(); // sem ícone (ou com o segundo plano desligado), fechar a janela encerra o app
            return;
        }

        e.Cancel = true;
        _window?.Hide();

        // Na primeira vez, explica onde o app foi parar
        if (!_settingsService.Current.TrayHintShown)
        {
            _settingsService.Current.TrayHintShown = true;
            _settingsService.Save();
            _tray.ShowBalloon(
                "Wallpaper Manager continua rodando",
                "O app ficou em segundo plano, perto do relógio. Clique no ícone para abrir; clique com o botão direito e escolha Sair para encerrar.");
        }
    }

    /// <summary>
    /// Encerra o app de vez. As apresentações que estavam rodando voltam sozinhas na próxima vez que ele abrir.
    /// </summary>
    public void ExitApplication()
    {
        if (_exiting && _tray is null)
        {
            return;
        }

        _exiting = true;
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// Rede de segurança: um erro que ninguém previu é registrado em arquivo e explicado ao usuário,
    /// em vez de o app simplesmente sumir.
    /// </summary>
    private void RegisterGlobalErrorHandlers()
    {
        // Erro na thread da interface: o app continua aberto
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write("Erro não tratado na interface", args.Exception);
            MessageBox.Show(
                $"Ocorreu um erro inesperado:\n\n{args.Exception.Message}\n\nOs detalhes foram salvos em:\n{AppPaths.LogFile}",
                "Wallpaper Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        // Erro em outra thread: não dá para continuar, mas deixa o registro
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ErrorLog.Write("Erro fatal", args.ExceptionObject as Exception);

        // Tarefa assíncrona que falhou sem ninguém esperar o resultado
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("Erro em tarefa em segundo plano", args.Exception);
            args.SetObserved();
        };
    }
}
