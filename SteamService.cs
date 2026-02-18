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

    public SteamService(HttpClient httpClient, IMemoryCache cache)
    {
        _httpClient = httpClient;
        _cache = cache;
        _httpClient.Timeout = TimeSpan.FromSeconds(45);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
        _httpClient.DefaultRequestHeaders.Add("Referer", "https://steamcommunity.com/");
        _httpClient.DefaultRequestHeaders.Add("Origin", "https://steamcommunity.com");
    }

    // ─── HTTP Helper ───────────────────────────────────────────────
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
                    await Task.Delay(2000 * (i + 1));
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

    // ─── SteamID Resolution ────────────────────────────────────────
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

    // ─── Player Summaries ──────────────────────────────────────────
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

    // ─── Owned Games ──────────────────────────────────────────────
    public async Task<List<Game>> GetOwnedGamesAsync(string steamId)
    {
        var key = $"games:{steamId}";
        if (_cache.TryGetValue(key, out List<Game> cached)) return cached;

        var resp = await GetAsync($"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={SteamApiKey}&steamid={steamId}&include_appinfo=true&include_played_free_games=true");
        resp.EnsureSuccessStatusCode();
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

    // ─── App Details ──────────────────────────────────────────────
    public async Task<(double price, string imageUrl, string genre, string developer, int metacritic)> GetAppDetailsAsync(int appId)
    {
        var key = $"app2:{appId}";
        if (_cache.TryGetValue(key, out (double, string, string, string, int) cached)) return cached;

        var resp = await GetAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&cc=br&l=pt&filters=price_overview,header_image,genres,developers,metacritic");
        if (!resp.IsSuccessStatusCode) return (0, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg", "", "", 0);

        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            if (!doc.TryGetProperty(appId.ToString(), out var app)) return (0, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg", "", "", 0);
            if (!app.TryGetProperty("success", out var s) || !(s.ValueKind == JsonValueKind.True || s.GetInt32() == 1))
                return (0, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg", "", "", 0);
            if (!app.TryGetProperty("data", out var data)) return (0, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg", "", "", 0);

            double price = 0;
            if (data.TryGetProperty("price_overview", out var po) && po.TryGetProperty("final", out var fin))
                price = fin.GetDouble() / 100.0;

            string img = data.TryGetProperty("header_image", out var hi) ? hi.GetString() ?? "" : $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";

            string genre = "";
            if (data.TryGetProperty("genres", out var genres) && genres.ValueKind == JsonValueKind.Array)
            {
                var genreList = genres.EnumerateArray().Select(g => g.TryGetProperty("description", out var d) ? d.GetString() : "").Where(g => !string.IsNullOrEmpty(g)).Take(2);
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
        catch { return (0, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg", "", "", 0); }
    }

    // ─── Market Price ─────────────────────────────────────────────
    public async Task<double> GetMarketPriceAsync(string name, int appId)
    {
        var key = $"mp:{appId}:{name}";
        if (_cache.TryGetValue(key, out double cp)) return cp;

        var url = $"https://steamcommunity.com/market/priceoverview/?appid={appId}&currency=7&market_hash_name={Uri.EscapeDataString(name)}";
        try
        {
            var resp = await GetAsync(url, retries: 2);
            if (!resp.IsSuccessStatusCode) return 0;

            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            if (!doc.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.True) return 0;

            string? priceStr = null;
            if (doc.TryGetProperty("lowest_price", out var lp)) priceStr = lp.GetString();
            if (string.IsNullOrWhiteSpace(priceStr) && doc.TryGetProperty("median_price", out var mp)) priceStr = mp.GetString();
            if (string.IsNullOrWhiteSpace(priceStr)) return 0;

            priceStr = Regex.Replace(priceStr, @"[^\d,.]", "").Replace(".", "").Replace(",", ".");
            if (!double.TryParse(priceStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var price)) return 0;

            _cache.Set(key, price, TimeSpan.FromMinutes(30));
            return price;
        }
        catch { return 0; }
    }

    // ─── FIXED Inventory (v2 paging + proper headers) ─────────────
    public async Task<(double total, List<InventoryItem> items)> CalculateInventoryValueAsync(
        string steamId, int appId, string gameName, Func<int, string, Task>? progress = null)
    {
        var allItems = new List<(string name, string imageUrl, bool marketable, string type, string rarity)>();
        string? startAssetId = null;
        int page = 0;

        // Paginate through entire inventory
        while (true)
        {
            var url = $"https://steamcommunity.com/inventory/{steamId}/{appId}/2?l=english&count=2000";
            if (startAssetId != null) url += $"&start_assetid={startAssetId}";

            try
            {
                var resp = await GetAsync(url, retries: 3, useInventoryHeaders: true);

                if (resp.StatusCode == HttpStatusCode.Forbidden || resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Inventory is private
                    return (0, new());
                }
                if (!resp.IsSuccessStatusCode) break;

                var json = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(json) || json == "null" || json == "[]") break;

                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                // Check success field
                if (doc.TryGetProperty("success", out var succ))
                {
                    bool ok = succ.ValueKind == JsonValueKind.True || (succ.ValueKind == JsonValueKind.Number && succ.GetInt32() == 1);
                    if (!ok) break;
                }

                if (!doc.TryGetProperty("assets", out var assetsEl)) break;
                if (!doc.TryGetProperty("descriptions", out var descsEl)) break;

                // Build description lookup
                var descs = new Dictionary<string, JsonElement>();
                foreach (var d in descsEl.EnumerateArray())
                {
                    string classid = d.TryGetProperty("classid", out var cid) ? cid.GetString() ?? "" : "";
                    string instanceid = d.TryGetProperty("instanceid", out var iid) ? iid.GetString() ?? "" : "";
                    string descKey = $"{classid}_{instanceid}";
                    if (!string.IsNullOrEmpty(classid) && !descs.ContainsKey(descKey))
                        descs[descKey] = d;
                }

                foreach (var asset in assetsEl.EnumerateArray())
                {
                    string classid = asset.TryGetProperty("classid", out var cid) ? cid.GetString() ?? "" : "";
                    string instanceid = asset.TryGetProperty("instanceid", out var iid) ? iid.GetString() ?? "" : "";
                    string descKey = $"{classid}_{instanceid}";
                    if (!descs.TryGetValue(descKey, out var desc)) continue;

                    bool marketable = false;
                    if (desc.TryGetProperty("marketable", out var mkt))
                        marketable = mkt.ValueKind == JsonValueKind.True || (mkt.ValueKind == JsonValueKind.Number && mkt.GetInt32() == 1);
                    if (!marketable) continue;

                    if (!desc.TryGetProperty("market_hash_name", out var mhn)) continue;
                    var mhnStr = mhn.GetString() ?? "";

                    string imgUrl = "";
                    if (desc.TryGetProperty("icon_url_large", out var ilu) && ilu.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(ilu.GetString()))
                        imgUrl = BuildInventoryImageUrl(ilu.GetString()!);
                    else if (desc.TryGetProperty("icon_url", out var iu) && iu.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(iu.GetString()))
                        imgUrl = BuildInventoryImageUrl(iu.GetString()!);

                    string type = desc.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                    string rarity = "";
                    if (desc.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tag in tags.EnumerateArray())
                        {
                            if (tag.TryGetProperty("category", out var cat) &&
                                (cat.GetString() == "Rarity" || cat.GetString() == "Quality") &&
                                tag.TryGetProperty("localized_tag_name", out var tagName))
                            {
                                rarity = tagName.GetString() ?? "";
                                break;
                            }
                        }
                    }

                    allItems.Add((mhnStr, imgUrl, marketable, type, rarity));
                }

                // Check if there's more pages
                bool moreItems = doc.TryGetProperty("more_items", out var more) &&
                    (more.ValueKind == JsonValueKind.True || (more.ValueKind == JsonValueKind.Number && more.GetInt32() == 1));

                if (!moreItems) break;

                if (doc.TryGetProperty("last_assetid", out var lastId))
                    startAssetId = lastId.GetString();
                else break;

                page++;
                if (page > 10) break; // safety cap
                await Task.Delay(800); // respect rate limits between pages
            }
            catch { break; }
        }

        if (!allItems.Any()) return (0, new());

        // Get unique items and price them
        var uniqueItems = allItems
            .GroupBy(i => i.name)
            .Select(g => (name: g.Key, imageUrl: g.First().imageUrl, count: g.Count(), type: g.First().type, rarity: g.First().rarity))
            .ToList();

        var sem = new SemaphoreSlim(8);
        int done = 0;
        var priceTasks = uniqueItems.Select(async item =>
        {
            await sem.WaitAsync();
            try
            {
                var price = await GetMarketPriceAsync(item.name, appId);
                Interlocked.Increment(ref done);
                if (progress != null && done % 5 == 0)
                    await progress(50 + (done * 35 / Math.Max(uniqueItems.Count, 1)), $"Precificando: {item.name[..Math.Min(25, item.name.Length)]}...");
                await Task.Delay(200); // rate limit
                return new InventoryItem
                {
                    Name = item.name,
                    Price = price * item.count,
                    UnitPrice = price,
                    Count = item.count,
                    ImageUrl = item.imageUrl,
                    Type = item.type,
                    Rarity = item.rarity,
                    AppId = appId
                };
            }
            finally { sem.Release(); }
        }).ToList();

        var results = await Task.WhenAll(priceTasks);
        var items = results.ToList();
        double totalValue = items.Sum(i => i.Price);
        return (totalValue, items);
    }

    // ─── Calculate Games Value ─────────────────────────────────────
    public async Task<(double total, List<Game> games)> CalculateGamesValueAsync(
        string steamId, Func<int, string, Task>? progress = null)
    {
        if (progress != null) await progress(15, "Buscando biblioteca de jogos...");
        var games = await GetOwnedGamesAsync(steamId);
        if (progress != null) await progress(20, $"Calculando preços de {games.Count} jogos...");

        var sem = new SemaphoreSlim(15);
        int done = 0;
        var tasks = games.Select(async g =>
        {
            await sem.WaitAsync();
            try
            {
                var (price, img, genre, developer, metacritic) = await GetAppDetailsAsync(g.AppId);
                g.Price = price;
                g.ImageUrl = img;
                g.Genre = genre;
                g.Developer = developer;
                g.MetacriticScore = metacritic;
                Interlocked.Increment(ref done);
                if (progress != null)
                    await progress(20 + (done * 28 / Math.Max(games.Count, 1)), $"Calculado: {g.Name}");
                return g;
            }
            finally { sem.Release(); }
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
        var sem = new SemaphoreSlim(20);
        var tasks = games.Select(async g =>
        {
            await sem.WaitAsync();
            try { var (p, _, _, _, _) = await GetAppDetailsAsync(g.AppId); return p; }
            finally { sem.Release(); }
        }).ToList();
        var prices = await Task.WhenAll(tasks);
        var total = prices.Sum();
        RecordAccountSnapshot(steamId, total);
        return (games.Count, total);
    }

    private string BuildInventoryImageUrl(string icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return "";
        if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return icon;
        return $"https://community.cloudflare.steamstatic.com/economy/image/{icon.TrimStart('/')}";
    }

    // ─── Friend List ──────────────────────────────────────────────
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

    // ─── Recently Played ──────────────────────────────────────────
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

    // ─── Achievements ─────────────────────────────────────────────
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

    // ─── Steam Level ──────────────────────────────────────────────
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

    // ─── Badges ──────────────────────────────────────────────────
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

    // ─── Player Bans ─────────────────────────────────────────────
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

    // ─── Wishlist ────────────────────────────────────────────────
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

    // ─── NEW: User Stats for Game ─────────────────────────────────
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

    // ─── NEW: Global Game Stats ───────────────────────────────────
    public async Task<List<GlobalStat>> GetGlobalStatsForGameAsync(int appId, string[] statNames)
    {
        var key = $"gstat:{appId}:{string.Join(",", statNames)}";
        if (_cache.TryGetValue(key, out List<GlobalStat> cg)) return cg;

        var names = string.Join("&", statNames.Select((n, i) => $"name[{i}]={Uri.EscapeDataString(n)}"));
        var resp = await GetAsync($"https://api.steampowered.com/ISteamUserStats/GetGlobalStatsForGame/v1/?appid={appId}&count={statNames.Length}&{names}");
        if (!resp.IsSuccessStatusCode) return new();
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var list = new List<GlobalStat>();
            if (doc.TryGetProperty("response", out var r) && r.TryGetProperty("globalstats", out var gs))
                foreach (var prop in gs.EnumerateObject())
                    if (prop.Value.TryGetProperty("total", out var tot))
                        list.Add(new GlobalStat { Name = prop.Name, Total = tot.GetString() ?? "0" });
            _cache.Set(key, list, TimeSpan.FromHours(4));
            return list;
        }
        catch { return new(); }
    }

    // ─── NEW: Number of Current Players ──────────────────────────
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

    // ─── NEW: Market Listings (price history style) ───────────────
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

    // ─── NEW: User Groups ─────────────────────────────────────────
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

    // ─── NEW: Playtime Analytics ──────────────────────────────────
    public async Task<PlaytimeAnalytics> GetPlaytimeAnalyticsAsync(string steamId)
    {
        var games = await GetOwnedGamesAsync(steamId);
        var recent = await GetRecentlyPlayedGamesAsync(steamId, 10);

        var totalMinutes = games.Sum(g => g.PlaytimeMinutes);
        var played = games.Where(g => g.PlaytimeMinutes > 0).ToList();
        var never = games.Count - played.Count;

        var topGenres = new Dictionary<string, int>();
        // approximate genre from game names — real genres need appdetails
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

    // ─── NEW: Profile Comparison ──────────────────────────────────
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
            BadgeCount1 = (await badges1Task).Count,
            BadgeCount2 = (await badges2Task).Count,
            TotalXp1 = (await badges1Task).Sum(b => b.Xp),
            TotalXp2 = (await badges2Task).Sum(b => b.Xp),
            TotalHours1 = games1.Sum(g => g.PlaytimeMinutes) / 60.0,
            TotalHours2 = games2.Sum(g => g.PlaytimeMinutes) / 60.0,
            CommonGamesCount = common.Count,
            CommonGames = games1.Where(g => common.Contains(g.AppId)).Take(20).Select(g => new { g.AppId, g.Name, g.ImageUrl }).ToList<object>(),
            ExclusiveGames1Count = games1.Count - common.Count,
            ExclusiveGames2Count = games2.Count - common.Count
        };
    }

    // ─── Snapshots ────────────────────────────────────────────────
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

// ─── Data Models ───────────────────────────────────────────────
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
