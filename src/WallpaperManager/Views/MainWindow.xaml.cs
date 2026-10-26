using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace WallpaperManager.Views;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();

        // Acompanha o tema do Windows (claro/escuro) e a cor de destaque enquanto o app está aberto
        SystemThemeWatcher.Watch(this);
    }
}
