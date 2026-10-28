namespace WallpaperManager.ViewModels;

/// <summary>
/// Uma tag escolhida no filtro do catálogo, mostrada como "etiqueta" com um X para removê-la.
/// </summary>
public sealed class TagChipViewModel
{
    public TagChipViewModel(string value, Action<TagChipViewModel> remove)
    {
        Value = value;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public string Value { get; }

    public RelayCommand RemoveCommand { get; }
}

/// <summary>
/// Sugestão de tag no seletor. Label é o texto mostrado; Value é a tag em inglês que o Wallhaven entende.
/// </summary>
public sealed record TagOption(string Label, string Value)
{
    public static IReadOnlyList<TagOption> Defaults { get; } = new[]
    {
        new TagOption("Anime", "anime"),
        new TagOption("Animais", "animals"),
        new TagOption("Arquitetura", "architecture"),
        new TagOption("Carros", "cars"),
        new TagOption("Cidade", "city"),
        new TagOption("Ciberpunk", "cyberpunk"),
        new TagOption("Deserto", "desert"),
        new TagOption("Dragão", "dragon"),
        new TagOption("Espaço", "space"),
        new TagOption("Fantasia", "fantasy"),
        new TagOption("Flores", "flowers"),
        new TagOption("Floresta", "forest"),
        new TagOption("Gato", "cat"),
        new TagOption("Jogos", "video games"),
        new TagOption("Lago", "lake"),
        new TagOption("Lua", "moon"),
        new TagOption("Minimalista", "minimalism"),
        new TagOption("Montanhas", "mountains"),
        new TagOption("Natureza", "nature"),
        new TagOption("Neon", "neon"),
        new TagOption("Noite", "night"),
        new TagOption("Nuvens", "clouds"),
        new TagOption("Oceano", "ocean"),
        new TagOption("Paisagem", "landscape"),
        new TagOption("Pôr do sol", "sunset"),
        new TagOption("Praia", "beach"),
        new TagOption("Robô", "robot"),
        new TagOption("Ficção científica", "science fiction"),
        new TagOption("Neve", "snow"),
        new TagOption("Estrelas", "stars"),
        new TagOption("Super-heróis", "superhero"),
        new TagOption("Veículos", "vehicle"),
    };
}
