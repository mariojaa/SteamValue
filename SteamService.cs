using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

public class SteamService
{
    private readonly HttpClient _httpClient;
    private const string SteamApiKey = "F4CAED645F0A7B3087195DDD23F74BA0";

    public SteamService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/121.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.AcceptLanguage.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("pt-BR", 0.9));
        _httpClient.DefaultRequestHeaders.AcceptLanguage.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("en-US", 0.8));
        _httpClient.DefaultRequestHeaders.AcceptLanguage.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("en", 0.7));
    }

    public async Task<string> ResolveSteamIdAsync(string profileUrl, Func<int, string, Task>? progressCallback = null)
    {
        if (progressCallback != null) await progressCallback(0, "Resolvendo SteamID...");
        var match = Regex.Match(profileUrl, @"profiles/(\d+)");
        if (match.Success)
        {
            if (progressCallback != null) await progressCallback(10, "SteamID encontrado na URL");
            return match.Groups[1].Value;
        }
        match = Regex.Match(profileUrl, @"id/([^/]+)");
        if (!match.Success)
        {
            throw new ArgumentException("URL de perfil inválida");
        }
        var vanity = match.Groups[1].Value;
        var response = await _httpClient.GetAsync($"https://api.steampowered.com/ISteamUser/ResolveVanityURL/v1/?key={SteamApiKey}&vanityurl={vanity}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        JsonElement data;
        try
        {
            data = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            throw new ArgumentException("Resposta inválida da API");
        }
        if (!data.TryGetProperty("response", out var resp)) throw new ArgumentException("Resposta inválida");
        if (!resp.TryGetProperty("success", out var success)) throw new ArgumentException("Não foi possível resolver o SteamID");
        int successValue = success.ValueKind == JsonValueKind.Number ? success.GetInt32() : (success.ValueKind == JsonValueKind.True ? 1 : 0);
        if (successValue != 1) throw new ArgumentException("Não foi possível resolver o SteamID");
        if (!resp.TryGetProperty("steamid", out var steamid)) throw new ArgumentException("SteamID não encontrado");
        if (progressCallback != null) await progressCallback(10, "SteamID resolvido");
        return steamid.GetString()!;
    }

    public async Task<List<Game>> GetOwnedGamesAsync(string steamId)
    {
        var response = await _httpClient.GetAsync($"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={SteamApiKey}&steamid={steamId}&include_appinfo=true");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        JsonElement data;
        try
        {
            data = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return new List<Game>();
        }
        if (!data.TryGetProperty("response", out var resp)) return new List<Game>();
        if (!resp.TryGetProperty("games", out var gamesElement)) return new List<Game>();
        var list = new List<Game>();
        foreach (var game in gamesElement.EnumerateArray())
        {
            if (game.TryGetProperty("appid", out var appid) && game.TryGetProperty("name", out var name))
            {
                list.Add(new Game
                {
                    AppId = appid.GetInt32(),
                    Name = name.GetString()!
                });
            }
        }
        return list;
    }

    // Novo método: obtém preço e imagem do app
    public async Task<(double price, string imageUrl)> GetAppDetailsAsync(int appId)
    {
        var response = await _httpClient.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&cc=br&l=pt");
        if (!response.IsSuccessStatusCode) return (0.0, string.Empty);
        var json = await response.Content.ReadAsStringAsync();
        JsonElement data;
        try
        {
            data = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return (0.0, string.Empty);
        }
        if (!data.TryGetProperty(appId.ToString(), out var app)) return (0.0, string.Empty);
        if (!app.TryGetProperty("success", out var success)) return (0.0, string.Empty);
        bool successBool = success.ValueKind == JsonValueKind.True || (success.ValueKind == JsonValueKind.Number && success.GetInt32() == 1);
        if (!successBool) return (0.0, string.Empty);
        if (!app.TryGetProperty("data", out var appData)) return (0.0, string.Empty);

        double price = 0.0;
        string imageUrl = string.Empty;

        if (appData.TryGetProperty("price_overview", out var priceOverview) && priceOverview.TryGetProperty("final", out var final))
        {
            price = final.GetDouble() / 100;
        }

        if (appData.TryGetProperty("header_image", out var headerImg))
        {
            imageUrl = headerImg.GetString() ?? string.Empty;
        }

        return (price, imageUrl);
    }

    public async Task<double> GetGamePriceAsync(int appId)
    {
        var response = await _httpClient.GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&cc=br&l=pt");
        if (!response.IsSuccessStatusCode) return 0.0;
        var json = await response.Content.ReadAsStringAsync();
        JsonElement data;
        try
        {
            data = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return 0.0;
        }
        if (!data.TryGetProperty(appId.ToString(), out var app)) return 0.0;
        if (!app.TryGetProperty("success", out var success)) return 0.0;
        bool successBool = success.ValueKind == JsonValueKind.True || (success.ValueKind == JsonValueKind.Number && success.GetInt32() == 1);
        if (!successBool) return 0.0;
        if (!app.TryGetProperty("data", out var appData)) return 0.0;
        if (!appData.TryGetProperty("price_overview", out var priceOverview)) return 0.0;
        if (!priceOverview.TryGetProperty("final", out var final)) return 0.0;
        return final.GetDouble() / 100;
    }

    public async Task<(double total, List<Game> games)> CalculateGamesValueAsync(string steamId, Func<int, string, Task>? progressCallback = null)
    {
        if (progressCallback != null) await progressCallback(15, "Buscando jogos da biblioteca...");
        var games = await GetOwnedGamesAsync(steamId);
        double total = 0.0;
        var resultGames = new List<Game>();
        int i = 0;
        foreach (var game in games)
        {
            var (price, imageUrl) = await GetAppDetailsAsync(game.AppId);
            game.Price = price;
            game.ImageUrl = imageUrl ?? string.Empty;
            total += price;
            resultGames.Add(game);
            i++;
            if (progressCallback != null) await progressCallback(20 + (i * 30 / Math.Max(games.Count, 1)), $"Calculando preço de {game.Name}...");
            await Task.Delay(600);
        }
        if (progressCallback != null) await progressCallback(50, "Jogos calculados");
        return (total, resultGames);
    }

    public async Task<JsonElement?> GetInventoryAsync(string steamId, int appId, int contextId = 2)
    {
        var url = $"https://steamcommunity.com/inventory/{steamId}/{appId}/{contextId}";
        var response = await _httpClient.GetAsync(url);
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            return null;
        }
        var json = await response.Content.ReadAsStringAsync();
        JsonElement data;
        try
        {
            data = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return null;
        }
        if (!data.TryGetProperty("success", out var success)) return null;
        bool successBool = success.ValueKind == JsonValueKind.True || (success.ValueKind == JsonValueKind.Number && success.GetInt32() == 1);
        if (!successBool)
        {
            return null;
        }
        if (!data.TryGetProperty("assets", out _))
        {
            return null;
        }
        return data;
    }

    public async Task<double> GetMarketPriceAsync(string name, int appId)
    {
        var response = await _httpClient.GetAsync($"https://steamcommunity.com/market/priceoverview/?appid={appId}&currency=7&market_hash_name={Uri.EscapeDataString(name)}");
        if (!response.IsSuccessStatusCode) return 0.0;
        var json = await response.Content.ReadAsStringAsync();
        JsonElement data;
        try
        {
            data = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return 0.0;
        }
        string? priceStr = null;
        if (data.TryGetProperty("lowest_price", out var lowest))
        {
            priceStr = lowest.GetString();
        }
        if (string.IsNullOrEmpty(priceStr) && data.TryGetProperty("median_price", out var median))
        {
            priceStr = median.GetString();
        }
        if (string.IsNullOrEmpty(priceStr)) return 0.0;
        priceStr = priceStr.Replace("R$", "").Replace(".", "").Replace(",", ".").Trim();
        if (double.TryParse(priceStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double result))
        {
            return result;
        }
        return 0.0;
    }

    // Updated: return structured inventory items instead of text lines
    public async Task<(double total, List<InventoryItem> items)> CalculateInventoryValueAsync(string steamId, int appId, string name, Func<int, string, Task>? progressCallback = null)
    {
        var itemsList = new List<InventoryItem>();
        var inv = await GetInventoryAsync(steamId, appId);
        if (inv == null)
        {
            return (0.0, itemsList);
        }
        if (!inv.Value.TryGetProperty("descriptions", out var descriptionsElement)) return (0.0, itemsList);
        var descriptions = new Dictionary<long, JsonElement>();
        foreach (var desc in descriptionsElement.EnumerateArray())
        {
            if (desc.TryGetProperty("classid", out var classId))
            {
                long cid;
                if (classId.ValueKind == JsonValueKind.Number)
                {
                    cid = classId.GetInt64();
                }
                else if (classId.ValueKind == JsonValueKind.String)
                {
                    if (!long.TryParse(classId.GetString(), out cid)) continue;
                }
                else continue;
                descriptions[cid] = desc;
            }
        }
        if (!inv.Value.TryGetProperty("assets", out var assetsElement)) return (0.0, itemsList);
        double total = 0.0;
        var assets = assetsElement.EnumerateArray().ToList();
        int j = 0;
        foreach (var asset in assets)
        {
            if (!asset.TryGetProperty("classid", out var classId)) continue;
            long cid;
            if (classId.ValueKind == JsonValueKind.Number)
            {
                cid = classId.GetInt64();
            }
            else if (classId.ValueKind == JsonValueKind.String)
            {
                if (!long.TryParse(classId.GetString(), out cid)) continue;
            }
            else continue;
            if (!descriptions.TryGetValue(cid, out var desc)) continue;
            if (!desc.TryGetProperty("marketable", out var marketable)) continue;
            bool isMarketable = marketable.ValueKind == JsonValueKind.True || (marketable.ValueKind == JsonValueKind.Number && marketable.GetInt32() == 1);
            if (!isMarketable) continue;
            if (!desc.TryGetProperty("market_hash_name", out var itemName)) continue;
            var itemNameStr = itemName.GetString()!;

            // try to get icon url from description
            string imageUrl = string.Empty;
            if (desc.TryGetProperty("icon_url_large", out var iconLarge) && iconLarge.ValueKind == JsonValueKind.String)
            {
                var icon = iconLarge.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(icon))
                {
                    imageUrl = BuildInventoryImageUrl(icon);
                }
            }
            else if (desc.TryGetProperty("icon_url", out var icon) && icon.ValueKind == JsonValueKind.String)
            {
                var iconStr = icon.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(iconStr))
                {
                    imageUrl = BuildInventoryImageUrl(iconStr);
                }
            }

            var price = await GetMarketPriceAsync(itemNameStr, appId);
            total += price;
            itemsList.Add(new InventoryItem { Name = itemNameStr, Price = price, ImageUrl = imageUrl });
            j++;
            if (progressCallback != null) await progressCallback(50 + (j * 40 / Math.Max(assets.Count, 1)), $"Calculando preço de {itemNameStr}...");
            await Task.Delay(1000);
        }
        return (total, itemsList);
    }

    private string BuildInventoryImageUrl(string icon)
    {
        // Some icon_url values are already full URLs, others are relative paths used with Steam CDN.
        if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return icon;
        // Remove leading slashes
        icon = icon.TrimStart('/');
        return $"https://steamcommunity-a.akamaihd.net/economy/image/{icon}";
    }
}

public class Game
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public string ImageUrl { get; set; } = "";
}

public class InventoryItem
{
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public string ImageUrl { get; set; } = "";
}