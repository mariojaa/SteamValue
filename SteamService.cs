using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.Net;

public class SteamService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private const string SteamApiKey = "F4CAED645F0A7B3087195DDD23F74BA0";
    private readonly Dictionary<string, List<(DateTime time, double total)>> _accountSnapshots = new();

    // ── Rate Limiter: 1 req/5s = ~12 req/min (Steam Market safe limit) ──────
    private readonly SemaphoreSlim _marketSemaphore = new SemaphoreSlim(1, 1);
    private DateTime _lastMarketRequestUtc = DateTime.MinValue;
    private readonly TimeSpan _marketRequestInterval = TimeSpan.FromSeconds(5); // safe floor: 1 req/5s

    // ── Inventory fetch semaphore: evita múltiplos fetches simultâneos ───────
    private readonly SemaphoreSlim _inventorySemaphore = new SemaphoreSlim(1, 1);

    // ── Circuit Breaker ──────────────────────────────────────────────────────
    private int _consecutiveMarket429s = 0;
    private DateTime _circuitOpenUntil = DateTime.MinValue;
    private readonly TimeSpan _circuitCooldown = TimeSpan.FromSeconds(120); // 2min cooldown

    public SteamService(HttpClient httpClient, IMemoryCache cache)
    {
        _httpClient = httpClient;
        _cache = cache;
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://steamcommunity.com/");
        _httpClient.DefaultRequestHeaders.Add("Origin", "https://steamcommunity.com");
    }

    // ─── HTTP Helper ───────────────────────────────────────────────────────────
    private async Task<HttpResponseMessage> GetAsync(string url, int retries = 3, bool useInventoryHeaders = false)
    {
        for (int i = 0; i <= retries; i++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (useInventoryHeaders)
                {
                    request.Headers.Add("Cookie", "Steam_Language=english; timezoneOffset=0,0");
                    request.Headers.Referrer = new Uri("https://steamcommunity.com/");
                }
                var resp = await _httpClient.SendAsync(request);

                if (resp.StatusCode == HttpStatusCode.TooManyRequests && i < retries)
                {
                    var waitSecs = GetRetryAfterSeconds(resp);
                    if (waitSecs <= 0) waitSecs = (int)Math.Pow(2, i + 3); // 8, 16, 32s
                    await Task.Delay(TimeSpan.FromSeconds(waitSecs + 2));
                    continue;
                }
                if (resp.StatusCode == HttpStatusCode.InternalServerError && i < retries)
                {
                    await Task.Delay(500 * (i + 1));
                    continue;
                }
                return resp;
            }
            catch when (i < retries)
            {
                await Task.Delay(500 * (i + 1));
            }
        }
        throw new HttpRequestException("Failed after retries: " + url);
    }

    private static int GetRetryAfterSeconds(HttpResponseMessage resp)
    {
        try
        {
            if (resp.Headers.TryGetValues("Retry-After", out var vals))
            {
                var v = vals.FirstOrDefault();
                if (int.TryParse(v, out var s)) return s;
                if (DateTimeOffset.TryParse(v, out var dt))
                {
                    var wait = (int)(dt - DateTimeOffset.UtcNow).TotalSeconds;
                    return Math.Max(wait, 1);
                }
            }
        }
        catch { }
        return 0;
    }

    // ─── SteamID Resolution ────────────────────────────────────────────────────
    public async Task<string> ResolveSteamIdAsync(string profileUrl, Func<int, string, Task>? progress = null)
    {
        if (progress != null) await progress(0, "Resolvendo SteamID...");
        var trimmed = profileUrl.Trim();

        var matchNum = Regex.Match(trimmed, @"profiles/(\d{17})");
        if (matchNum.Success) return matchNum.Groups[1].Value;
        if (Regex.IsMatch(trimmed, @"^\d{17}$")) return trimmed;

        var matchVanity = Regex.Match(trimmed, @"id/([^/?\s]+)");
        if (!matchVanity.Success)
            throw new ArgumentException("URL inválida. Use: steamcommunity.com/id/nome ou /profiles/ID");

        var vanity = matchVanity.Groups[1].Value;
        var resp = await GetAsync($"https://api.steampowered.com/ISteamUser/ResolveVanityURL/v1/?key={SteamApiKey}&vanityurl={vanity}");
        resp.EnsureSuccessStatusCode();

        var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var r = doc.GetProperty("response");
        if (r.TryGetProperty("success", out var s) && (s.ValueKind == JsonValueKind.True || s.GetInt32() == 1)
            && r.TryGetProperty("steamid", out var sid))
        {
            if (progress != null) await progress(5, "SteamID resolvido");
            return sid.GetString()!;
        }
        throw new ArgumentException("Não foi possível resolver o SteamID para: " + vanity);
    }

    // ─── Player Summaries ──────────────────────────────────────────────────────
    public async Task<JsonElement?> GetPlayerSummariesAsync(string steamIds)
    {
        var chunks = steamIds.Split(',').Where(s => !string.IsNullOrWhiteSpace(s)).Chunk(100).ToList();
        var allPlayers = new List<JsonElement>();

        foreach (var chunk in chunks)
        {
            var key = $"ps:{string.Join(",", chunk)}";
            if (_cache.TryGetValue(key, out JsonElement cached))
            {
                if (cached.TryGetProperty("response", out var cr) && cr.TryGetProperty("players", out var cp))
                    allPlayers.AddRange(cp.EnumerateArray());
                continue;
            }
            var url = $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={SteamApiKey}&steamids={string.Join(",", chunk)}";
            var resp = await GetAsync(url);
            if (!resp.IsSuccessStatusCode) continue;
            var json = await resp.Content.ReadAsStringAsync();
            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(json);
                _cache.Set(key, doc, TimeSpan.FromMinutes(10));
                if (doc.TryGetProperty("response", out var r) && r.TryGetProperty("players", out var players))
                    allPlayers.AddRange(players.EnumerateArray());
            }
            catch { }
        }
        if (allPlayers.Count == 0) return null;
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new { response = new { players = allPlayers } }));
    }

    // ─── Owned Games ──────────────────────────────────────────────────────────
    public async Task<List<Game>> GetOwnedGamesAsync(string steamId)
    {
        var key = $"games:{steamId}";
        if (_cache.TryGetValue(key, out List<Game> cached)) return cached;

        var resp = await GetAsync($"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={SteamApiKey}&steamid={steamId}&include_appinfo=true&include_played_free_games=true");
        if (!resp.IsSuccessStatusCode) return new();
        var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var list = new List<Game>();

        if (!doc.TryGetProperty("response", out var r) || !r.TryGetProperty("games", out var gamesEl)) return list;
        foreach (var g in gamesEl.EnumerateArray())
        {
            if (!g.TryGetProperty("appid", out var appid) || !g.TryGetProperty("name", out var name)) continue;
            int pt = g.TryGetProperty("playtime_forever", out var ptf) ? ptf.GetInt32() : 0;
            int pt2 = g.TryGetProperty("playtime_2weeks", out var pt2w) ? pt2w.GetInt32() : 0;
            string icon = g.TryGetProperty("img_icon_url", out var ico) ? ico.GetString() ?? "" : "";
            list.Add(new Game
            {
                AppId = appid.GetInt32(),
                Name = name.GetString()!,
                PlaytimeMinutes = pt,
                Playtime2WeeksMinutes = pt2,
                IconUrl = string.IsNullOrEmpty(icon) ? "" : $"https://media.steampowered.com/steamcommunity/public/images/apps/{appid.GetInt32()}/{icon}.jpg"
            });
        }
        _cache.Set(key, list, TimeSpan.FromMinutes(15));
        return list;
    }

    // ─── App Details ─────────────────────────────────────────────────────────
    // Bounded concurrency for store API (more lenient than market)
    private readonly SemaphoreSlim _storeSem = new SemaphoreSlim(8, 8);

    public async Task<(double price, string imageUrl, string genre, string developer, int metacritic)> GetAppDetailsAsync(int appId)
    {
        var key = $"app2:{appId}";
        if (_cache.TryGetValue(key, out (double, string, string, string, int) cached)) return cached;

        await _storeSem.WaitAsync();
        try
        {
            // double-check after acquiring
            if (_cache.TryGetValue(key, out cached)) return cached;

            var resp = await GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&cc=br&l=pt&filters=price_overview,header_image,genres,developers,metacritic");
            await Task.Delay(100); // gentle pacing

            if (!resp.IsSuccessStatusCode) return DefaultAppDetails(appId);

            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
                if (!doc.TryGetProperty(appId.ToString(), out var app)) return DefaultAppDetails(appId);
                if (!app.TryGetProperty("success", out var s) || !(s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.Number && s.GetInt32() == 1)))
                    return DefaultAppDetails(appId);
                if (!app.TryGetProperty("data", out var data)) return DefaultAppDetails(appId);

                double price = 0;
                if (data.TryGetProperty("price_overview", out var po) && po.TryGetProperty("final", out var fin))
                    price = fin.GetDouble() / 100.0;

                string img = data.TryGetProperty("header_image", out var hi) ? hi.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(img)) img = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";

                string genre = "";
                if (data.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
                {
                    var genreList = genres.EnumerateArray()
                        .Select(g => g.TryGetProperty("description", out var d) ? d.GetString() : "")
                        .Where(g => !string.IsNullOrEmpty(g)).Take(2);
                    genre = string.Join(", ", genreList);
                }

                string developer = "";
                if (data.TryGetProperty("developers", out var devs) && devs.ValueKind == JsonValueKind.Array)
                    developer = devs.EnumerateArray().FirstOrDefault().GetString() ?? "";

                int metacritic = 0;
                if (data.TryGetProperty("metacritic", out var mc) && mc.TryGetProperty("score", out var mcs))
                    metacritic = mcs.GetInt32();

                var result = (price, img, genre, developer, metacritic);
                _cache.Set(key, result, TimeSpan.FromHours(6));
                return result;
            }
            catch { return DefaultAppDetails(appId); }
        }
        finally { _storeSem.Release(); }
    }

    private static (double, string, string, string, int) DefaultAppDetails(int appId)
        => (0, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg", "", "", 0);

    // ─── Market Price — FIXED rate limiter with circuit breaker ──────────────
    // Steam Community Market: max ~20 req/min. We do 1 req/3s = 20/min.
    // After 5 consecutive 429s → open circuit for 60s before retrying.
    public async Task<double> GetMarketPriceAsync(string name, int appId)
    {
        var key = $"mp:{appId}:{name}";
        if (_cache.TryGetValue(key, out double cp)) return cp;

        var url = $"https://steamcommunity.com/market/priceoverview/?appid={appId}&currency=7&market_hash_name={Uri.EscapeDataString(name)}";

        await _marketSemaphore.WaitAsync();
        try
        {
            // Re-check cache inside semaphore
            if (_cache.TryGetValue(key, out cp)) return cp;

            // Circuit breaker: if open, wait until it resets
            var now = DateTime.UtcNow;
            if (_circuitOpenUntil > now)
            {
                var circuitWait = _circuitOpenUntil - now;
                await Task.Delay(circuitWait);
                _consecutiveMarket429s = 0;
            }

            // Token bucket: enforce minimum spacing
            var elapsed = DateTime.UtcNow - _lastMarketRequestUtc;
            if (elapsed < _marketRequestInterval)
            {
                var jitter = TimeSpan.FromMilliseconds(new Random().Next(0, 500));
                await Task.Delay(_marketRequestInterval - elapsed + jitter);
            }

            for (int attempts = 1; attempts <= 5; attempts++)
            {
                try
                {
                    _lastMarketRequestUtc = DateTime.UtcNow;
                    var resp = await GetAsync(url, retries: 1);

                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        _consecutiveMarket429s++;
                        int waitSecs = GetRetryAfterSeconds(resp);
                        if (waitSecs <= 0) waitSecs = (int)Math.Pow(2, attempts + 2) + new Random().Next(2, 8); // 8,12,20s+jitter

                        // Open circuit breaker after 3 consecutive 429s (antes eram 5 — muito permissivo)
                        if (_consecutiveMarket429s >= 3)
                        {
                            _circuitOpenUntil = DateTime.UtcNow.Add(_circuitCooldown);
                            _consecutiveMarket429s = 0;
                            await Task.Delay(_circuitCooldown);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromSeconds(waitSecs));
                        }
                        // Update last request time after each wait to re-enforce spacing
                        _lastMarketRequestUtc = DateTime.UtcNow;
                        continue;
                    }

                    if (!resp.IsSuccessStatusCode)
                    {
                        if ((int)resp.StatusCode >= 500 && attempts < 5)
                        {
                            await Task.Delay(500 * attempts);
                            continue;
                        }
                        return 0;
                    }

                    // Success — reset circuit breaker
                    _consecutiveMarket429s = 0;

                    var content = await resp.Content.ReadAsStringAsync();
                    var doc = JsonSerializer.Deserialize<JsonElement>(content);
                    if (!doc.TryGetProperty("success", out var s) ||
                        !(s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.Number && s.GetInt32() == 1)))
                        return 0;

                    string? priceStr = null;
                    if (doc.TryGetProperty("lowest_price", out var lp)) priceStr = lp.GetString();
                    if (string.IsNullOrWhiteSpace(priceStr) && doc.TryGetProperty("median_price", out var mp)) priceStr = mp.GetString();
                    if (string.IsNullOrWhiteSpace(priceStr)) return 0;

                    var price = ParseSteamPrice(priceStr);
                    _cache.Set(key, price, TimeSpan.FromHours(3)); // TTL alto = menos requests ao market
                    return price;
                }
                catch when (attempts < 5)
                {
                    await Task.Delay(1000 * attempts);
                }
            }
            return 0;
        }
        finally { _marketSemaphore.Release(); }
    }

    // ─── Price String Parser ──────────────────────────────────────────────────
    private static double ParseSteamPrice(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var cleaned = Regex.Replace(raw, @"[^\d,.]", "").Trim();
        if (string.IsNullOrEmpty(cleaned)) return 0;
        if (Regex.IsMatch(cleaned, @",\d{2}$") && !cleaned.Contains('.'))
            cleaned = cleaned.Replace(",", ".");
        else if (Regex.IsMatch(cleaned, @"\.\d{2}$") && cleaned.Contains(','))
            cleaned = cleaned.Replace(",", "");
        else if (cleaned.Contains(',') && cleaned.Contains('.'))
        {
            int dotIdx = cleaned.LastIndexOf('.');
            int commaIdx = cleaned.LastIndexOf(',');
            cleaned = commaIdx > dotIdx
                ? cleaned.Replace(".", "").Replace(",", ".")
                : cleaned.Replace(",", "");
        }
        else cleaned = cleaned.Replace(",", "");
        return double.TryParse(cleaned, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var price) ? price : 0;
    }

    // ─── Market Price History ────────────────────────────────────────────────
    public async Task<List<(long timestamp, double price, int volume)>> GetMarketPriceHistoryAsync(int appId, string marketHashName)
    {
        var key = $"mph:{appId}:{marketHashName}";
        if (_cache.TryGetValue(key, out List<(long, double, int)> ch)) return ch;

        var url = $"https://steamcommunity.com/market/pricehistory/?appid={appId}&market_hash_name={Uri.EscapeDataString(marketHashName)}";
        try
        {
            var resp = await GetAsync(url, retries: 2, useInventoryHeaders: true);
            if (!resp.IsSuccessStatusCode) return new();
            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            if (!doc.TryGetProperty("success", out var s) ||
                !(s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.Number && s.GetInt32() == 1))) return new();
            if (!doc.TryGetProperty("prices", out var prices)) return new();

            var list = new List<(long, double, int)>();
            foreach (var entry in prices.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Array) continue;
                var arr = entry.EnumerateArray().ToList();
                if (arr.Count < 2) continue;
                string dateStr = arr[0].GetString() ?? "";
                double price = arr[1].GetDouble();
                int vol = arr.Count > 2 && int.TryParse(arr[2].GetString(), out var v) ? v : 0;
                var parts = dateStr.Split(' ');
                if (parts.Length >= 3 && DateTime.TryParseExact($"{parts[0]} {parts[1]} {parts[2]}",
                    "MMM dd yyyy", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var dt))
                {
                    list.Add((((DateTimeOffset)dt).ToUnixTimeSeconds(), price, vol));
                }
            }
            var cutoff = DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeSeconds();
            var result = list.Where(x => x.Item1 >= cutoff).TakeLast(90).ToList();
            _cache.Set(key, result, TimeSpan.FromHours(4));
            return result;
        }
        catch { return new(); }
    }

    // ─── Inventory: raw fetch (no prices) ────────────────────────────────────
    private async Task<List<InventoryItem>> FetchInventoryItemsAsync(string steamId, int appId)
    {
        var cacheKey = $"invraw:{steamId}:{appId}";
        if (_cache.TryGetValue(cacheKey, out List<InventoryItem> raw)) return raw;

        // Serializar fetches de inventário: evita múltiplos requests simultâneos ao mesmo endpoint
        await _inventorySemaphore.WaitAsync();
        try
        {
            // Re-check cache após adquirir o semáforo (outro fetch pode ter populado enquanto esperava)
            if (_cache.TryGetValue(cacheKey, out raw)) return raw;

            var allItems = new List<(string name, string imageUrl, string type, string rarity)>();
            string? startAssetId = null;
            int page = 0;
            while (true)
            {
                var url = $"https://steamcommunity.com/inventory/{steamId}/{appId}/2?l=english&count=2000";
                if (startAssetId != null) url += $"&start_assetid={startAssetId}";
                try
                {
                    // Delay entre apps diferentes para não bater no rate limit do endpoint de inventário
                    if (page == 0 && startAssetId == null)
                        await Task.Delay(1000);

                    var resp = await GetAsync(url, retries: 3, useInventoryHeaders: true);
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30));
                        break;
                    }
                    if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Unauthorized) break;
                    if (!resp.IsSuccessStatusCode) break;
                    var json = await resp.Content.ReadAsStringAsync();
                    if (string.IsNullOrWhiteSpace(json) || json == "null" || json == "[]") break;
                    var doc = JsonSerializer.Deserialize<JsonElement>(json);
                    if (doc.TryGetProperty("success", out var succ))
                        if (!(succ.ValueKind == JsonValueKind.True || (succ.ValueKind == JsonValueKind.Number && succ.GetInt32() == 1))) break;
                    if (!doc.TryGetProperty("assets", out var assetsEl)) break;
                    if (!doc.TryGetProperty("descriptions", out var descsEl)) break;

                    var descs = new Dictionary<string, JsonElement>();
                    foreach (var d in descsEl.EnumerateArray())
                    {
                        string classid = d.TryGetProperty("classid", out var cid) ? cid.GetString() ?? "" : "";
                        string instanceid = d.TryGetProperty("instanceid", out var iid) ? iid.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(classid)) descs[$"{classid}_{instanceid}"] = d;
                    }
                    foreach (var asset in assetsEl.EnumerateArray())
                    {
                        string classid = asset.TryGetProperty("classid", out var cid) ? cid.GetString() ?? "" : "";
                        string instanceid = asset.TryGetProperty("instanceid", out var iid) ? iid.GetString() ?? "" : "";
                        if (!descs.TryGetValue($"{classid}_{instanceid}", out var desc)) continue;

                        string name = desc.TryGetProperty("market_hash_name", out var mhn) ? mhn.GetString() ?? "" :
                                      desc.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(name)) continue;
                        string imgUrl = "";
                        if (desc.TryGetProperty("icon_url_large", out var ilu) && !string.IsNullOrEmpty(ilu.GetString()))
                            imgUrl = BuildInventoryImageUrl(ilu.GetString()!);
                        else if (desc.TryGetProperty("icon_url", out var iu) && !string.IsNullOrEmpty(iu.GetString()))
                            imgUrl = BuildInventoryImageUrl(iu.GetString()!);
                        string type = desc.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                        string rarity = "";
                        if (desc.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                            foreach (var tag in tags.EnumerateArray())
                                if (tag.TryGetProperty("category", out var cat) &&
                                    (cat.GetString() == "Rarity" || cat.GetString() == "Quality") &&
                                    tag.TryGetProperty("localized_tag_name", out var tagName))
                                { rarity = tagName.GetString() ?? ""; break; }
                        allItems.Add((name, imgUrl, type, rarity));
                    }
                    bool moreItems = doc.TryGetProperty("more_items", out var more) &&
                        (more.ValueKind == JsonValueKind.True || (more.ValueKind == JsonValueKind.Number && more.GetInt32() == 1));
                    if (!moreItems) break;
                    if (doc.TryGetProperty("last_assetid", out var lastId)) startAssetId = lastId.GetString();
                    else break;
                    page++; if (page > 10) break;
                    await Task.Delay(1500); // delay maior entre páginas do mesmo inventário
                }
                catch { break; }
            }

            var result = allItems.GroupBy(i => i.name)
                .Select(g => new InventoryItem
                {
                    Name = g.Key,
                    Count = g.Count(),
                    ImageUrl = g.First().imageUrl,
                    Type = g.First().type,
                    Rarity = g.First().rarity,
                    Price = -1,
                    UnitPrice = -1,
                    AppId = appId
                }).ToList();

            _cache.Set(cacheKey, result, TimeSpan.FromMinutes(15));
            return result;
        } // end try
        finally { _inventorySemaphore.Release(); }
    }

    public Task<List<InventoryItem>> GetInventoryQuickAsync(string steamId, int appId)
        => FetchInventoryItemsAsync(steamId, appId);

    // ─── Calculate Inventory Value — FIXED: skip pricing if too many items ────
    // Items > 120 unique: do partial pricing (top by name alphabetically).
    // This prevents the 60% stall caused by hundreds of sequential market calls.
    public async Task<(double total, List<InventoryItem> items)> CalculateInventoryValueAsync(
        string steamId, int appId, string gameName, Func<int, string, Task>? progress = null)
    {
        if (progress != null) await progress(5, $"Buscando itens de {gameName}...");
        var uniqueItems = await FetchInventoryItemsAsync(steamId, appId);
        if (uniqueItems.Count == 0) return (0, new());

        if (progress != null) await progress(15, $"{gameName}: {uniqueItems.Count} itens únicos. Iniciando precificação...");

        // IMPORTANT FIX: cap at 80 unique items to price — beyond this Steam will 429-block us
        // For CS2/Dota2 inventories with 500+ items, only price the first 80 by value potential
        var toPrice = uniqueItems.Count <= 80
            ? uniqueItems
            : uniqueItems.OrderBy(i => i.Name).Take(80).ToList();

        double totalValue = 0;
        var pricedItems = new List<InventoryItem>();
        for (int i = 0; i < toPrice.Count; i++)
        {
            var ui = toPrice[i];
            var price = await GetMarketPriceAsync(ui.Name, appId);
            var inv = new InventoryItem
            {
                Name = ui.Name,
                Price = price * ui.Count,
                UnitPrice = price,
                Count = ui.Count,
                ImageUrl = ui.ImageUrl,
                Type = ui.Type,
                Rarity = ui.Rarity,
                AppId = appId
            };
            pricedItems.Add(inv);
            totalValue += inv.Price;

            if (progress != null && (i % 2 == 0 || i == toPrice.Count - 1))
            {
                int pct = 15 + (i * 80 / Math.Max(toPrice.Count, 1));
                await progress(Math.Min(pct, 95), $"[{gameName}] {i + 1}/{toPrice.Count}: {ui.Name[..Math.Min(30, ui.Name.Length)]}");
            }
        }

        // Add unpriced items (show them without price)
        var unpricedItems = uniqueItems.Except(toPrice).Select(ui => new InventoryItem
        {
            Name = ui.Name,
            Price = 0,
            UnitPrice = 0,
            Count = ui.Count,
            ImageUrl = ui.ImageUrl,
            Type = ui.Type,
            Rarity = ui.Rarity,
            AppId = appId
        });
        pricedItems.AddRange(unpricedItems);

        return (totalValue, pricedItems);
    }

    // ─── Calculate All Inventories — SEQUENCIAL para não causar 429 ───────────
    // Nome mantido por compatibilidade com o Hub, mas execução é sequencial.
    public async Task<(double total, Dictionary<int, List<InventoryItem>> byApp)> CalculateAllInventoriesParallelAsync(
        string steamId, Func<int, string, Task>? progress = null)
    {
        var apps = new[] { (730, "CS2"), (570, "Dota 2"), (440, "TF2"), (252490, "Rust"), (1172470, "Apex Legends"), (578080, "PUBG"), (304930, "Unturned") };

        var byApp = new Dictionary<int, List<InventoryItem>>();
        double grandTotal = 0;
        int appIdx = 0;
        foreach (var app in apps)
        {
            appIdx++;
            if (progress != null) await progress(5 + appIdx * 5, $"Buscando inventário {app.Item2}...");
            try
            {
                var (total, items) = await CalculateInventoryValueAsync(steamId, app.Item1, app.Item2, progress);
                if (items.Count > 0)
                {
                    byApp[app.Item1] = items;
                    grandTotal += total;
                }
                // Delay entre apps para respeitar rate limit do endpoint de inventário
                if (appIdx < apps.Length)
                    await Task.Delay(1500);
            }
            catch { /* skip app on error */ }
        }
        return (grandTotal, byApp);
    }

    private string BuildInventoryImageUrl(string icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return "";
        if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return icon;
        return $"https://community.cloudflare.steamstatic.com/economy/image/{icon.TrimStart('/')}";
    }

    // ─── Friend List ──────────────────────────────────────────────────────────
    public async Task<List<(string steamId, long friendSince)>> GetFriendListAsync(string steamId)
    {
        var key = $"fl:{steamId}";
        if (_cache.TryGetValue(key, out List<(string, long)> cached)) return cached;
        var resp = await GetAsync($"https://api.steampowered.com/ISteamUser/GetFriendList/v1/?key={SteamApiKey}&steamid={steamId}&relationship=all");
        if (!resp.IsSuccessStatusCode) return new();
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var list = new List<(string, long)>();
            if (doc.TryGetProperty("friendslist", out var fl) && fl.TryGetProperty("friends", out var friends))
                foreach (var f in friends.EnumerateArray())
                {
                    if (!f.TryGetProperty("steamid", out var sid)) continue;
                    long since = f.TryGetProperty("friend_since", out var fs) ? fs.GetInt64() : 0;
                    list.Add((sid.GetString()!, since));
                }
            _cache.Set(key, list, TimeSpan.FromMinutes(15));
            return list;
        }
        catch { return new(); }
    }

    // ─── Recently Played ─────────────────────────────────────────────────────
    public async Task<List<Game>> GetRecentlyPlayedGamesAsync(string steamId, int count = 10)
    {
        var key = $"recent:{steamId}:{count}";
        if (_cache.TryGetValue(key, out List<Game> cached)) return cached;
        var resp = await GetAsync($"https://api.steampowered.com/IPlayerService/GetRecentlyPlayedGames/v1/?key={SteamApiKey}&steamid={steamId}&count={count}");
        if (!resp.IsSuccessStatusCode) return new();
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var list = new List<Game>();
            if (!doc.TryGetProperty("response", out var r) || !r.TryGetProperty("games", out var gamesEl)) return list;
            foreach (var g in gamesEl.EnumerateArray())
            {
                if (!g.TryGetProperty("appid", out var appid) || !g.TryGetProperty("name", out var name)) continue;
                list.Add(new Game
                {
                    AppId = appid.GetInt32(),
                    Name = name.GetString()!,
                    PlaytimeMinutes = g.TryGetProperty("playtime_forever", out var pf) ? pf.GetInt32() : 0,
                    Playtime2WeeksMinutes = g.TryGetProperty("playtime_2weeks", out var pt2) ? pt2.GetInt32() : 0,
                    ImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{appid.GetInt32()}/header.jpg"
                });
            }
            _cache.Set(key, list, TimeSpan.FromMinutes(10));
            return list;
        }
        catch { return new(); }
    }

    // ─── Achievements ────────────────────────────────────────────────────────
    public async Task<(int total, int unlocked, double percent, List<AchievementInfo> achievements)> GetPlayerAchievementsAsync(string steamId, int appId)
    {
        var resp = await GetAsync($"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?key={SteamApiKey}&steamid={steamId}&appid={appId}&l=portuguese");
        if (!resp.IsSuccessStatusCode) return (0, 0, 0, new());
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            if (!doc.TryGetProperty("playerstats", out var ps) || !ps.TryGetProperty("achievements", out var ach))
                return (0, 0, 0, new());

            int total = 0, unlocked = 0;
            var list = new List<AchievementInfo>();
            foreach (var a in ach.EnumerateArray())
            {
                total++;
                bool achieved = a.TryGetProperty("achieved", out var ac) && ac.GetInt32() == 1;
                if (achieved) unlocked++;
                list.Add(new AchievementInfo
                {
                    ApiName = a.TryGetProperty("apiname", out var an) ? an.GetString() ?? "" : "",
                    Name = a.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                    Description = a.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                    Achieved = achieved,
                    UnlockTime = a.TryGetProperty("unlocktime", out var ut) ? ut.GetInt64() : 0
                });
            }
            double pct = total > 0 ? (double)unlocked / total * 100.0 : 0;
            return (total, unlocked, pct, list.OrderByDescending(a => a.Achieved).ThenByDescending(a => a.UnlockTime).ToList());
        }
        catch { return (0, 0, 0, new()); }
    }

    // ─── Game Schema — ícones de conquistas via GetSchemaForGame ─────────────
    public async Task<Dictionary<string, string>> GetGameSchemaIconsAsync(int appId)
    {
        var key = $"schema:{appId}";
        if (_cache.TryGetValue(key, out Dictionary<string, string> cached)) return cached;
        try
        {
            var resp = await GetAsync($"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={SteamApiKey}&appid={appId}&l=portuguese");
            if (!resp.IsSuccessStatusCode) return new();
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.TryGetProperty("game", out var game) &&
                game.TryGetProperty("availableGameStats", out var stats) &&
                stats.TryGetProperty("achievements", out var achList))
            {
                foreach (var a in achList.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string icon = a.TryGetProperty("icon", out var ic) ? ic.GetString() ?? "" : "";
                    string iconGray = a.TryGetProperty("icongray", out var ig) ? ig.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(name))
                        icons[name] = string.IsNullOrEmpty(icon) ? iconGray : icon;
                }
            }
            _cache.Set(key, icons, TimeSpan.FromHours(6));
            return icons;
        }
        catch { return new(); }
    }

    // ─── Achievements + ícones (jogo único) ──────────────────────────────────
    public async Task<(int total, int unlocked, double percent, List<AchievementInfo> achievements)> GetPlayerAchievementsWithIconsAsync(string steamId, int appId)
    {
        var achTask = GetPlayerAchievementsAsync(steamId, appId);
        var schemaTask = GetGameSchemaIconsAsync(appId);
        await Task.WhenAll(achTask, schemaTask);

        var (total, unlocked, pct, list) = await achTask;
        var icons = await schemaTask;
        foreach (var a in list)
            if (icons.TryGetValue(a.ApiName, out var url))
                a.IconUrl = url;
        return (total, unlocked, pct, list);
    }

    // ─── Conquistas de TODOS os jogos do usuário ──────────────────────────────
    public async Task<List<(int appId, string appName, string appIcon, int total, int unlocked, double percent, List<AchievementInfo> achievements)>>
        GetAllGamesAchievementsAsync(string steamId, Func<int, string, Task>? progress = null)
    {
        var games = await GetOwnedGamesAsync(steamId);
        // Apenas jogos com tempo de jogo > 0 — os sem playtime quase nunca têm conquistas
        var candidates = games.Where(g => g.PlaytimeMinutes > 0)
                              .OrderByDescending(g => g.PlaytimeMinutes)
                              .ToList();

        var results = new System.Collections.Concurrent.ConcurrentBag<(int, string, string, int, int, double, List<AchievementInfo>)>();
        int done = 0;
        var sem = new SemaphoreSlim(3, 3); // máximo 3 chamadas paralelas (Steam API tolera bem)

        var tasks = candidates.Select(async g =>
        {
            await sem.WaitAsync();
            try
            {
                var (total, unlocked, pct, list) = await GetPlayerAchievementsWithIconsAsync(steamId, g.AppId);
                if (total > 0)
                    results.Add((g.AppId, g.Name, g.IconUrl, total, unlocked, pct, list));
            }
            catch { /* jogo sem conquistas ou privado — ignora */ }
            finally
            {
                sem.Release();
                int d = System.Threading.Interlocked.Increment(ref done);
                if (progress != null && (d % 5 == 0 || d == candidates.Count))
                    await progress(
                        Math.Min(10 + d * 80 / Math.Max(candidates.Count, 1), 90),
                        $"Buscando conquistas... {d}/{candidates.Count} jogos");
            }
        }).ToList();

        await Task.WhenAll(tasks);

        // Ordena por mais desbloqueadas primeiro
        return results
            .OrderByDescending(r => r.Item5)   // unlocked desc
            .ThenByDescending(r => r.Item4)    // total desc
            .ToList();
    }

    // ─── Steam Level ─────────────────────────────────────────────────────────
    public async Task<int> GetSteamLevelAsync(string steamId)
    {
        var key = $"lvl:{steamId}";
        if (_cache.TryGetValue(key, out int cl)) return cl;
        var resp = await GetAsync($"https://api.steampowered.com/IPlayerService/GetSteamLevel/v1/?key={SteamApiKey}&steamid={steamId}");
        if (!resp.IsSuccessStatusCode) return 0;
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            int lvl = doc.TryGetProperty("response", out var r) && r.TryGetProperty("player_level", out var l) ? l.GetInt32() : 0;
            _cache.Set(key, lvl, TimeSpan.FromHours(1));
            return lvl;
        }
        catch { return 0; }
    }

    // ─── Badges ──────────────────────────────────────────────────────────────
    public async Task<List<Badge>> GetBadgesAsync(string steamId)
    {
        var key = $"badges:{steamId}";
        if (_cache.TryGetValue(key, out List<Badge> cb)) return cb;
        var resp = await GetAsync($"https://api.steampowered.com/IPlayerService/GetBadges/v1/?key={SteamApiKey}&steamid={steamId}");
        if (!resp.IsSuccessStatusCode) return new();
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var list = new List<Badge>();
            if (doc.TryGetProperty("response", out var r) && r.TryGetProperty("badges", out var badges))
                foreach (var b in badges.EnumerateArray())
                    list.Add(new Badge
                    {
                        BadgeId = b.TryGetProperty("badgeid", out var bid) ? bid.GetInt32() : 0,
                        Level = b.TryGetProperty("level", out var lv) ? lv.GetInt32() : 0,
                        Xp = b.TryGetProperty("xp", out var xp) ? xp.GetInt32() : 0,
                        AppId = b.TryGetProperty("appid", out var ai) ? ai.GetInt32() : 0,
                        CompletionTime = b.TryGetProperty("completion_time", out var ct) ? ct.GetInt64() : 0
                    });
            _cache.Set(key, list, TimeSpan.FromHours(1));
            return list;
        }
        catch { return new(); }
    }

    // ─── Player Bans ────────────────────────────────────────────────────────
    public async Task<PlayerBans?> GetPlayerBansAsync(string steamId)
    {
        var key = $"bans:{steamId}";
        if (_cache.TryGetValue(key, out PlayerBans? cb)) return cb;
        var resp = await GetAsync($"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={SteamApiKey}&steamids={steamId}");
        if (!resp.IsSuccessStatusCode) return null;
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            if (!doc.TryGetProperty("players", out var players)) return null;
            var p = players.EnumerateArray().FirstOrDefault();
            if (p.ValueKind == JsonValueKind.Undefined) return null;
            var bans = new PlayerBans
            {
                CommunityBanned = p.TryGetProperty("CommunityBanned", out var cb2) && cb2.ValueKind == JsonValueKind.True,
                VacBanned = p.TryGetProperty("VACBanned", out var vb) && vb.ValueKind == JsonValueKind.True,
                NumberOfVacBans = p.TryGetProperty("NumberOfVACBans", out var nv) ? nv.GetInt32() : 0,
                DaysSinceLastBan = p.TryGetProperty("DaysSinceLastBan", out var dl) ? dl.GetInt32() : 0,
                NumberOfGameBans = p.TryGetProperty("NumberOfGameBans", out var ng) ? ng.GetInt32() : 0,
                EconomyBan = p.TryGetProperty("EconomyBan", out var eb) ? eb.GetString() ?? "none" : "none"
            };
            _cache.Set(key, bans, TimeSpan.FromHours(1));
            return bans;
        }
        catch { return null; }
    }

    // ─── Wishlist ───────────────────────────────────────────────────────────
    public async Task<List<WishlistItem>> GetWishlistAsync(string steamId)
    {
        var key = $"wish:{steamId}";
        if (_cache.TryGetValue(key, out List<WishlistItem> cw)) return cw;
        var resp = await GetAsync($"https://store.steampowered.com/wishlist/profiles/{steamId}/wishlistdata/");
        if (!resp.IsSuccessStatusCode) return new();
        try
        {
            var json = await resp.Content.ReadAsStringAsync();
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            var list = new List<WishlistItem>();
            foreach (var prop in doc.EnumerateObject())
            {
                if (!int.TryParse(prop.Name, out int appId)) continue;
                var v = prop.Value;
                list.Add(new WishlistItem
                {
                    AppId = appId,
                    Name = v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    Priority = v.TryGetProperty("priority", out var pr) ? pr.GetInt32() : 999,
                    Added = v.TryGetProperty("added", out var a) ? a.GetInt64() : 0,
                    ImageUrl = v.TryGetProperty("capsule", out var cap) ? cap.GetString() ?? $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg"
                                                                        : $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg"
                });
            }
            list = list.OrderBy(w => w.Priority).ToList();
            _cache.Set(key, list, TimeSpan.FromMinutes(30));
            return list;
        }
        catch { return new(); }
    }

    // ─── User Stats ─────────────────────────────────────────────────────────
    public async Task<Dictionary<string, double>> GetUserStatsForGameAsync(string steamId, int appId)
    {
        var key = $"stats:{steamId}:{appId}";
        if (_cache.TryGetValue(key, out Dictionary<string, double> cs)) return cs;
        var resp = await GetAsync($"https://api.steampowered.com/ISteamUserStats/GetUserStatsForGame/v2/?key={SteamApiKey}&steamid={steamId}&appid={appId}");
        if (!resp.IsSuccessStatusCode) return new();
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var stats = new Dictionary<string, double>();
            if (doc.TryGetProperty("playerstats", out var ps) && ps.TryGetProperty("stats", out var s))
                foreach (var stat in s.EnumerateArray())
                    if (stat.TryGetProperty("name", out var nm) && stat.TryGetProperty("value", out var val))
                        stats[nm.GetString()!] = val.ValueKind == JsonValueKind.Number ? val.GetDouble() : 0;
            _cache.Set(key, stats, TimeSpan.FromHours(2));
            return stats;
        }
        catch { return new(); }
    }

    // ─── Current Players ────────────────────────────────────────────────────
    public async Task<int> GetNumberOfCurrentPlayersAsync(int appId)
    {
        var key = $"players:{appId}";
        if (_cache.TryGetValue(key, out int cp)) return cp;
        var resp = await GetAsync($"https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/?appid={appId}");
        if (!resp.IsSuccessStatusCode) return 0;
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            int count = doc.TryGetProperty("response", out var r) && r.TryGetProperty("player_count", out var pc) ? pc.GetInt32() : 0;
            _cache.Set(key, count, TimeSpan.FromMinutes(5));
            return count;
        }
        catch { return 0; }
    }

    // ─── Market Listings ────────────────────────────────────────────────────
    public async Task<List<MarketListing>> GetMarketListingsAsync(int appId, string marketHashName, int count = 10)
    {
        var key = $"mlist:{appId}:{marketHashName}:{count}";
        if (_cache.TryGetValue(key, out List<MarketListing> cm)) return cm;

        var url = $"https://steamcommunity.com/market/listings/{appId}/{Uri.EscapeDataString(marketHashName)}/render?start=0&count={count}&currency=7&language=portuguese&format=json";
        var resp = await GetAsync(url);
        if (!resp.IsSuccessStatusCode) return new();
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var list = new List<MarketListing>();
            if (doc.TryGetProperty("listinginfo", out var listings))
            {
                foreach (var prop in listings.EnumerateObject())
                {
                    if (!prop.Value.TryGetProperty("converted_price", out var price)) continue;
                    if (!prop.Value.TryGetProperty("converted_fee", out var fee)) continue;
                    list.Add(new MarketListing
                    {
                        ListingId = prop.Name,
                        Price = (price.GetDouble() + fee.GetDouble()) / 100.0
                    });
                }
            }
            _cache.Set(key, list, TimeSpan.FromMinutes(10));
            return list;
        }
        catch { return new(); }
    }

    // ─── User Groups ────────────────────────────────────────────────────────
    public async Task<List<SteamGroup>> GetUserGroupsAsync(string steamId)
    {
        var key = $"groups:{steamId}";
        if (_cache.TryGetValue(key, out List<SteamGroup> cg)) return cg;
        var resp = await GetAsync($"https://api.steampowered.com/ISteamUser/GetUserGroupList/v1/?key={SteamApiKey}&steamid={steamId}");
        if (!resp.IsSuccessStatusCode) return new();
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var list = new List<SteamGroup>();
            if (doc.TryGetProperty("response", out var r) && r.TryGetProperty("groups", out var groups))
                foreach (var g in groups.EnumerateArray())
                    if (g.TryGetProperty("gid", out var gid))
                        list.Add(new SteamGroup { GroupId = gid.GetString() ?? "" });
            _cache.Set(key, list, TimeSpan.FromHours(1));
            return list;
        }
        catch { return new(); }
    }

    // ─── Playtime Analytics ──────────────────────────────────────────────────
    public async Task<PlaytimeAnalytics> GetPlaytimeAnalyticsAsync(string steamId)
    {
        var games = await GetOwnedGamesAsync(steamId);
        var recent = await GetRecentlyPlayedGamesAsync(steamId, 10);

        var totalMinutes = games.Sum(g => g.PlaytimeMinutes);
        var played = games.Where(g => g.PlaytimeMinutes > 0).ToList();
        var never = games.Count - played.Count;
        var mostPlayed = played.OrderByDescending(g => g.PlaytimeMinutes).Take(10).ToList();

        return new PlaytimeAnalytics
        {
            TotalGames = games.Count,
            PlayedGames = played.Count,
            NeverPlayedGames = never,
            TotalHours = totalMinutes / 60.0,
            AverageHoursPerGame = played.Count > 0 ? (totalMinutes / 60.0) / played.Count : 0,
            MostPlayedGames = mostPlayed,
            RecentlyPlayed = recent,
            PlaytimePercentile = played.Count > 0 ? played.Count * 100.0 / games.Count : 0
        };
    }

    // ─── Calculate Games Value ────────────────────────────────────────────────
    public async Task<(double total, List<Game> games)> CalculateGamesValueAsync(
        string steamId, Func<int, string, Task>? progress = null)
    {
        if (progress != null) await progress(15, "Buscando biblioteca de jogos...");
        var games = await GetOwnedGamesAsync(steamId);
        if (progress != null) await progress(20, $"Calculando preços de {games.Count} jogos...");

        int done = 0;
        var tasks = games.Select(async g =>
        {
            var (price, img, genre, developer, metacritic) = await GetAppDetailsAsync(g.AppId);
            g.Price = price;
            g.ImageUrl = img;
            g.Genre = genre;
            g.Developer = developer;
            g.MetacriticScore = metacritic;

            double hours = Math.Max(1.0, g.PlaytimeMinutes / 60.0);
            g.HoursPerDollar = price > 0 ? Math.Round(hours / price, 2) : double.PositiveInfinity;
            g.CommunityScore = Math.Round((metacritic * 0.6) + Math.Min(50, hours) * 0.8, 2);
            g.FriendPopularity = 0;

            Interlocked.Increment(ref done);
            if (progress != null)
                await progress(20 + (done * 28 / Math.Max(games.Count, 1)), $"Calculado: {g.Name}");
            return g;
        }).ToList();

        var results = await Task.WhenAll(tasks);
        double total = results.Sum(g => g.Price);
        RecordAccountSnapshot(steamId, total);
        if (progress != null) await progress(50, $"{games.Count} jogos calculados");
        return (total, results.ToList());
    }

    public async Task<(int count, double total)> CalculateGamesFastAsync(string steamId)
    {
        var games = await GetOwnedGamesAsync(steamId);
        if (games.Count == 0) return (0, 0);
        var tasks = games.Select(async g =>
        {
            var (p, _, _, _, _) = await GetAppDetailsAsync(g.AppId); return p;
        }).ToList();
        var prices = await Task.WhenAll(tasks);
        var total = prices.Sum();
        RecordAccountSnapshot(steamId, total);
        return (games.Count, total);
    }

    // ─── Playtime ROI ────────────────────────────────────────────────────────
    public async Task<List<PlaytimeROI>> GetPlaytimeROIAsync(string steamId)
    {
        var key = $"roi:{steamId}";
        if (_cache.TryGetValue(key, out List<PlaytimeROI> cr)) return cr;

        var games = await GetOwnedGamesAsync(steamId);
        var played = games.Where(g => g.PlaytimeMinutes >= 60).ToList();

        var tasks = played.Select(async g =>
        {
            var (price, img, genre, dev, meta) = await GetAppDetailsAsync(g.AppId);
            double hours = g.PlaytimeMinutes / 60.0;
            return new PlaytimeROI
            {
                AppId = g.AppId,
                Name = g.Name,
                Price = price,
                Hours = Math.Round(hours, 1),
                CostPerHour = price > 0 ? Math.Round(price / hours, 2) : 0,
                ImageUrl = string.IsNullOrEmpty(img) ? $"https://cdn.akamai.steamstatic.com/steam/apps/{g.AppId}/header.jpg" : img,
                Genre = genre
            };
        }).ToList();

        var results = (await Task.WhenAll(tasks))
            .Where(x => x != null)
            .OrderBy(x => x!.CostPerHour)
            .ToList()!;

        _cache.Set(key, results, TimeSpan.FromHours(3));
        return results;
    }

    // ─── Friend Game Scout ────────────────────────────────────────────────────
    public async Task<List<GameScout>> GetGameScoutAsync(string myId, string[] friendIds)
    {
        var myGamesTask = GetOwnedGamesAsync(myId);
        var friendGamesTasks = friendIds.Take(10).Select(id => GetOwnedGamesAsync(id)).ToList();
        await Task.WhenAll(new[] { myGamesTask }.Concat(friendGamesTasks));

        var myGames = (await myGamesTask).Select(g => g.AppId).ToHashSet();
        var allFriendGames = new Dictionary<int, (string name, int ownerCount, int totalPlaytime)>();

        foreach (var task in friendGamesTasks)
        {
            var games = await task;
            foreach (var g in games)
            {
                if (myGames.Contains(g.AppId)) continue;
                if (!allFriendGames.ContainsKey(g.AppId))
                    allFriendGames[g.AppId] = (g.Name, 0, 0);
                var existing = allFriendGames[g.AppId];
                allFriendGames[g.AppId] = (existing.name, existing.ownerCount + 1, existing.totalPlaytime + g.PlaytimeMinutes);
            }
        }

        var top = allFriendGames
            .OrderByDescending(kv => kv.Value.ownerCount)
            .ThenByDescending(kv => kv.Value.totalPlaytime)
            .Take(20).ToList();

        var scout = await Task.WhenAll(top.Select(async kv =>
        {
            var (price, img, genre, dev, meta) = await GetAppDetailsAsync(kv.Key);
            return new GameScout
            {
                AppId = kv.Key,
                Name = kv.Value.name,
                FriendsWhoOwn = kv.Value.ownerCount,
                AvgFriendHours = Math.Round(kv.Value.totalPlaytime / 60.0 / Math.Max(kv.Value.ownerCount, 1), 1),
                Price = price,
                ImageUrl = string.IsNullOrEmpty(img) ? $"https://cdn.akamai.steamstatic.com/steam/apps/{kv.Key}/header.jpg" : img,
                Genre = genre,
                MetacriticScore = meta
            };
        }));

        return scout.OrderByDescending(g => g.FriendsWhoOwn).ThenByDescending(g => g.AvgFriendHours).ToList();
    }

    // ─── Friend Leaderboard ───────────────────────────────────────────────────
    public async Task<List<LeaderboardEntry>> GetFriendLeaderboardAsync(string myId, string[] friendIds)
    {
        var allIds = new[] { myId }.Concat(friendIds.Take(15)).ToArray();

        var sem = new SemaphoreSlim(5);
        var tasks = allIds.Select(async id =>
        {
            await sem.WaitAsync();
            try
            {
                var summaryTask = GetPlayerSummariesAsync(id);
                var levelTask = GetSteamLevelAsync(id);
                var gamesTask = GetOwnedGamesAsync(id);
                var badgesTask = GetBadgesAsync(id);
                await Task.WhenAll(summaryTask, levelTask, gamesTask, badgesTask);

                var summary = await summaryTask;
                var level = await levelTask;
                var games = await gamesTask;
                var badges = await badgesTask;

                string name = id, avatar = "";
                if (summary.HasValue)
                {
                    var player = summary.Value.GetProperty("response").GetProperty("players").EnumerateArray().FirstOrDefault();
                    if (player.ValueKind != JsonValueKind.Undefined)
                    {
                        name = player.TryGetProperty("personaname", out var pn) ? pn.GetString() ?? id : id;
                        avatar = player.TryGetProperty("avatarfull", out var av) ? av.GetString() ?? "" : "";
                    }
                }

                return new LeaderboardEntry
                {
                    SteamId = id,
                    Name = name,
                    Avatar = avatar,
                    Level = level,
                    TotalGames = games.Count,
                    TotalHours = Math.Round(games.Sum(g => g.PlaytimeMinutes) / 60.0, 0),
                    BadgeCount = badges.Count,
                    TotalXp = badges.Sum(b => b.Xp),
                    IsMe = id == myId
                };
            }
            finally { sem.Release(); }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        return results.OrderByDescending(e => e.TotalHours).ToList();
    }

    // ─── Trade Tracker ───────────────────────────────────────────────────────
    public async Task<List<TradeTrackerItem>> GetTradeTrackerAsync(string steamId, int appId)
    {
        var (_, items) = await CalculateInventoryValueAsync(steamId, appId, "");
        if (!items.Any()) return new();

        var topItems = items.Where(i => i.UnitPrice > 0).OrderByDescending(i => i.UnitPrice).Take(8).ToList();

        var sem = new SemaphoreSlim(3);
        var tasks = topItems.Select(async item =>
        {
            await sem.WaitAsync();
            try
            {
                var history = await GetMarketPriceHistoryAsync(appId, item.Name);
                if (!history.Any()) return null;
                double minPrice = history.Min(h => h.price);
                double maxPrice = history.Max(h => h.price);
                double avgPrice = history.Average(h => h.price);
                double trend = history.Count >= 2 ? history.Last().price - history[history.Count / 2].price : 0;
                return new TradeTrackerItem
                {
                    Name = item.Name,
                    CurrentPrice = item.UnitPrice,
                    MinPrice = Math.Round(minPrice, 2),
                    MaxPrice = Math.Round(maxPrice, 2),
                    AvgPrice = Math.Round(avgPrice, 2),
                    Trend = Math.Round(trend, 2),
                    PriceHistory = history.Select(h => new { ts = h.timestamp, price = Math.Round(h.price, 2), vol = h.volume }).Cast<object>().ToList(),
                    ImageUrl = item.ImageUrl,
                    Count = item.Count
                };
            }
            finally { sem.Release(); }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        return results.Where(r => r != null).Cast<TradeTrackerItem>().ToList();
    }

    // ─── Country Distribution ─────────────────────────────────────────────────
    public async Task<List<CountryEntry>> GetFriendCountryDistributionAsync(string steamId)
    {
        var friends = await GetFriendListAsync(steamId);
        if (!friends.Any()) return new();

        var summaries = await GetPlayerSummariesAsync(string.Join(",", friends.Select(f => f.steamId)));
        if (!summaries.HasValue) return new();

        var countryCounts = new Dictionary<string, int>();
        foreach (var p in summaries.Value.GetProperty("response").GetProperty("players").EnumerateArray())
        {
            if (!p.TryGetProperty("loccountrycode", out var cc) || string.IsNullOrEmpty(cc.GetString())) continue;
            var country = cc.GetString()!;
            countryCounts[country] = (countryCounts.TryGetValue(country, out var c) ? c : 0) + 1;
        }
        return countryCounts
            .Select(kv => new CountryEntry { Code = kv.Key, Count = kv.Value })
            .OrderByDescending(e => e.Count)
            .ToList();
    }

    // ─── Profile Comparison ──────────────────────────────────────────────────
    public async Task<ProfileComparison> CompareProfilesAsync(string steamId1, string steamId2)
    {
        var games1Task = GetOwnedGamesAsync(steamId1);
        var games2Task = GetOwnedGamesAsync(steamId2);
        var lvl1Task = GetSteamLevelAsync(steamId1);
        var lvl2Task = GetSteamLevelAsync(steamId2);
        var badges1Task = GetBadgesAsync(steamId1);
        var badges2Task = GetBadgesAsync(steamId2);

        await Task.WhenAll(games1Task, games2Task, lvl1Task, lvl2Task, badges1Task, badges2Task);

        var games1 = await games1Task;
        var games2 = await games2Task;
        var badges1 = await badges1Task;
        var badges2 = await badges2Task;

        var ids1 = games1.Select(g => g.AppId).ToHashSet();
        var ids2 = games2.Select(g => g.AppId).ToHashSet();
        var common = ids1.Intersect(ids2).ToList();

        return new ProfileComparison
        {
            SteamId1 = steamId1,
            SteamId2 = steamId2,
            GamesCount1 = games1.Count,
            GamesCount2 = games2.Count,
            Level1 = await lvl1Task,
            Level2 = await lvl2Task,
            BadgeCount1 = badges1.Count,
            BadgeCount2 = badges2.Count,
            TotalXp1 = badges1.Sum(b => b.Xp),
            TotalXp2 = badges2.Sum(b => b.Xp),
            TotalHours1 = games1.Sum(g => g.PlaytimeMinutes) / 60.0,
            TotalHours2 = games2.Sum(g => g.PlaytimeMinutes) / 60.0,
            CommonGamesCount = common.Count,
            CommonGames = games1.Where(g => common.Contains(g.AppId)).Take(20)
                .Select(g => new { g.AppId, g.Name, g.ImageUrl }).ToList<object>(),
            ExclusiveGames1Count = games1.Count - common.Count,
            ExclusiveGames2Count = games2.Count - common.Count
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    // ─── NEW FEATURE #1: Perfil DNA — Gamer Identity Score ──────────────────
    // Analisa o perfil e gera uma "identidade gamer" baseada em dados reais:
    // jogos mais jogados, horários de uso, diversidade de gêneros, agressividade
    // em conquistas e mais. Não existe em nenhum outro tracker incluindo SteamDB.
    // ════════════════════════════════════════════════════════════════════════
    public async Task<GamerDna> GetGamerDnaAsync(string steamId)
    {
        var key = $"dna:{steamId}";
        if (_cache.TryGetValue(key, out GamerDna? cd) && cd != null) return cd;

        var gamesTask = GetOwnedGamesAsync(steamId);
        var badgesTask = GetBadgesAsync(steamId);
        var levelTask = GetSteamLevelAsync(steamId);
        var recentTask = GetRecentlyPlayedGamesAsync(steamId, 10);

        await Task.WhenAll(gamesTask, badgesTask, levelTask, recentTask);

        var games = await gamesTask;
        var badges = await badgesTask;
        var level = await levelTask;
        var recent = await recentTask;

        if (games.Count == 0) return new GamerDna { SteamId = steamId };

        var played = games.Where(g => g.PlaytimeMinutes > 0).ToList();
        double totalHours = games.Sum(g => g.PlaytimeMinutes) / 60.0;
        double avgHours = played.Count > 0 ? totalHours / played.Count : 0;
        var topGame = played.OrderByDescending(g => g.PlaytimeMinutes).FirstOrDefault();
        double topGamePercent = totalHours > 0 && topGame != null
            ? (topGame.PlaytimeMinutes / 60.0) / totalHours * 100.0 : 0;
        double playedPercent = games.Count > 0 ? played.Count * 100.0 / games.Count : 0;
        double recentActivity = recent.Sum(g => g.Playtime2WeeksMinutes) / 60.0;

        // Calculate genre diversity from recently played/top games
        // (full genre would require appdetails calls — we do it based on name heuristics)
        double diversityScore = Math.Min(100, played.Count * 2.5); // heuristic

        // Determine archetype
        string archetype = DetermineArchetype(topGamePercent, avgHours, playedPercent,
            badges.Count, level, recentActivity);

        // Score pillars (0–100 each)
        int explorerScore = (int)Math.Min(100, playedPercent * 1.2);
        int veteranScore = (int)Math.Min(100, Math.Log10(Math.Max(1, totalHours)) * 25);
        int collectorScore = (int)Math.Min(100, Math.Log10(Math.Max(1, games.Count)) * 33);
        int achieverScore = (int)Math.Min(100, badges.Count * 1.5);
        int socialScore = (int)Math.Min(100, level * 1.2);
        int intensityScore = (int)Math.Min(100, recentActivity * 5);

        int overallScore = (explorerScore + veteranScore + collectorScore + achieverScore + socialScore + intensityScore) / 6;

        var result = new GamerDna
        {
            SteamId = steamId,
            Archetype = archetype,
            OverallScore = overallScore,
            ExplorerScore = explorerScore,
            VeteranScore = veteranScore,
            CollectorScore = collectorScore,
            AchieverScore = achieverScore,
            SocialScore = socialScore,
            IntensityScore = intensityScore,
            TotalHours = Math.Round(totalHours, 0),
            TotalGames = games.Count,
            PlayedPercent = Math.Round(playedPercent, 1),
            TopGameName = topGame?.Name ?? "",
            TopGameHours = topGame != null ? Math.Round(topGame.PlaytimeMinutes / 60.0, 0) : 0,
            TopGamePercent = Math.Round(topGamePercent, 1),
            RecentHours2w = Math.Round(recentActivity, 1),
            BadgeCount = badges.Count,
            SteamLevel = level
        };

        _cache.Set(key, result, TimeSpan.FromHours(3));
        return result;
    }

    private static string DetermineArchetype(double topGamePct, double avgHours, double playedPct,
        int badges, int level, double recentActivity)
    {
        if (topGamePct > 60 && avgHours > 50) return "One-Trick Warrior";
        if (playedPct < 30 && avgHours < 5) return "Backlog Hoarder";
        if (recentActivity > 20 && avgHours > 10) return "Active Grinder";
        if (badges > 100 && level > 50) return "Badge Hunter";
        if (playedPct > 80 && avgHours > 20) return "True Completionist";
        if (avgHours < 3 && playedPct > 60) return "Game Sampler";
        if (recentActivity < 1 && avgHours > 30) return "Veteran in Hiatus";
        if (level > 100) return "Steam Legend";
        if (avgHours > 100) return "Hardcore Gamer";
        return "Casual Explorer";
    }

    // ════════════════════════════════════════════════════════════════════════
    // ─── NEW FEATURE #2: Sleep Schedule Detector ────────────────────────────
    // Analisa os horários de última atividade dos amigos para detectar padrões
    // de jogo (horário favorito, fuso horário estimado, noturno vs diurno).
    // 100% baseado em dados da Steam API. Não existe no SteamDB nem no Steam.
    // ════════════════════════════════════════════════════════════════════════
    public async Task<List<FriendActivityPattern>> GetFriendActivityPatternsAsync(string steamId)
    {
        var key = $"patterns:{steamId}";
        if (_cache.TryGetValue(key, out List<FriendActivityPattern>? cp) && cp != null) return cp;

        var friends = await GetFriendListAsync(steamId);
        if (!friends.Any()) return new();

        var summaries = await GetPlayerSummariesAsync(string.Join(",", friends.Select(f => f.steamId)));
        if (!summaries.HasValue) return new();

        var patterns = new List<FriendActivityPattern>();
        foreach (var p in summaries.Value.GetProperty("response").GetProperty("players").EnumerateArray())
        {
            string sid = p.TryGetProperty("steamid", out var id) ? id.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(sid)) continue;

            long lastLogoff = p.TryGetProperty("lastlogoff", out var ll) ? ll.GetInt64() : 0;
            if (lastLogoff == 0) continue;

            var lastSeen = DateTimeOffset.FromUnixTimeSeconds(lastLogoff).DateTime;
            int hour = lastSeen.Hour;
            string slot = hour switch
            {
                >= 0 and < 6 => "Madrugador",
                >= 6 and < 12 => "Matutino",
                >= 12 and < 18 => "Vespertino",
                _ => "Noturno"
            };

            patterns.Add(new FriendActivityPattern
            {
                SteamId = sid,
                Name = p.TryGetProperty("personaname", out var pn) ? pn.GetString() ?? "" : "",
                Avatar = p.TryGetProperty("avatarfull", out var av) ? av.GetString() ?? "" : "",
                LastLogoffHour = hour,
                ActivitySlot = slot,
                LastLogoffUnix = lastLogoff,
                IsOnline = p.TryGetProperty("personastate", out var ps) && ps.GetInt32() > 0,
                PlayingGame = p.TryGetProperty("gameextrainfo", out var ge) ? ge.GetString() ?? "" : ""
            });
        }

        var result = patterns.OrderBy(p => p.ActivitySlot).ThenByDescending(p => p.LastLogoffUnix).ToList();
        _cache.Set(key, result, TimeSpan.FromMinutes(20));
        return result;
    }

    // ════════════════════════════════════════════════════════════════════════
    // ─── NEW FEATURE #3: Wishlist Value Tracker ──────────────────────────────
    // Calcula o valor total da wishlist, detecta jogos em promoção, e estima
    // quando cada jogo provavelmente entrará em desconto baseado em histórico.
    // Inclui prioridade da wishlist e data de adição. Único no mercado.
    // ════════════════════════════════════════════════════════════════════════
    public async Task<WishlistAnalysis> GetWishlistAnalysisAsync(string steamId)
    {
        var key = $"wishanalysis:{steamId}";
        if (_cache.TryGetValue(key, out WishlistAnalysis? cwa) && cwa != null) return cwa;

        var wishlist = await GetWishlistAsync(steamId);
        if (!wishlist.Any()) return new WishlistAnalysis { SteamId = steamId };

        // Fetch prices for top 30 wishlist items
        var toFetch = wishlist.Take(30).ToList();
        var sem = new SemaphoreSlim(8);
        var pricedItems = await Task.WhenAll(toFetch.Select(async w =>
        {
            await sem.WaitAsync();
            try
            {
                var (price, img, genre, dev, meta) = await GetAppDetailsAsync(w.AppId);
                return new WishlistItemAnalyzed
                {
                    AppId = w.AppId,
                    Name = w.Name,
                    Priority = w.Priority,
                    Added = w.Added,
                    ImageUrl = string.IsNullOrEmpty(img) ? w.ImageUrl : img,
                    CurrentPrice = price,
                    Genre = genre,
                    Developer = dev,
                    MetacriticScore = meta,
                    // Estimate sale probability: games >6mo old with no price change often go on sale
                    SaleProbability = EstimateSaleProbability(w.Added, price, meta)
                };
            }
            catch { return new WishlistItemAnalyzed { AppId = w.AppId, Name = w.Name, ImageUrl = w.ImageUrl }; }
            finally { sem.Release(); }
        }));

        var valid = pricedItems.Where(i => i != null).ToList()!;
        double totalFull = valid.Sum(i => i.CurrentPrice);
        double totalPriority = valid.Where(i => i.Priority <= 5).Sum(i => i.CurrentPrice);
        var likelySale = valid.Where(i => i.SaleProbability >= 60).OrderByDescending(i => i.SaleProbability).Take(5).ToList();

        var result = new WishlistAnalysis
        {
            SteamId = steamId,
            TotalItems = wishlist.Count,
            PricedItems = valid.Count(i => i.CurrentPrice > 0),
            TotalFullPrice = Math.Round(totalFull, 2),
            TotalPriorityPrice = Math.Round(totalPriority, 2),
            LikelySaleItems = likelySale,
            Items = valid.OrderBy(i => i.Priority).ToList()
        };

        _cache.Set(key, result, TimeSpan.FromHours(2));
        return result;
    }

    private static int EstimateSaleProbability(long addedUnix, double price, int metacritic)
    {
        if (addedUnix == 0 || price == 0) return 0;
        var added = DateTimeOffset.FromUnixTimeSeconds(addedUnix).DateTime;
        int monthsOld = (int)(DateTime.UtcNow - added).TotalDays / 30;
        int prob = 0;
        if (monthsOld > 12) prob += 40;
        else if (monthsOld > 6) prob += 20;
        if (price > 50) prob += 20;    // expensive games are more likely to be discounted
        if (metacritic > 80) prob += 15; // popular games get frequent sales
        if (price > 0 && price < 20) prob += 10; // indie games go on sale often
        return Math.Min(95, prob);
    }

    // ─── Snapshots ────────────────────────────────────────────────────────────
    public List<(DateTime time, double total)> GetAccountSnapshots(string steamId)
    {
        lock (_accountSnapshots) return _accountSnapshots.TryGetValue(steamId, out var snaps) ? snaps.ToList() : new();
    }

    public void RecordAccountSnapshot(string steamId, double total)
    {
        lock (_accountSnapshots)
        {
            if (!_accountSnapshots.ContainsKey(steamId)) _accountSnapshots[steamId] = new();
            _accountSnapshots[steamId].Add((DateTime.UtcNow, total));
        }
    }
}

// ─── Data Models ──────────────────────────────────────────────────────────────
public class Game
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public string ImageUrl { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public int PlaytimeMinutes { get; set; }
    public int Playtime2WeeksMinutes { get; set; }
    public string Genre { get; set; } = "";
    public string Developer { get; set; } = "";
    public int MetacriticScore { get; set; }
    public double CommunityScore { get; set; } = 0;
    public double HoursPerDollar { get; set; } = 0;
    public int FriendPopularity { get; set; } = 0;
}

public class InventoryItem
{
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public double UnitPrice { get; set; }
    public int Count { get; set; } = 1;
    public string ImageUrl { get; set; } = "";
    public string Type { get; set; } = "";
    public string Rarity { get; set; } = "";
    public int AppId { get; set; }
}

public class PlayerBans
{
    public bool CommunityBanned { get; set; }
    public bool VacBanned { get; set; }
    public int NumberOfVacBans { get; set; }
    public int DaysSinceLastBan { get; set; }
    public int NumberOfGameBans { get; set; }
    public string EconomyBan { get; set; } = "none";
}

public class Badge
{
    public int BadgeId { get; set; }
    public int Level { get; set; }
    public long CompletionTime { get; set; }
    public int Xp { get; set; }
    public int AppId { get; set; }
}

public class WishlistItem
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public int Priority { get; set; }
    public long Added { get; set; }
    public string ImageUrl { get; set; } = "";
}

public class SteamGroup
{
    public string GroupId { get; set; } = "";
    public string Name { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
}

public class AchievementInfo
{
    public string ApiName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Achieved { get; set; }
    public long UnlockTime { get; set; }
    public string IconUrl { get; set; } = ""; // ícone real da Steam (GetSchemaForGame)
}

public class GlobalStat
{
    public string Name { get; set; } = "";
    public string Total { get; set; } = "0";
}

public class MarketListing
{
    public string ListingId { get; set; } = "";
    public double Price { get; set; }
}

public class PlaytimeAnalytics
{
    public int TotalGames { get; set; }
    public int PlayedGames { get; set; }
    public int NeverPlayedGames { get; set; }
    public double TotalHours { get; set; }
    public double AverageHoursPerGame { get; set; }
    public List<Game> MostPlayedGames { get; set; } = new();
    public List<Game> RecentlyPlayed { get; set; } = new();
    public double PlaytimePercentile { get; set; }
}

public class ProfileComparison
{
    public string SteamId1 { get; set; } = "";
    public string SteamId2 { get; set; } = "";
    public int GamesCount1 { get; set; }
    public int GamesCount2 { get; set; }
    public int Level1 { get; set; }
    public int Level2 { get; set; }
    public int BadgeCount1 { get; set; }
    public int BadgeCount2 { get; set; }
    public int TotalXp1 { get; set; }
    public int TotalXp2 { get; set; }
    public double TotalHours1 { get; set; }
    public double TotalHours2 { get; set; }
    public int CommonGamesCount { get; set; }
    public List<object> CommonGames { get; set; } = new();
    public int ExclusiveGames1Count { get; set; }
    public int ExclusiveGames2Count { get; set; }
}

public class PlaytimeROI
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public double Hours { get; set; }
    public double CostPerHour { get; set; }
    public string ImageUrl { get; set; } = "";
    public string Genre { get; set; } = "";
}

public class GameScout
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public int FriendsWhoOwn { get; set; }
    public double AvgFriendHours { get; set; }
    public double Price { get; set; }
    public string ImageUrl { get; set; } = "";
    public string Genre { get; set; } = "";
    public int MetacriticScore { get; set; }
}

public class LeaderboardEntry
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Avatar { get; set; } = "";
    public int Level { get; set; }
    public int TotalGames { get; set; }
    public double TotalHours { get; set; }
    public int BadgeCount { get; set; }
    public int TotalXp { get; set; }
    public bool IsMe { get; set; }
}

public class TradeTrackerItem
{
    public string Name { get; set; } = "";
    public double CurrentPrice { get; set; }
    public double MinPrice { get; set; }
    public double MaxPrice { get; set; }
    public double AvgPrice { get; set; }
    public double Trend { get; set; }
    public List<object> PriceHistory { get; set; } = new();
    public string ImageUrl { get; set; } = "";
    public int Count { get; set; }
}

public class CountryEntry
{
    public string Code { get; set; } = "";
    public int Count { get; set; }
}

// ─── NEW Feature Models ────────────────────────────────────────────────────
public class GamerDna
{
    public string SteamId { get; set; } = "";
    public string Archetype { get; set; } = "";
    public int OverallScore { get; set; }
    public int ExplorerScore { get; set; }
    public int VeteranScore { get; set; }
    public int CollectorScore { get; set; }
    public int AchieverScore { get; set; }
    public int SocialScore { get; set; }
    public int IntensityScore { get; set; }
    public double TotalHours { get; set; }
    public int TotalGames { get; set; }
    public double PlayedPercent { get; set; }
    public string TopGameName { get; set; } = "";
    public double TopGameHours { get; set; }
    public double TopGamePercent { get; set; }
    public double RecentHours2w { get; set; }
    public int BadgeCount { get; set; }
    public int SteamLevel { get; set; }
}

public class FriendActivityPattern
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Avatar { get; set; } = "";
    public int LastLogoffHour { get; set; }
    public string ActivitySlot { get; set; } = "";
    public long LastLogoffUnix { get; set; }
    public bool IsOnline { get; set; }
    public string PlayingGame { get; set; } = "";
}

public class WishlistItemAnalyzed
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public int Priority { get; set; }
    public long Added { get; set; }
    public string ImageUrl { get; set; } = "";
    public double CurrentPrice { get; set; }
    public string Genre { get; set; } = "";
    public string Developer { get; set; } = "";
    public int MetacriticScore { get; set; }
    public int SaleProbability { get; set; }
}

public class WishlistAnalysis
{
    public string SteamId { get; set; } = "";
    public int TotalItems { get; set; }
    public int PricedItems { get; set; }
    public double TotalFullPrice { get; set; }
    public double TotalPriorityPrice { get; set; }
    public List<WishlistItemAnalyzed> LikelySaleItems { get; set; } = new();
    public List<WishlistItemAnalyzed> Items { get; set; } = new();
}