using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using WallpaperManager.Services;

namespace WallpaperManager.Views;

/// <summary>O que o menu do ícone precisa saber na hora de abrir (consultado a cada abertura).</summary>
public sealed record TrayMenuState(bool AnySlideshowRunning, bool StartWithWindows);

/// <summary>
/// Ícone do app perto do relógio (área de notificação), com menu. Feito direto com a API do Windows
/// (Shell_NotifyIcon) numa janela invisível, sem bibliotecas extras. Se o Explorer reiniciar, o ícone é recolocado.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10;
    private const int WmLButtonUp = 0x0202, WmLButtonDblClk = 0x0203, WmRButtonUp = 0x0205;
    private const uint MfString = 0x0, MfSeparator = 0x800, MfChecked = 0x8;
    private const uint TpmRightButton = 0x2, TpmReturnCmd = 0x100;
    private const int CmdOpen = 1, CmdStopAll = 2, CmdAutostart = 3, CmdExit = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    private readonly HwndSource _window;
    private readonly Icon _icon;
    private readonly uint _taskbarCreatedMessage;
    private readonly Func<TrayMenuState> _getState;
    private bool _added;

    public TrayIcon(Func<TrayMenuState> getState)
    {
        _getState = getState;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        // Janela invisível só para receber as mensagens do ícone (e o aviso de que o Explorer reiniciou)
        var parameters = new HwndSourceParameters("WallpaperManagerTray")
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, sem WS_VISIBLE
            ExtendedWindowStyle = 0x80,               // WS_EX_TOOLWINDOW
            Width = 0,
            Height = 0,
        };
        _window = new HwndSource(parameters);
        _window.AddHook(WndProc);

        using System.IO.Stream stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        int size = GetSystemMetrics(49); // SM_CXSMICON: o tamanho certo para a bandeja nesta tela (16, 20, 24...)
        _icon = new Icon(stream, new System.Drawing.Size(size, size));

        Add();
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? StopAllRequested;

    public event EventHandler? ToggleStartWithWindowsRequested;

    public event EventHandler? ExitRequested;

    private NotifyIconData NewData(uint flags) => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _window.Handle,
        Id = 1,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _icon.Handle,
        Tip = "Wallpaper Manager",
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private void Add()
    {
        NotifyIconData data = NewData(NifMessage | NifIcon | NifTip);
        _added = Shell_NotifyIcon(NimAdd, ref data);
    }

    /// <summary>Aviso curto do Windows ao lado do relógio (usado uma única vez, para explicar o segundo plano).</summary>
    public void ShowBalloon(string title, string text)
    {
        if (!_added)
        {
            return;
        }

        NotifyIconData data = NewData(NifInfo);
        data.InfoTitle = title;
        data.Info = text;
        data.InfoFlags = 1; // NIIF_INFO
        Shell_NotifyIcon(NimModify, ref data);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage)
        {
            Add(); // o Explorer reiniciou e apagou o ícone: coloca de novo
            handled = true;
        }
        else if (msg == CallbackMessage)
        {
            switch ((int)lParam)
            {
                case WmLButtonUp or WmLButtonDblClk:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case WmRButtonUp:
                    ShowMenu();
                    break;
            }

            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        TrayMenuState state = _getState();
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MfString, (UIntPtr)CmdOpen, "Abrir o Wallpaper Manager");
            if (state.AnySlideshowRunning)
            {
                AppendMenu(menu, MfString, (UIntPtr)CmdStopAll, "Parar as apresentações de slides");
            }

            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, MfString | (state.StartWithWindows ? MfChecked : 0), (UIntPtr)CmdAutostart, "Iniciar com o Windows");
            AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            AppendMenu(menu, MfString, (UIntPtr)CmdExit, "Sair");
            SetMenuDefaultItem(menu, CmdOpen, 0);

            GetCursorPos(out Point cursor);
            SetForegroundWindow(_window.Handle); // sem isto o menu não fecha quando se clica fora dele
            int command = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd, cursor.X, cursor.Y, _window.Handle, IntPtr.Zero);
            PostMessage(_window.Handle, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CmdOpen: OpenRequested?.Invoke(this, EventArgs.Empty); break;
                case CmdStopAll: StopAllRequested?.Invoke(this, EventArgs.Empty); break;
                case CmdAutostart: ToggleStartWithWindowsRequested?.Invoke(this, EventArgs.Empty); break;
                case CmdExit: ExitRequested?.Invoke(this, EventArgs.Empty); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            NotifyIconData data = NewData(0);
            Shell_NotifyIcon(NimDelete, ref data);
            _added = false;
        }

        _window.RemoveHook(WndProc);
        _window.Dispose();
        _icon.Dispose();
    }
}
