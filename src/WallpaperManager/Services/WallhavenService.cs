using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using WallpaperManager.Models;

namespace WallpaperManager.Services;

/// <summary>
/// Consulta a API v1 do Wallhaven. Buscas idênticas são respondidas pelo cache por alguns minutos,
/// e um limitador local evita estourar o limite da API (45 requisições por minuto).
/// </summary>
public sealed class WallhavenService
{
    private const string SearchUrl = "https://wallhaven.cc/api/v1/search";
    private const string SettingsUrl = "https://wallhaven.cc/api/v1/settings";

    // O limite oficial é 45/min; ficamos abaixo para sobrar margem
    private const int MaxRequestsPerMinute = 40;

    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);

    // Proporções que o filtro "ratios" do Wallhaven aceita
    private static readonly (string Name, double Value)[] StandardRatios =
    {
        ("16x9", 16.0 / 9), ("16x10", 16.0 / 10), ("21x9", 21.0 / 9),
        ("32x9", 32.0 / 9), ("48x9", 48.0 / 9),
        ("9x16", 9.0 / 16), ("10x16", 10.0 / 16), ("9x18", 9.0 / 18),
        ("1x1", 1.0), ("3x2", 3.0 / 2), ("4x3", 4.0 / 3), ("5x4", 5.0 / 4),
    };

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private readonly Dictionary<string, (Task<WallhavenPage> Task, DateTime ExpiresAt)> _cache = new();
    private readonly Queue<DateTime> _requestTimes = new();
    private string? _apiKey;

    public WallhavenService()
    {
        // Um único HttpClient para o app inteiro (criar um por requisição esgota conexões)
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("WallpaperManager/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    /// <summary>
    /// Chave de API opcional. Fica nas configurações do usuário (etapa 5), nunca no código.
    /// </summary>
    public string? ApiKey
    {
        get => _apiKey;
        set
        {
            lock (_gate)
            {
                _apiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                _cache.Clear(); // com outra chave os resultados podem mudar
            }
        }
    }

    /// <summary>
    /// A proporção padrão do Wallhaven mais próxima da do monitor (ex.: 1360×768 → 16x9).
    /// </summary>
    public static string GetClosestRatio(MonitorInfo monitor)
    {
        double aspect = monitor.AspectRatio;
        return StandardRatios.MinBy(r => Math.Abs(Math.Log(r.Value / aspect))).Name;
    }

    /// <summary>
    /// Diferença relativa entre a proporção do monitor e a proporção padrão escolhida.
    /// Acima de 1% a busca é só uma aproximação, e a validação local é quem garante o resultado.
    /// </summary>
    public static double GetRatioDeviation(MonitorInfo monitor)
    {
        string name = GetClosestRatio(monitor);
        double standard = StandardRatios.First(r => r.Name == name).Value;
        return Math.Abs(standard - monitor.AspectRatio) / monitor.AspectRatio;
    }

    public async Task<WallhavenPage> SearchAsync(WallhavenQuery query, CancellationToken cancellationToken)
    {
        string url = BuildUrl(query);
        Task<WallhavenPage> task;

        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;
            if (_cache.TryGetValue(url, out var cached) && cached.ExpiresAt > now
                && cached.Task.Status != TaskStatus.Faulted)
            {
                task = cached.Task; // busca idêntica: reaproveita (inclusive se ainda estiver em andamento)
            }
            else
            {
                EnsureWithinRateLimit(now);
                task = FetchAsync(url);

                foreach (string expired in _cache.Where(e => e.Value.ExpiresAt <= now).Select(e => e.Key).ToList())
                {
                    _cache.Remove(expired);
                }
                _cache[url] = (task, now + CacheLifetime);
            }
        }

        // A requisição compartilhada continua mesmo se este chamador cancelar, e o resultado vai para o cache
        return await task.WaitAsync(cancellationToken);
    }

    private static string BuildUrl(WallhavenQuery q)
    {
        string text = q.Text.Trim();
        string search = text.Length > 0 ? $"&q={Uri.EscapeDataString(text)}" : string.Empty;

        return $"{SearchUrl}?atleast={q.MinWidth}x{q.MinHeight}&ratios={q.Ratio}" +
               $"&sorting={q.Sorting}&order=desc&categories={q.Categories}&purity={q.Purity}&page={q.Page}{search}";
    }

    /// <summary>
    /// Deve ser chamado dentro do lock. Registra a requisição ou recusa se o limite local foi atingido.
    /// </summary>
    private void EnsureWithinRateLimit(DateTime now)
    {
        while (_requestTimes.Count > 0 && now - _requestTimes.Peek() > TimeSpan.FromMinutes(1))
        {
            _requestTimes.Dequeue();
        }

        if (_requestTimes.Count >= MaxRequestsPerMinute)
        {
            int wait = (int)Math.Ceiling(60 - (now - _requestTimes.Peek()).TotalSeconds);
            throw new WallhavenException($"Muitas buscas em pouco tempo. Aguarde cerca de {wait} s e tente de novo.");
        }

        _requestTimes.Enqueue(now);
    }

    private async Task<WallhavenPage> FetchAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            string? apiKey = _apiKey;
            if (apiKey is not null)
            {
                request.Headers.Add("X-API-Key", apiKey); // cabeçalho: a chave não aparece na URL nem no cache
            }

            using HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new WallhavenException(DescribeHttpError(response.StatusCode));
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParsePage(json);
        }
        catch (Exception ex)
        {
            // Falhas não ficam no cache: a próxima tentativa refaz a busca
            lock (_gate)
            {
                _cache.Remove(url);
            }

            throw ToWallhavenException(ex);
        }
    }

    private static Exception ToWallhavenException(Exception ex) => ex switch
    {
        WallhavenException => ex,
        HttpRequestException => new WallhavenException("Não foi possível conectar ao Wallhaven. Verifique sua internet.", ex),
        TaskCanceledException => new WallhavenException("O Wallhaven demorou demais para responder. Tente novamente.", ex),
        JsonException => new WallhavenException("O Wallhaven devolveu uma resposta inesperada.", ex),
        _ => ex,
    };

    /// <summary>
    /// Pergunta ao Wallhaven se a chave é válida (endpoint /settings, que exige chave).
    /// Devolve false se a chave foi recusada; lança WallhavenException se não deu para saber (rede, limite...).
    /// </summary>
    public async Task<bool> IsApiKeyValidAsync(string apiKey, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            EnsureWithinRateLimit(DateTime.UtcNow);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, SettingsUrl);
            request.Headers.Add("X-API-Key", apiKey);

            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // Observado na API real: uma chave inexistente no /settings devolve 404 (e não 401, que é o que
            // o mesmo endpoint responde quando não há chave nenhuma). Os dois significam "chave recusada".
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound)
            {
                return false;
            }

            throw new WallhavenException(DescribeHttpError(response.StatusCode));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw ToWallhavenException(ex);
        }
    }

    private static string DescribeHttpError(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => "O Wallhaven recusou por excesso de requisições (limite de 45 por minuto). Aguarde um minuto.",
        HttpStatusCode.Unauthorized => "O Wallhaven recusou a chave de API ou o conteúdo pedido exige uma chave.",
        _ => $"O Wallhaven respondeu com erro {(int)status} ({status}).",
    };

    private static WallhavenPage ParsePage(string json)
    {
        SearchResponse response = JsonSerializer.Deserialize<SearchResponse>(json)
                                  ?? throw new JsonException("Resposta vazia.");

        var items = (response.Data ?? new List<WallpaperDto>())
            .Select(d => new WallpaperItem(
                Id: d.Id,
                PageUrl: d.Url,
                ImageUrl: d.Path,
                ThumbnailUrl: d.Thumbs.Large,
                Width: d.DimensionX,
                Height: d.DimensionY,
                FileSize: d.FileSize))
            .ToList();

        return new WallhavenPage(
            items,
            CurrentPage: response.Meta?.CurrentPage ?? 1,
            LastPage: Math.Max(response.Meta?.LastPage ?? 1, 1),
            Total: response.Meta?.Total ?? items.Count);
    }

    // Formato do JSON da API. Só os campos que usamos; o resto é ignorado.
    private sealed class SearchResponse
    {
        [JsonPropertyName("data")] public List<WallpaperDto>? Data { get; set; }
        [JsonPropertyName("meta")] public MetaDto? Meta { get; set; }
    }

    private sealed class WallpaperDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("dimension_x")] public int DimensionX { get; set; }
        [JsonPropertyName("dimension_y")] public int DimensionY { get; set; }
        [JsonPropertyName("file_size")] public long FileSize { get; set; }
        [JsonPropertyName("thumbs")] public ThumbsDto Thumbs { get; set; } = new();
    }

    private sealed class ThumbsDto
    {
        [JsonPropertyName("large")] public string Large { get; set; } = "";
    }

    private sealed class MetaDto
    {
        [JsonPropertyName("current_page")] public int CurrentPage { get; set; }
        [JsonPropertyName("last_page")] public int LastPage { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }
    }
}
