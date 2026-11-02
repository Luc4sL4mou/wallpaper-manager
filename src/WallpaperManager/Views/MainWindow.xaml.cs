using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
                old.Catalog.PropertyChanged -= Catalog_PropertyChanged;
            }
            if (e.NewValue is MainViewModel vm)
            {
                vm.Catalog.PreviewRequested += Catalog_PreviewRequested;
                vm.Catalog.PropertyChanged += Catalog_PropertyChanged;
            }
        };
    }

    // Página nova no catálogo (lista trocada, não só acrescentada pela rolagem): volta para o topo
    private void Catalog_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CatalogViewModel.Items))
        {
            return;
        }

        // Depois que a tela já recebeu a lista nova
        Dispatcher.BeginInvoke(() =>
        {
            if (CatalogList.Items.Count > 0)
            {
                CatalogList.ScrollIntoView(CatalogList.Items[0]);
            }
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // Rolagem dentro da página do catálogo: perto do fim (ou se a lista ainda não enche a tela), pede mais papéis
    private void CatalogList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight) < LoadMoreThreshold)
        {
            vm.Catalog.LoadMore();
        }
    }

    private void Catalog_PreviewRequested(object? sender, EventArgs e)
    {
        if (sender is CatalogViewModel catalog)
        {
            new PreviewWindow(catalog) { Owner = this }.ShowDialog();
        }
    }

    // Rolagem infinita da Pasta local: perto do fim (ou se a lista ainda não enche a tela), pede mais imagens
    // Escolher na lista adiciona a tag (pelo valor em inglês) e esvazia o seletor para a próxima escolha
    private void TagPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TagPicker.SelectedItem is TagOption option && DataContext is MainViewModel vm)
        {
            vm.Catalog.AddTag(option.Value);
            TagPicker.SelectedItem = null;
            TagPicker.Text = string.Empty;
        }
    }

    // Tag digitada à mão (qualquer tag do Wallhaven vale)
    private void TagPicker_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm)
        {
            vm.Catalog.AddTag(TagPicker.Text);
            TagPicker.Text = string.Empty;
            e.Handled = true;
        }
    }

    private void LocalList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is MainViewModel vm && e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight) < LoadMoreThreshold)
        {
            vm.LocalFolder.LoadMore();
        }
    }
}
