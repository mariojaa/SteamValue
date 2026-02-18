using Microsoft.AspNetCore.SignalR;
using System.Text.Json;

namespace SteamValue.Services
{
    public class CalculationHub : Hub
    {
        private readonly SteamService _steam;

        public CalculationHub(SteamService steam) { _steam = steam; }

        // ─── Main Calculation ────────────────────────────────────────
        public async Task StartCalculation(string profileUrl, bool calculateGames, bool calculateInventory)
        {
            Func<int, string, Task> progress = async (p, m) =>
            {
                try { await Clients.Caller.SendAsync("UpdateProgress", p, m); } catch { }
            };
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl, progress);
                double totalValue = 0;

                var summaryTask = _steam.GetPlayerSummariesAsync(steamId);
                var levelTask = _steam.GetSteamLevelAsync(steamId);
                var bansTask = _steam.GetPlayerBansAsync(steamId);
                var recentTask = _steam.GetRecentlyPlayedGamesAsync(steamId, 5);

                await Task.WhenAll(summaryTask, levelTask, bansTask, recentTask);

                var summary = await summaryTask;
                var level = await levelTask;
                var bans = await bansTask;
                var recentGames = await recentTask;

                if (summary != null)
                {
                    var player = summary.Value.GetProperty("response").GetProperty("players").EnumerateArray().FirstOrDefault();
                    if (player.ValueKind != JsonValueKind.Undefined)
                    {
                        await Clients.Caller.SendAsync("ReceiveProfileInfo", new
                        {
                            steamId,
                            name = player.TryGetProperty("personaname", out var pn) ? pn.GetString() : "",
                            avatar = player.TryGetProperty("avatarfull", out var af) ? af.GetString() : "",
                            personastate = player.TryGetProperty("personastate", out var ps) ? ps.GetInt32() : 0,
                            country = player.TryGetProperty("loccountrycode", out var cc) ? cc.GetString() : "",
                            lastLogoff = player.TryGetProperty("lastlogoff", out var ll) ? ll.GetInt64() : 0,
                            profileUrl = player.TryGetProperty("profileurl", out var pu) ? pu.GetString() : "",
                            created = player.TryGetProperty("timecreated", out var tc) ? tc.GetInt64() : 0,
                            level,
                            bans = bans != null ? new { bans.VacBanned, bans.NumberOfVacBans, bans.DaysSinceLastBan, bans.NumberOfGameBans, bans.CommunityBanned, bans.EconomyBan } : null,
                            recentGames = recentGames.Select(g => new { appId = g.AppId, name = g.Name, playtime2weeks = g.Playtime2WeeksMinutes, playtimeForever = g.PlaytimeMinutes, imageUrl = g.ImageUrl })
                        });
                    }
                }

                if (calculateGames)
                {
                    await progress(20, "Calculando valor dos jogos...");
                    var (gamesTotal, gamesList) = await _steam.CalculateGamesValueAsync(steamId, progress);
                    totalValue += gamesTotal;
                    await Clients.Caller.SendAsync("ReceiveGamesData", gamesList.Select(g => new
                    {
                        name = g.Name, price = g.Price, imageUrl = g.ImageUrl,
                        appId = g.AppId, playtimeMinutes = g.PlaytimeMinutes,
                        playtime2weeks = g.Playtime2WeeksMinutes,
                        genre = g.Genre, developer = g.Developer, metacritic = g.MetacriticScore
                    }), gamesTotal);
                }

                if (calculateInventory)
                {
                    var inventories = new[] { (730, "CS2"), (570, "Dota 2"), (440, "TF2"), (252490, "Rust"), (1172470, "Apex Legends") };
                    int startPct = 50;
                    foreach (var (appId, gameName) in inventories)
                    {
                        await progress(startPct, $"Analisando inventário {gameName}...");
                        var (invTotal, invList) = await _steam.CalculateInventoryValueAsync(steamId, appId, gameName, progress);
                        totalValue += invTotal;
                        if (invList.Count > 0)
                        {
                            await Clients.Caller.SendAsync("ReceiveInventoryData", gameName, invList.Select(it => new
                            {
                                name = it.Name, price = it.Price, unitPrice = it.UnitPrice,
                                count = it.Count, imageUrl = it.ImageUrl, type = it.Type,
                                rarity = it.Rarity, appId
                            }), invTotal);
                        }
                        startPct = Math.Min(startPct + 8, 88);
                    }
                }

                // Wishlist
                var wishlist = await _steam.GetWishlistAsync(steamId);
                if (wishlist.Count > 0)
                    await Clients.Caller.SendAsync("ReceiveWishlist", wishlist.Take(20).Select(w => new
                    { appId = w.AppId, name = w.Name, imageUrl = w.ImageUrl, priority = w.Priority }));

                // Badges
                var badges = await _steam.GetBadgesAsync(steamId);
                if (badges.Count > 0)
                    await Clients.Caller.SendAsync("ReceiveBadges", new { count = badges.Count, totalXp = badges.Sum(b => b.Xp) });

                // Playtime analytics
                var analytics = await _steam.GetPlaytimeAnalyticsAsync(steamId);
                await Clients.Caller.SendAsync("ReceivePlaytimeAnalytics", new
                {
                    totalGames = analytics.TotalGames,
                    playedGames = analytics.PlayedGames,
                    neverPlayedGames = analytics.NeverPlayedGames,
                    totalHours = Math.Round(analytics.TotalHours, 1),
                    averageHours = Math.Round(analytics.AverageHoursPerGame, 1),
                    playedPercent = Math.Round(analytics.PlaytimePercentile, 1),
                    mostPlayed = analytics.MostPlayedGames.Select(g => new
                    { g.AppId, g.Name, hours = Math.Round(g.PlaytimeMinutes / 60.0, 1), g.ImageUrl })
                });

                _steam.RecordAccountSnapshot(steamId, totalValue);
                await progress(95, "Finalizando...");
                await Clients.Caller.SendAsync("ReceiveTotalValue", totalValue);
                await progress(100, "Concluído!");
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Friends ─────────────────────────────────────────────────
        public async Task GetFriends(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var friendList = await _steam.GetFriendListAsync(steamId);
                if (!friendList.Any()) { await Clients.Caller.SendAsync("ReceiveFriends", new List<object>()); return; }

                var friendIds = friendList.Select(f => f.steamId).ToList();
                var summaryJson = await _steam.GetPlayerSummariesAsync(string.Join(",", friendIds));
                if (summaryJson == null) { await Clients.Caller.SendAsync("ReceiveFriends", new List<object>()); return; }

                var friendSinceLookup = friendList.ToDictionary(f => f.steamId, f => f.friendSince);
                var friends = summaryJson.Value.GetProperty("response").GetProperty("players").EnumerateArray()
                    .Select(p =>
                    {
                        var sid = p.GetProperty("steamid").GetString()!;
                        int state = p.TryGetProperty("personastate", out var ps) ? ps.GetInt32() : 0;
                        long since = friendSinceLookup.TryGetValue(sid, out var s) ? s : 0;
                        return new
                        {
                            id = sid,
                            name = p.TryGetProperty("personaname", out var pn) ? pn.GetString() : "",
                            avatar = p.TryGetProperty("avatarfull", out var af) && !string.IsNullOrEmpty(af.GetString())
                                ? af.GetString() : (p.TryGetProperty("avatarmedium", out var am) ? am.GetString() : ""),
                            visibility = p.TryGetProperty("communityvisibilitystate", out var cv) ? cv.GetInt32() : 1,
                            personastate = state, isOnline = state >= 1,
                            lastLogoff = p.TryGetProperty("lastlogoff", out var ll) ? ll.GetInt64() : 0,
                            country = p.TryGetProperty("loccountrycode", out var cc) ? cc.GetString() : "",
                            friendSince = since,
                            gameId = p.TryGetProperty("gameid", out var gid) ? gid.GetString() : "",
                            gameExtra = p.TryGetProperty("gameextrainfo", out var ge) ? ge.GetString() : ""
                        };
                    })
                    .OrderByDescending(f => f.isOnline).ThenByDescending(f => f.lastLogoff).ToList();

                await Clients.Caller.SendAsync("ReceiveFriends", friends);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Compute Totals for Friend ────────────────────────────────
        public async Task ComputeTotalsForSteamId(string steamId, bool calculateGames, bool calculateInventory)
        {
            Func<int, string, Task> progress = async (p, m) =>
            {
                try { await Clients.Caller.SendAsync("UpdateFriendProgress", steamId, p, m); } catch { }
            };
            try
            {
                int gamesCount = 0; double gamesValue = 0; int inventoryCount = 0; double inventoryValue = 0;
                var tasks = new List<Task>();
                if (calculateGames)
                    tasks.Add(Task.Run(async () => { var (c, t) = await _steam.CalculateGamesFastAsync(steamId); gamesCount = c; gamesValue = t; }));
                if (calculateInventory)
                {
                    // Check ALL major game inventories
                    var invApps = new[] { (730, "CS2"), (570, "Dota 2"), (440, "TF2"), (252490, "Rust") };
                    tasks.Add(Task.Run(async () =>
                    {
                        double invTotal = 0; int invItems = 0;
                        foreach (var (appId, gname) in invApps)
                        {
                            var (t, items) = await _steam.CalculateInventoryValueAsync(steamId, appId, gname, progress);
                            invTotal += t; invItems += items.Count;
                        }
                        inventoryValue = invTotal; inventoryCount = invItems;
                    }));
                }
                await Task.WhenAll(tasks);
                await Clients.Caller.SendAsync("ReceiveFriendTotalsDetailed", steamId, new
                { gamesCount, gamesValue, inventoryCount, inventoryValue, total = gamesValue + inventoryValue });
                await Clients.Caller.SendAsync("ReceiveFriendTotal", steamId, gamesValue + inventoryValue);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Friend Games ────────────────────────────────────────────
        public async Task GetFriendGames(string steamId)
        {
            try
            {
                var games = await _steam.GetOwnedGamesAsync(steamId);
                var sem = new SemaphoreSlim(15);
                var tasks = games.Select(async g =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var (price, image, genre, dev, meta) = await _steam.GetAppDetailsAsync(g.AppId);
                        return new { appId = g.AppId, name = g.Name, playtimeMinutes = g.PlaytimeMinutes, price, imageUrl = string.IsNullOrEmpty(image) ? $"https://cdn.akamai.steamstatic.com/steam/apps/{g.AppId}/header.jpg" : image, genre, developer = dev, metacritic = meta };
                    }
                    finally { sem.Release(); }
                }).ToList();
                var results = await Task.WhenAll(tasks);
                await Clients.Caller.SendAsync("ReceiveFriendGames", steamId, results.OrderByDescending(g => g.price).ToList());
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Friend Inventory (ALL game inventories) ──────────────────
        public async Task GetFriendInventory(string steamId, int appId)
        {
            try
            {
                var (total, items) = await _steam.CalculateInventoryValueAsync(steamId, appId, "");
                await Clients.Caller.SendAsync("ReceiveFriendInventory", steamId, appId,
                    items.Select(it => new { name = it.Name, price = it.Price, unitPrice = it.UnitPrice, count = it.Count, imageUrl = it.ImageUrl, rarity = it.Rarity }).OrderByDescending(it => it.price).ToList(), total);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Profile Summary ──────────────────────────────────────────
        public async Task GetProfileSummary(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                await Task.WhenAll(
                    _steam.GetPlayerSummariesAsync(steamId),
                    _steam.GetSteamLevelAsync(steamId),
                    _steam.GetPlayerBansAsync(steamId),
                    _steam.GetRecentlyPlayedGamesAsync(steamId, 5)
                );
                var summary = await _steam.GetPlayerSummariesAsync(steamId);
                var level = await _steam.GetSteamLevelAsync(steamId);
                var bans = await _steam.GetPlayerBansAsync(steamId);
                var recent = await _steam.GetRecentlyPlayedGamesAsync(steamId, 5);
                var badges = await _steam.GetBadgesAsync(steamId);
                var groups = await _steam.GetUserGroupsAsync(steamId);

                await Clients.Caller.SendAsync("ReceiveProfileSummary", new
                {
                    summary, level, bans,
                    badgeCount = badges.Count, totalXp = badges.Sum(b => b.Xp),
                    groupCount = groups.Count,
                    recentGames = recent.Select(g => new { appId = g.AppId, name = g.Name, playtime2weeks = g.Playtime2WeeksMinutes, imageUrl = g.ImageUrl })
                });
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Wishlist ─────────────────────────────────────────────────
        public async Task GetWishlist(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var wishlist = await _steam.GetWishlistAsync(steamId);
                // Enrich with current player count for fun
                await Clients.Caller.SendAsync("ReceiveWishlist", wishlist.Take(30).Select(w => new
                { appId = w.AppId, name = w.Name, imageUrl = w.ImageUrl, priority = w.Priority, added = w.Added }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Badges & Level ───────────────────────────────────────────
        public async Task GetBadgesAndLevel(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var level = await _steam.GetSteamLevelAsync(steamId);
                var badges = await _steam.GetBadgesAsync(steamId);
                await Clients.Caller.SendAsync("ReceiveBadgesAndLevel", new
                {
                    level, badgeCount = badges.Count, totalXp = badges.Sum(b => b.Xp),
                    badges = badges.Take(12).Select(b => new { b.BadgeId, b.Level, b.Xp, b.AppId })
                });
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Achievements ─────────────────────────────────────────────
        public async Task GetAchievements(string profileUrl, int appId)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var (total, unlocked, pct, list) = await _steam.GetPlayerAchievementsAsync(steamId, appId);
                await Clients.Caller.SendAsync("ReceiveAchievements", appId, total, unlocked, pct, list.Take(50).Select(a => new { a.ApiName, a.Name, a.Description, a.Achieved, a.UnlockTime }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── NEW: Playtime Analytics ──────────────────────────────────
        public async Task GetPlaytimeAnalytics(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var analytics = await _steam.GetPlaytimeAnalyticsAsync(steamId);
                await Clients.Caller.SendAsync("ReceivePlaytimeAnalytics", new
                {
                    totalGames = analytics.TotalGames,
                    playedGames = analytics.PlayedGames,
                    neverPlayedGames = analytics.NeverPlayedGames,
                    totalHours = Math.Round(analytics.TotalHours, 1),
                    averageHours = Math.Round(analytics.AverageHoursPerGame, 1),
                    playedPercent = Math.Round(analytics.PlaytimePercentile, 1),
                    mostPlayed = analytics.MostPlayedGames.Select(g => new { g.AppId, g.Name, hours = Math.Round(g.PlaytimeMinutes / 60.0, 1), imageUrl = g.ImageUrl }),
                    recentlyPlayed = analytics.RecentlyPlayed.Select(g => new { g.AppId, g.Name, hours2w = Math.Round(g.Playtime2WeeksMinutes / 60.0, 1), imageUrl = g.ImageUrl })
                });
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── NEW: Profile Comparison ──────────────────────────────────
        public async Task CompareProfiles(string profileUrl1, string profileUrl2)
        {
            try
            {
                var sid1Task = _steam.ResolveSteamIdAsync(profileUrl1);
                var sid2Task = _steam.ResolveSteamIdAsync(profileUrl2);

                await Task.WhenAll(sid1Task, sid2Task);

                var steamId1 = await sid1Task;
                var steamId2 = await sid2Task;

                if (string.IsNullOrEmpty(steamId1) || string.IsNullOrEmpty(steamId2))
                {
                    await Clients.Caller.SendAsync("ReceiveError", "Erro ao resolver SteamID");
                    return;
                }

                var comparisonTask = _steam.CompareProfilesAsync(steamId1, steamId2);
                var summariesTask = _steam.GetPlayerSummariesAsync($"{steamId1},{steamId2}");

                await Task.WhenAll(comparisonTask, summariesTask);

                var comparison = await comparisonTask;
                var summaries = await summariesTask;

                var players = new List<JsonElement>();

                // CORREÇÃO PARA JsonElement?
                if (summaries.HasValue &&
                    summaries.Value.ValueKind == JsonValueKind.Object &&
                    summaries.Value.TryGetProperty("response", out var response) &&
                    response.TryGetProperty("players", out var playersElement))
                {
                    players = playersElement.EnumerateArray().ToList();
                }

                var player1 = players.FirstOrDefault(p =>
                    p.TryGetProperty("steamid", out var id) &&
                    id.GetString() == steamId1);

                var player2 = players.FirstOrDefault(p =>
                    p.TryGetProperty("steamid", out var id) &&
                    id.GetString() == steamId2);

                string name1 = player1.ValueKind != JsonValueKind.Undefined &&
                               player1.TryGetProperty("personaname", out var pn1)
                    ? pn1.GetString() ?? ""
                    : "";

                string name2 = player2.ValueKind != JsonValueKind.Undefined &&
                               player2.TryGetProperty("personaname", out var pn2)
                    ? pn2.GetString() ?? ""
                    : "";

                string avatar1 = player1.ValueKind != JsonValueKind.Undefined &&
                                 player1.TryGetProperty("avatarfull", out var av1)
                    ? av1.GetString() ?? ""
                    : "";

                string avatar2 = player2.ValueKind != JsonValueKind.Undefined &&
                                 player2.TryGetProperty("avatarfull", out var av2)
                    ? av2.GetString() ?? ""
                    : "";

                await Clients.Caller.SendAsync("ReceiveProfileComparison", new
                {
                    name1,
                    name2,
                    avatar1,
                    avatar2,

                    gamesCount1 = comparison.GamesCount1,
                    gamesCount2 = comparison.GamesCount2,

                    level1 = comparison.Level1,
                    level2 = comparison.Level2,

                    badgeCount1 = comparison.BadgeCount1,
                    badgeCount2 = comparison.BadgeCount2,

                    totalXp1 = comparison.TotalXp1,
                    totalXp2 = comparison.TotalXp2,

                    totalHours1 = Math.Round(comparison.TotalHours1, 0),
                    totalHours2 = Math.Round(comparison.TotalHours2, 0),

                    commonGamesCount = comparison.CommonGamesCount,
                    commonGames = comparison.CommonGames.Take(12),

                    exclusive1 = comparison.ExclusiveGames1Count,
                    exclusive2 = comparison.ExclusiveGames2Count
                });
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }


        // ─── NEW: Live Player Counts ──────────────────────────────────
        public async Task GetLivePlayerCounts(int[] appIds)
        {
            try
            {
                var sem = new SemaphoreSlim(10);
                var tasks = appIds.Select(async appId =>
                {
                    await sem.WaitAsync();
                    try { return new { appId, count = await _steam.GetNumberOfCurrentPlayersAsync(appId) }; }
                    finally { sem.Release(); }
                }).ToList();
                var results = await Task.WhenAll(tasks);
                await Clients.Caller.SendAsync("ReceiveLivePlayerCounts", results);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── NEW: Game Market Price ───────────────────────────────────
        public async Task GetMarketItemPrice(int appId, string marketHashName)
        {
            try
            {
                var price = await _steam.GetMarketPriceAsync(marketHashName, appId);
                var listings = await _steam.GetMarketListingsAsync(appId, marketHashName, 5);
                await Clients.Caller.SendAsync("ReceiveMarketItemPrice", new
                { appId, name = marketHashName, price, listings = listings.Select(l => new { l.Price }) });
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── NEW: Full Inventory with All Games ───────────────────────
        public async Task GetFullInventory(string profileUrl)
        {
            Func<int, string, Task> progress = async (p, m) =>
            {
                try { await Clients.Caller.SendAsync("UpdateProgress", p, m); } catch { }
            };
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var allInventories = new[] { (730, "CS2"), (570, "Dota 2"), (440, "TF2"), (252490, "Rust"), (1172470, "Apex Legends"), (578080, "PUBG"), (304930, "Unturned") };
                double total = 0;
                foreach (var (appId, name) in allInventories)
                {
                    await progress(0, $"Buscando inventário {name}...");
                    var (invTotal, items) = await _steam.CalculateInventoryValueAsync(steamId, appId, name, progress);
                    total += invTotal;
                    if (items.Count > 0)
                    {
                        await Clients.Caller.SendAsync("ReceiveInventoryData", name, items.Select(it => new
                        { name = it.Name, price = it.Price, unitPrice = it.UnitPrice, count = it.Count, imageUrl = it.ImageUrl, type = it.Type, rarity = it.Rarity, appId }), invTotal);
                    }
                    await Task.Delay(300);
                }
                await Clients.Caller.SendAsync("ReceiveInventoryTotal", total);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Snapshots ────────────────────────────────────────────────
        public async Task GetSnapshots(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var snaps = _steam.GetAccountSnapshots(steamId);
                await Clients.Caller.SendAsync("ReceiveSnapshots", snaps.Select(s => new { time = s.time.ToString("dd/MM HH:mm"), total = s.total }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Profile Totals (sidebar) ─────────────────────────────────
        public async Task GetProfileTotals(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var gamesTask = _steam.CalculateGamesFastAsync(steamId);
                var invTask = _steam.CalculateInventoryValueAsync(steamId, 730, "CS2");
                await Task.WhenAll(gamesTask, invTask);
                var (gamesCount, gamesValue) = await gamesTask;
                var (invValue, invItems) = await invTask;
                await Clients.Caller.SendAsync("ReceiveProfileTotalsDetailed", new { gamesCount, gamesValue, inventoryCount = invItems.Count, inventoryValue = invValue, total = gamesValue + invValue });
                await Clients.Caller.SendAsync("ReceiveProfileTotal", gamesValue + invValue);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Recent Games ─────────────────────────────────────────────
        public async Task GetRecentGames(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var recent = await _steam.GetRecentlyPlayedGamesAsync(steamId, 10);
                await Clients.Caller.SendAsync("ReceiveRecentGames", recent.Select(g => new { appId = g.AppId, name = g.Name, playtime2weeks = g.Playtime2WeeksMinutes, playtimeForever = g.PlaytimeMinutes, imageUrl = g.ImageUrl }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Player Bans ──────────────────────────────────────────────
        public async Task GetPlayerBans(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var bans = await _steam.GetPlayerBansAsync(steamId);
                await Clients.Caller.SendAsync("ReceivePlayerBans", bans);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Friend Details ───────────────────────────────────────────
        public async Task ComputeFriendDetails(string steamId)
        {
            try
            {
                var summaryTask = _steam.GetPlayerSummariesAsync(steamId);
                var levelTask = _steam.GetSteamLevelAsync(steamId);
                var bansTask = _steam.GetPlayerBansAsync(steamId);
                var recentTask = _steam.GetRecentlyPlayedGamesAsync(steamId, 3);
                var gamesTask = _steam.CalculateGamesFastAsync(steamId);

                await Task.WhenAll(summaryTask, levelTask, bansTask, recentTask, gamesTask);

                var summary = await summaryTask;
                var level = await levelTask;
                var bans = await bansTask;
                var recent = await recentTask;
                var (gamesCount, gamesValue) = await gamesTask;

                JsonElement player = default;
                if (summary != null)
                    player = summary.Value.GetProperty("response").GetProperty("players").EnumerateArray().FirstOrDefault();

                await Clients.Caller.SendAsync("ReceiveFriendDetails", new
                {
                    steamId,
                    name = player.ValueKind != JsonValueKind.Undefined ? (player.TryGetProperty("personaname", out var pn) ? pn.GetString() : "") : "",
                    avatar = player.ValueKind != JsonValueKind.Undefined ? (player.TryGetProperty("avatarfull", out var af) ? af.GetString() : "") : "",
                    level,
                    personastate = player.ValueKind != JsonValueKind.Undefined ? (player.TryGetProperty("personastate", out var ps) ? ps.GetInt32() : 0) : 0,
                    country = player.ValueKind != JsonValueKind.Undefined ? (player.TryGetProperty("loccountrycode", out var cc) ? cc.GetString() : "") : "",
                    gamesCount, gamesValue,
                    bans = bans != null ? new { bans.VacBanned, bans.NumberOfVacBans, bans.NumberOfGameBans } : null,
                    recentGames = recent.Select(g => new { appId = g.AppId, name = g.Name, playtime2weeks = g.Playtime2WeeksMinutes, imageUrl = g.ImageUrl })
                });
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }
    }
}
