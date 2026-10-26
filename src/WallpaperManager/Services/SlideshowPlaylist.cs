using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// A ordem em que as imagens de UM monitor são mostradas na apresentação.
/// Cada imagem aparece uma vez por "volta"; só depois de mostrar todas a lista recomeça.
/// No modo aleatório, cada volta tem uma ordem nova e a última imagem de uma volta nunca é a primeira da seguinte.
/// </summary>
public sealed class SlideshowPlaylist
{
    private readonly IReadOnlyList<LocalImage> _images;
    private readonly bool _shuffle;
    private readonly Random _random;
    private int[] _order = Array.Empty<int>();
    private int _position;
    private int _lastIndex = -1;

    public SlideshowPlaylist(IReadOnlyList<LocalImage> images, bool shuffle, Random? random = null)
    {
        _images = images;
        _shuffle = shuffle;
        _random = random ?? Random.Shared;
        _position = 0; // _order vazio: a primeira chamada de Next monta a primeira volta
    }

    public int Count => _images.Count;

    /// <summary>A próxima imagem da apresentação.</summary>
    public LocalImage Next()
    {
        if (_images.Count == 0)
        {
            throw new InvalidOperationException("A lista de imagens deste monitor está vazia.");
        }

        if (_position >= _order.Length)
        {
            StartNewRound();
        }

        _lastIndex = _order[_position++];
        return _images[_lastIndex];
    }

    private void StartNewRound()
    {
        _order = Enumerable.Range(0, _images.Count).ToArray();

        if (_shuffle)
        {
            // Embaralhamento de Fisher-Yates
            for (int i = _order.Length - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }

            // Evita mostrar a mesma imagem duas vezes seguidas na virada de uma volta para a outra
            if (_order.Length > 1 && _order[0] == _lastIndex)
            {
                int swapWith = 1 + _random.Next(_order.Length - 1);
                (_order[0], _order[swapWith]) = (_order[swapWith], _order[0]);
            }
        }

        _position = 0;
    }
}
