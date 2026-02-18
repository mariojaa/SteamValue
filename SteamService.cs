using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;

public class SteamService
{
    private readonly HttpClient _httpClient;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private const string SteamApiKey = "F4CAED645F0A7B3087195DDD23F74BA0";

    // Snapshots de valor ao longo do tempo (steamId => lista)
    private readonly Dictionary<string, List<(DateTime time, double total)>> _accountSnapshots = new();

    public SteamService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/121.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en-US;q=0.8,en;q=0.7");
    }

    // ─────────────────────────────────────────────────────────────
    //  HTTP helper
    // ─────────────────────────────────────────────────────────────
    private async Task<HttpResponseMessage> GetAsync(string url, int retries = 2)
    {
        for (int i = 0; i <= retries; i++)
        {
            try
            {
                var resp = await _httpClient.GetAsync(url);
                return resp;
            }
            catch when (i < retries)
            {
                await Task.Delay(400 * (i + 1));
            }
        }
        throw new HttpRequestException("Failed: " + url);
    }

    // ─────────────────────────────────────────────────────────────
    //  Resolução de SteamID
    // ─────────────────────────────────────────────────────────────
    public async Task<string> ResolveSteamIdAsync(string profileUrl,
        Func<int, string, Task>? progress = null)
    {
        if (progress != null) await progress(0, "Resolvendo SteamID...");

        var matchNum = Regex.Match(profileUrl, @"profiles/(\d+)");
        if (matchNum.Success)
        {
            if (progress != null) await progress(5, "SteamID encontrado na URL");
            return matchNum.Groups[1].Value;
        }

        // É um número puro (steamid64)?
        if (Regex.IsMatch(profileUrl.Trim(), @"^\d{17}$"))
            return profileUrl.Trim();

        var matchVanity = Regex.Match(profileUrl, @"id/([^/?\s]+)");
        if (!matchVanity.Success)
            throw new ArgumentException("URL de perfil inválida. Use: steamcommunity.com/id/nome ou /profiles/ID");

        var vanity = matchVanity.Groups[1].Value;
        var resp = await GetAsync(
            $"https://api.steampowered.com/ISteamUser/ResolveVanityURL/v1/?key={SteamApiKey}&vanityurl={vanity}");
        resp.EnsureSuccessStatusCode();

        var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var r = doc.GetProperty("response");
        if (r.TryGetProperty("success", out var s) &&
            (s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.Number && s.GetInt32() == 1)) &&
            r.TryGetProperty("steamid", out var sid))
        {
            if (progress != null) await progress(5, "SteamID resolvido");
            return sid.GetString()!;
        }

        throw new ArgumentException("Não foi possível resolver o SteamID para: " + vanity);
    }

    // ─────────────────────────────────────────────────────────────
    //  Player Summaries (batch)
    // ─────────────────────────────────────────────────────────────
    public async Task<JsonElement?> GetPlayerSummariesAsync(string steamIds)
    {
        // API aceita até 100 IDs por chamada
        var chunks = steamIds.Split(',')
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Chunk(100)
            .ToList();

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

        // Rebuilda um único JsonElement com todos os players
        if (allPlayers.Count == 0) return null;
        var result = JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(new { response = new { players = allPlayers } }));
        return result;
    }

    // ─────────────────────────────────────────────────────────────
    //  Jogos
    // ─────────────────────────────────────────────────────────────
    public async Task<List<Game>> GetOwnedGamesAsync(string steamId)
    {
        var key = $"games:{steamId}";
        if (_cache.TryGetValue(key, out List<Game> cached)) return cached;

        var resp = await GetAsync(
            $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/?key={SteamApiKey}" +
            $"&steamid={steamId}&include_appinfo=true&include_played_free_games=true");
        resp.EnsureSuccessStatusCode();

        var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var list = new List<Game>();

        if (!doc.TryGetProperty("response", out var r) || !r.TryGetProperty("games", out var gamesEl))
            return list;

        foreach (var g in gamesEl.EnumerateArray())
        {
            if (!g.TryGetProperty("appid", out var appid) || !g.TryGetProperty("name", out var name)) continue;
            int playtime = g.TryGetProperty("playtime_forever", out var pt) && pt.ValueKind == JsonValueKind.Number
                ? pt.GetInt32() : 0;
            int playtime2weeks = g.TryGetProperty("playtime_2weeks", out var pt2) && pt2.ValueKind == JsonValueKind.Number
                ? pt2.GetInt32() : 0;
            string icon = g.TryGetProperty("img_icon_url", out var ico) ? ico.GetString() ?? "" : "";

            list.Add(new Game
            {
                AppId = appid.GetInt32(),
                Name = name.GetString()!,
                PlaytimeMinutes = playtime,
                Playtime2WeeksMinutes = playtime2weeks,
                IconUrl = string.IsNullOrEmpty(icon) ? "" :
                    $"https://media.steampowered.com/steamcommunity/public/images/apps/{appid.GetInt32()}/{icon}.jpg"
            });
        }

        _cache.Set(key, list, TimeSpan.FromMinutes(15));
        return list;
    }

    // ─────────────────────────────────────────────────────────────
    //  App Details (preço + imagem)
    // ─────────────────────────────────────────────────────────────
    public async Task<(double price, string imageUrl)> GetAppDetailsAsync(int appId)
    {
        var key = $"app:{appId}";
        if (_cache.TryGetValue(key, out (double, string) cached)) return cached;

        var resp = await GetAsync(
            $"https://store.steampowered.com/api/appdetails?appids={appId}&cc=br&l=pt&filters=price_overview,header_image");
        if (!resp.IsSuccessStatusCode) return (0, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg");

        var json = await resp.Content.ReadAsStringAsync();
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            if (!doc.TryGetProperty(appId.ToString(), out var app)) return (0, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg");
            if (!app.TryGetProperty("success", out var s) || !(s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.Number && s.GetInt32() == 1)))
                return (0, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg");
            if (!app.TryGetProperty("data", out var data)) return (0, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg");

            double price = 0;
            if (data.TryGetProperty("price_overview", out var po) && po.TryGetProperty("final", out var fin))
                price = fin.GetDouble() / 100.0;

            string img = data.TryGetProperty("header_image", out var hi) ? hi.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(img)) img = $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";

            var result = (price, img);
            _cache.Set(key, result, TimeSpan.FromHours(6));
            return result;
        }
        catch
        {
            return (0, $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg");
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Market Price
    // ─────────────────────────────────────────────────────────────
    public async Task<double> GetMarketPriceAsync(string name, int appId)
    {
        var key = $"mp:{appId}:{name}";
        if (_cache.TryGetValue(key, out double cp)) return cp;

        var url = $"https://steamcommunity.com/market/priceoverview/?appid={appId}&currency=7&market_hash_name={Uri.EscapeDataString(name)}";
        var resp = await GetAsync(url);
        if (!resp.IsSuccessStatusCode) return 0;

        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            if (!doc.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.True) return 0;

            string? priceStr = null;
            if (doc.TryGetProperty("lowest_price", out var lp)) priceStr = lp.GetString();
            if (string.IsNullOrWhiteSpace(priceStr) && doc.TryGetProperty("median_price", out var mp)) priceStr = mp.GetString();
            if (string.IsNullOrWhiteSpace(priceStr)) return 0;

            priceStr = priceStr.Replace("R$", "").Replace(".", "").Replace(",", ".").Trim();
            if (!double.TryParse(priceStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var price)) return 0;

            _cache.Set(key, price, TimeSpan.FromMinutes(30));
            return price;
        }
        catch { return 0; }
    }

    // ─────────────────────────────────────────────────────────────
    //  Calcular jogos — PARALELO com semáforo ajustado
    // ─────────────────────────────────────────────────────────────
    public async Task<(double total, List<Game> games)> CalculateGamesValueAsync(
        string steamId, Func<int, string, Task>? progress = null)
    {
        if (progress != null) await progress(15, "Buscando biblioteca de jogos...");
        var games = await GetOwnedGamesAsync(steamId);
        double total = 0;
        var resultGames = new List<Game>();

        if (progress != null) await progress(20, $"Calculando preços de {games.Count} jogos...");

        // Paralelismo com limite de 15 requisições simultâneas
        var sem = new SemaphoreSlim(15);
        int done = 0;
        var tasks = games.Select(async g =>
        {
            await sem.WaitAsync();
            try
            {
                var (price, img) = await GetAppDetailsAsync(g.AppId);
                g.Price = price;
                g.ImageUrl = img;
                Interlocked.Increment(ref done);
                if (progress != null)
                    await progress(20 + (done * 28 / Math.Max(games.Count, 1)), $"Calculado: {g.Name}");
                return g;
            }
            finally { sem.Release(); }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        total = results.Sum(g => g.Price);

        RecordAccountSnapshot(steamId, total);
        if (progress != null) await progress(50, $"{games.Count} jogos calculados");
        return (total, results.ToList());
    }

    // ─────────────────────────────────────────────────────────────
    //  Calcular jogos RÁPIDO para amigos (sem delay, paralelo)
    // ─────────────────────────────────────────────────────────────
    public async Task<(int count, double total)> CalculateGamesFastAsync(string steamId)
    {
        var games = await GetOwnedGamesAsync(steamId);
        if (games.Count == 0) return (0, 0);

        var sem = new SemaphoreSlim(20);
        var tasks = games.Select(async g =>
        {
            await sem.WaitAsync();
            try { var (p, _) = await GetAppDetailsAsync(g.AppId); return p; }
            finally { sem.Release(); }
        }).ToList();

        var prices = await Task.WhenAll(tasks);
        var total = prices.Sum();
        RecordAccountSnapshot(steamId, total);
        return (games.Count, total);
    }

    // ─────────────────────────────────────────────────────────────
    //  Inventário — PARALELO
    // ─────────────────────────────────────────────────────────────
    public async Task<JsonElement?> GetInventoryAsync(string steamId, int appId, int contextId = 2)
    {
        var key = $"inv:{steamId}:{appId}:{contextId}";
        if (_cache.TryGetValue(key, out JsonElement ci)) return ci;

        var url = $"https://steamcommunity.com/inventory/{steamId}/{appId}/{contextId}?l=pt&count=5000";
        var resp = await GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync();
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            if (!doc.TryGetProperty("success", out var s)) return null;
            bool ok = s.ValueKind == JsonValueKind.True || (s.ValueKind == JsonValueKind.Number && s.GetInt32() == 1);
            if (!ok || !doc.TryGetProperty("assets", out _)) return null;

            _cache.Set(key, doc, TimeSpan.FromMinutes(15));
            return doc;
        }
        catch { return null; }
    }

    public async Task<(double total, List<InventoryItem> items)> CalculateInventoryValueAsync(
        string steamId, int appId, string gameName,
        Func<int, string, Task>? progress = null)
    {
        var items = new List<InventoryItem>();
        var inv = await GetInventoryAsync(steamId, appId);
        if (inv == null) return (0, items);

        if (!inv.Value.TryGetProperty("descriptions", out var descsEl)) return (0, items);

        // Indexar descrições por classid
        var descs = new Dictionary<long, JsonElement>();
        foreach (var d in descsEl.EnumerateArray())
        {
            if (!d.TryGetProperty("classid", out var cid)) continue;
            long id = cid.ValueKind == JsonValueKind.Number ? cid.GetInt64() :
                long.TryParse(cid.GetString(), out var l) ? l : -1;
            if (id >= 0) descs[id] = d;
        }

        if (!inv.Value.TryGetProperty("assets", out var assetsEl)) return (0, items);
        var assets = assetsEl.EnumerateArray().ToList();

        // Itens marketáveis únicos para precificar
        var marketableNames = new ConcurrentBag<(long cid, string name, string imageUrl)>();
        foreach (var asset in assets)
        {
            if (!asset.TryGetProperty("classid", out var cidEl)) continue;
            long cid = cidEl.ValueKind == JsonValueKind.Number ? cidEl.GetInt64() :
                long.TryParse(cidEl.GetString(), out var l) ? l : -1;
            if (cid < 0 || !descs.TryGetValue(cid, out var desc)) continue;

            if (!desc.TryGetProperty("marketable", out var mkt)) continue;
            bool isMarketable = mkt.ValueKind == JsonValueKind.True ||
                (mkt.ValueKind == JsonValueKind.Number && mkt.GetInt32() == 1);
            if (!isMarketable) continue;

            if (!desc.TryGetProperty("market_hash_name", out var mhn)) continue;
            var mhnStr = mhn.GetString()!;

            string imgUrl = "";
            if (desc.TryGetProperty("icon_url_large", out var ilu) && ilu.ValueKind == JsonValueKind.String)
                imgUrl = BuildInventoryImageUrl(ilu.GetString() ?? "");
            else if (desc.TryGetProperty("icon_url", out var iu) && iu.ValueKind == JsonValueKind.String)
                imgUrl = BuildInventoryImageUrl(iu.GetString() ?? "");

            marketableNames.Add((cid, mhnStr, imgUrl));
        }

        // Preços em paralelo, limitado a 10 simultâneos (respeitando rate limit do market)
        var sem = new SemaphoreSlim(10);
        int done = 0;
        int total_count = marketableNames.Count;

        var priceTasks = marketableNames.Select(async tuple =>
        {
            await sem.WaitAsync();
            try
            {
                var price = await GetMarketPriceAsync(tuple.name, appId);
                Interlocked.Increment(ref done);
                if (progress != null)
                    await progress(50 + (done * 35 / Math.Max(total_count, 1)),
                        $"Preço: {tuple.name.Substring(0, Math.Min(30, tuple.name.Length))}...");
                // Pequeno delay para não bater no rate limit do Steam Market
                await Task.Delay(300);
                return new InventoryItem { Name = tuple.name, Price = price, ImageUrl = tuple.imageUrl };
            }
            finally { sem.Release(); }
        }).ToList();

        var results = await Task.WhenAll(priceTasks);
        items = results.ToList();
        double totalValue = items.Sum(i => i.Price);

        return (totalValue, items);
    }

    private string BuildInventoryImageUrl(string icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return "";
        if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return icon;
        return $"https://steamcommunity-a.akamaihd.net/economy/image/{icon.TrimStart('/')}";
    }

    // ─────────────────────────────────────────────────────────────
    //  Lista de amigos
    // ─────────────────────────────────────────────────────────────
    public async Task<List<(string steamId, long friendSince)>> GetFriendListAsync(string steamId)
    {
        var key = $"fl:{steamId}";
        if (_cache.TryGetValue(key, out List<(string, long)> cached)) return cached;

        var resp = await GetAsync(
            $"https://api.steampowered.com/ISteamUser/GetFriendList/v1/?key={SteamApiKey}&steamid={steamId}&relationship=all");
        if (!resp.IsSuccessStatusCode) return new();

        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var list = new List<(string, long)>();
            if (doc.TryGetProperty("friendslist", out var fl) && fl.TryGetProperty("friends", out var friends))
            {
                foreach (var f in friends.EnumerateArray())
                {
                    if (!f.TryGetProperty("steamid", out var sid)) continue;
                    long since = f.TryGetProperty("friend_since", out var fs) ? fs.GetInt64() : 0;
                    list.Add((sid.GetString()!, since));
                }
            }
            _cache.Set(key, list, TimeSpan.FromMinutes(15));
            return list;
        }
        catch { return new(); }
    }

    // ─────────────────────────────────────────────────────────────
    //  Recent Games (últimos jogos jogados)
    // ─────────────────────────────────────────────────────────────
    public async Task<List<Game>> GetRecentlyPlayedGamesAsync(string steamId, int count = 10)
    {
        var key = $"recent:{steamId}:{count}";
        if (_cache.TryGetValue(key, out List<Game> cached)) return cached;

        var resp = await GetAsync(
            $"https://api.steampowered.com/IPlayerService/GetRecentlyPlayedGames/v1/?key={SteamApiKey}&steamid={steamId}&count={count}");
        if (!resp.IsSuccessStatusCode) return new();

        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var list = new List<Game>();
            if (!doc.TryGetProperty("response", out var r) || !r.TryGetProperty("games", out var gamesEl)) return list;

            foreach (var g in gamesEl.EnumerateArray())
            {
                if (!g.TryGetProperty("appid", out var appid) || !g.TryGetProperty("name", out var name)) continue;
                int pt = g.TryGetProperty("playtime_2weeks", out var p) ? p.GetInt32() : 0;
                list.Add(new Game
                {
                    AppId = appid.GetInt32(),
                    Name = name.GetString()!,
                    PlaytimeMinutes = g.TryGetProperty("playtime_forever", out var pf) ? pf.GetInt32() : 0,
                    Playtime2WeeksMinutes = pt,
                    ImageUrl = $"https://cdn.akamai.steamstatic.com/steam/apps/{appid.GetInt32()}/header.jpg"
                });
            }

            _cache.Set(key, list, TimeSpan.FromMinutes(10));
            return list;
        }
        catch { return new(); }
    }

    // ─────────────────────────────────────────────────────────────
    //  Achievements
    // ─────────────────────────────────────────────────────────────
    public async Task<(int total, int unlocked, double percent)> GetPlayerAchievementsAsync(string steamId, int appId)
    {
        var resp = await GetAsync(
            $"https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/?key={SteamApiKey}&steamid={steamId}&appid={appId}");
        if (!resp.IsSuccessStatusCode) return (0, 0, 0);

        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            if (!doc.TryGetProperty("playerstats", out var ps) || !ps.TryGetProperty("achievements", out var ach))
                return (0, 0, 0);

            int total = 0, unlocked = 0;
            foreach (var a in ach.EnumerateArray())
            {
                total++;
                if (a.TryGetProperty("achieved", out var ac) && ac.ValueKind == JsonValueKind.Number && ac.GetInt32() == 1)
                    unlocked++;
            }
            double pct = total > 0 ? (double)unlocked / total * 100.0 : 0;
            return (total, unlocked, pct);
        }
        catch { return (0, 0, 0); }
    }

    // ─────────────────────────────────────────────────────────────
    //  User Stats for Game
    // ─────────────────────────────────────────────────────────────
    public async Task<Dictionary<string, double>> GetUserStatsForGameAsync(string steamId, int appId)
    {
        var resp = await GetAsync(
            $"https://api.steampowered.com/ISteamUserStats/GetUserStatsForGame/v2/?key={SteamApiKey}&steamid={steamId}&appid={appId}");
        if (!resp.IsSuccessStatusCode) return new();

        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var stats = new Dictionary<string, double>();
            if (doc.TryGetProperty("playerstats", out var ps) && ps.TryGetProperty("stats", out var s))
            {
                foreach (var stat in s.EnumerateArray())
                {
                    if (stat.TryGetProperty("name", out var n) && stat.TryGetProperty("value", out var v))
                        stats[n.GetString()!] = v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                }
            }
            return stats;
        }
        catch { return new(); }
    }

    // ─────────────────────────────────────────────────────────────
    //  Bans
    // ─────────────────────────────────────────────────────────────
    public async Task<PlayerBans?> GetPlayerBansAsync(string steamId)
    {
        var key = $"bans:{steamId}";
        if (_cache.TryGetValue(key, out PlayerBans? cb)) return cb;

        var resp = await GetAsync(
            $"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={SteamApiKey}&steamids={steamId}");
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

    // ─────────────────────────────────────────────────────────────
    //  Level / badges
    // ─────────────────────────────────────────────────────────────
    public async Task<int> GetSteamLevelAsync(string steamId)
    {
        var key = $"lvl:{steamId}";
        if (_cache.TryGetValue(key, out int cl)) return cl;

        var resp = await GetAsync(
            $"https://api.steampowered.com/IPlayerService/GetSteamLevel/v1/?key={SteamApiKey}&steamid={steamId}");
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

    public async Task<List<Badge>> GetBadgesAsync(string steamId)
    {
        var key = $"badges:{steamId}";
        if (_cache.TryGetValue(key, out List<Badge> cb)) return cb;

        var resp = await GetAsync(
            $"https://api.steampowered.com/IPlayerService/GetBadges/v1/?key={SteamApiKey}&steamid={steamId}");
        if (!resp.IsSuccessStatusCode) return new();

        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var list = new List<Badge>();
            if (doc.TryGetProperty("response", out var r) && r.TryGetProperty("badges", out var badges))
            {
                foreach (var b in badges.EnumerateArray())
                {
                    list.Add(new Badge
                    {
                        BadgeId = b.TryGetProperty("badgeid", out var bid) ? bid.GetInt32() : 0,
                        Level = b.TryGetProperty("level", out var lv) ? lv.GetInt32() : 0,
                        CompletionTime = b.TryGetProperty("completion_time", out var ct) ? ct.GetInt64() : 0,
                        Xp = b.TryGetProperty("xp", out var xp) ? xp.GetInt32() : 0,
                        AppId = b.TryGetProperty("appid", out var ai) ? ai.GetInt32() : 0,
                        CommunityItemId = b.TryGetProperty("communityitemid", out var ci) ? ci.GetString() ?? "" : "",
                        BorderColor = b.TryGetProperty("border_color", out var bc) ? bc.GetInt32() : 0,
                        ScarcityScore = b.TryGetProperty("scarcity", out var sc) ? sc.GetInt32() : 0
                    });
                }
            }
            _cache.Set(key, list, TimeSpan.FromHours(1));
            return list;
        }
        catch { return new(); }
    }

    // ─────────────────────────────────────────────────────────────
    //  Wishlist
    // ─────────────────────────────────────────────────────────────
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
                string name = v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                int priority = v.TryGetProperty("priority", out var p) ? p.GetInt32() : 999;
                long added = v.TryGetProperty("added", out var a) ? a.GetInt64() : 0;
                string capsule = v.TryGetProperty("capsule", out var cap) ? cap.GetString() ?? "" : $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";

                list.Add(new WishlistItem
                {
                    AppId = appId,
                    Name = name,
                    Priority = priority,
                    Added = added,
                    ImageUrl = capsule
                });
            }

            list = list.OrderBy(w => w.Priority).ToList();
            _cache.Set(key, list, TimeSpan.FromMinutes(30));
            return list;
        }
        catch { return new(); }
    }

    // ─────────────────────────────────────────────────────────────
    //  Grupos
    // ─────────────────────────────────────────────────────────────
    public async Task<List<SteamGroup>> GetUserGroupsAsync(string steamId)
    {
        var key = $"groups:{steamId}";
        if (_cache.TryGetValue(key, out List<SteamGroup> cg)) return cg;

        var resp = await GetAsync(
            $"https://api.steampowered.com/ISteamUser/GetUserGroupList/v1/?key={SteamApiKey}&steamid={steamId}");
        if (!resp.IsSuccessStatusCode) return new();

        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
            var list = new List<SteamGroup>();
            if (doc.TryGetProperty("response", out var r) && r.TryGetProperty("groups", out var groups))
            {
                foreach (var g in groups.EnumerateArray())
                {
                    if (g.TryGetProperty("gid", out var gid))
                        list.Add(new SteamGroup { GroupId = gid.GetString() ?? "" });
                }
            }
            _cache.Set(key, list, TimeSpan.FromHours(1));
            return list;
        }
        catch { return new(); }
    }

    // ─────────────────────────────────────────────────────────────
    //  Snapshots de valor histórico
    // ─────────────────────────────────────────────────────────────
    public List<(DateTime time, double total)> GetAccountSnapshots(string steamId)
    {
        lock (_accountSnapshots)
            return _accountSnapshots.TryGetValue(steamId, out var snaps) ? snaps.ToList() : new();
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

// ─────────────────────────────────────────────────────────────
//  Modelos de dados
// ─────────────────────────────────────────────────────────────
public class Game
{
    public int AppId { get; set; }
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public string ImageUrl { get; set; } = "";
    public string IconUrl { get; set; } = "";
    public int PlaytimeMinutes { get; set; }
    public int Playtime2WeeksMinutes { get; set; }
}

public class InventoryItem
{
    public string Name { get; set; } = "";
    public double Price { get; set; }
    public string ImageUrl { get; set; } = "";
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
    public string CommunityItemId { get; set; } = "";
    public int BorderColor { get; set; }
    public int ScarcityScore { get; set; }
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
