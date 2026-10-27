using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WallpaperManager.Models;
using WallpaperManager.ViewModels;

namespace WallpaperManager.Views;

/// <summary>
/// Visualização em tela cheia de um papel do catálogo. Usa o próprio CatalogViewModel como DataContext,
/// então "Baixar" e "Baixar e aplicar" são exatamente os mesmos comandos da aba.
/// </summary>
public partial class PreviewWindow : Window
{
    private readonly CatalogViewModel _catalog;
    private BitmapImage? _loading;

    public PreviewWindow(CatalogViewModel catalog)
    {
        InitializeComponent();
        _catalog = catalog;
        DataContext = catalog;

        catalog.PropertyChanged += Catalog_PropertyChanged;
        Closed += (_, _) => catalog.PropertyChanged -= Catalog_PropertyChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => LoadImage();
    }

    private void Catalog_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CatalogViewModel.SelectedTile))
        {
            if (_catalog.SelectedTile is null)
            {
                Close(); // a página mudou ou o monitor foi trocado: não há mais o que mostrar
                return;
            }

            LoadImage();
        }
    }

    private void LoadImage()
    {
        WallpaperItem? item = _catalog.SelectedTile?.Item;
        if (item is null)
        {
            return;
        }

        // A miniatura aparece na hora; a original (que pode ter vários MB) substitui quando chegar
        PlaceholderImage.Source = TryCreate(item.ThumbnailUrl, decodeWidth: 0);
        FullImage.Source = null;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = "Carregando a imagem original…";

        // Decodifica no tamanho da tela: uma imagem 8K inteira na memória não deixaria a visualização melhor
        int screenWidth = (int)(SystemParameters.PrimaryScreenWidth * DpiScaleX());
        BitmapImage? full = TryCreate(item.ImageUrl, decodeWidth: Math.Min(item.Width, Math.Max(screenWidth, 1920)));
        _loading = full;
        if (full is null)
        {
            LoadingText.Text = "Não foi possível carregar a imagem original.";
            return;
        }

        if (!full.IsDownloading)
        {
            ShowFull(full);
            return;
        }

        full.DownloadCompleted += (_, _) =>
        {
            if (ReferenceEquals(_loading, full))
            {
                ShowFull(full);
            }
        };
        full.DownloadFailed += (_, _) =>
        {
            if (ReferenceEquals(_loading, full))
            {
                LoadingText.Text = "Não foi possível carregar a imagem original (mostrando a miniatura).";
            }
        };
    }

    private void ShowFull(BitmapImage full)
    {
        FullImage.Source = full;
        LoadingPanel.Visibility = Visibility.Collapsed;
    }

    private double DpiScaleX() => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    private static BitmapImage? TryCreate(string url, int decodeWidth)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(url);
            if (decodeWidth > 0)
            {
                bitmap.DecodePixelWidth = decodeWidth;
            }
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Setas só navegam quando o foco não está na caixa de monitores (lá elas trocam o item)
        bool inCombo = Keyboard.FocusedElement is System.Windows.Controls.ComboBox or System.Windows.Controls.ComboBoxItem;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Left when !inCombo:
                _catalog.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Right when !inCombo:
                _catalog.MoveSelection(+1);
                e.Handled = true;
                break;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Previous_Click(object sender, RoutedEventArgs e) => _catalog.MoveSelection(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => _catalog.MoveSelection(+1);
}
