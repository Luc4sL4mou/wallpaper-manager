using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using WallpaperManager.ViewModels;

namespace WallpaperManager.Views;

public partial class MainWindow : FluentWindow
{
    // Quando faltar menos que isto (em pixels) para o fim da lista, o próximo lote de imagens é carregado
    private const double LoadMoreThreshold = 600;

    public MainWindow()
    {
        InitializeComponent();

        // Acompanha o tema do Windows (claro/escuro) e a cor de destaque enquanto o app está aberto
        SystemThemeWatcher.Watch(this);

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old)
            {
                old.Catalog.PreviewRequested -= Catalog_PreviewRequested;
            }
            if (e.NewValue is MainViewModel vm)
            {
                vm.Catalog.PreviewRequested += Catalog_PreviewRequested;
            }
        };
    }

    private void Catalog_PreviewRequested(object? sender, EventArgs e)
    {
        if (sender is CatalogViewModel catalog)
        {
            new PreviewWindow(catalog) { Owner = this }.ShowDialog();
        }
    }

    // Rolagem infinita da Pasta local: perto do fim (ou se a lista ainda não enche a tela), pede mais imagens
    private void LocalList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight) < LoadMoreThreshold)
        {
            vm.LocalFolder.LoadMore();
        }
    }
}
