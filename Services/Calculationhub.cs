using Microsoft.AspNetCore.SignalR;
using System.Text.Json;

namespace SteamValue.Services
{
    public class CalculationHub : Hub
    {
        private readonly SteamService _steam;

        public CalculationHub(SteamService steam) { _steam = steam; }

        // ─── Helper: fire-and-forget progress that never throws ───────────────
        private Func<int, string, Task> MakeProgress(string progressEvent = "UpdateProgress")
            => async (p, m) => { try { await Clients.Caller.SendAsync(progressEvent, p, m); } catch { } };

        // ─── Main Calculation ─────────────────────────────────────────────────
        public async Task StartCalculation(string profileUrl, bool calculateGames, bool calculateInventory)
        {
            var progress = MakeProgress();
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl, progress);
                double totalValue = 0;

                // Load profile data in parallel — independent from games/inventory
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
                            bans = bans != null ? new
                            {
                                bans.VacBanned, bans.NumberOfVacBans, bans.DaysSinceLastBan,
                                bans.NumberOfGameBans, bans.CommunityBanned, bans.EconomyBan
                            } : null,
                            recentGames = recentGames.Select(g => new
                            {
                                appId = g.AppId, name = g.Name,
                                playtime2weeks = g.Playtime2WeeksMinutes,
                                playtimeForever = g.PlaytimeMinutes,
                                imageUrl = g.ImageUrl
                            })
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
                        name = g.Name,
                        price = g.Price,
                        imageUrl = g.ImageUrl,
                        appId = g.AppId,
                        playtimeMinutes = g.PlaytimeMinutes,
                        playtime2weeks = g.Playtime2WeeksMinutes,
                        genre = g.Genre,
                        developer = g.Developer,
                        metacritic = g.MetacriticScore,
                        communityScore = g.CommunityScore,
                        hoursPerDollar = double.IsInfinity(g.HoursPerDollar) ? -1 : g.HoursPerDollar
                    }), gamesTotal);
                }

                if (calculateInventory)
                {
                    var apps = new[] { (730, "CS2"), (570, "Dota 2"), (440, "TF2"), (252490, "Rust"), (1172470, "Apex Legends"), (578080, "PUBG"), (304930, "Unturned") };
                    await progress(50, "Buscando inventários...");

                    var inventoryResults = new List<(int appId, string appName, List<InventoryItem> items)>();
                    foreach (var app in apps)
                    {
                        try
                        {
                            var items = await _steam.GetInventoryQuickAsync(steamId, app.Item1);
                            inventoryResults.Add((app.Item1, app.Item2, items));
                        }
                        catch { inventoryResults.Add((app.Item1, app.Item2, new List<InventoryItem>())); }
                    }

                    foreach (var (appId, appName, items) in inventoryResults.Where(r => r.items.Count > 0))
                    {
                        await Clients.Caller.SendAsync("ReceiveInventoryData", appName, items.Select(it => new
                        {
                            name = it.Name, price = (double)-1, unitPrice = (double)-1,
                            count = it.Count, imageUrl = it.ImageUrl, type = it.Type, rarity = it.Rarity, appId
                        }), (double)0);
                    }

                    double invTotal = 0;
                    int priced = 0;
                    int totalItems = inventoryResults.Sum(r => r.items.Count);
                    foreach (var (appId, appName, items) in inventoryResults.Where(r => r.items.Count > 0))
                    {
                        double gameTotal = 0;
                        var toPrice = items.Take(80).ToList();
                        for (int i = 0; i < toPrice.Count; i++)
                        {
                            var item = toPrice[i];
                            var price = await _steam.GetMarketPriceAsync(item.Name, appId);
                            item.Price = price * item.Count;
                            item.UnitPrice = price;
                            gameTotal += item.Price;
                            priced++;
                            await Clients.Caller.SendAsync("ReceiveItemPriceUpdate", appName, appId, item.Name, price, item.Count);
                            if (priced % 3 == 0 || i == toPrice.Count - 1)
                            {
                                int pct = 60 + (priced * 35 / Math.Max(totalItems, 1));
                                await progress(Math.Min(pct, 95), $"[{appName}] {i + 1}/{toPrice.Count}: {item.Name[..Math.Min(28, item.Name.Length)]}");
                            }
                        }
                        invTotal += gameTotal;
                        await Clients.Caller.SendAsync("ReceiveInventoryGameTotal", appName, gameTotal);
                    }
                    totalValue += invTotal;
                }

                // These are parallel, won't block the main result
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var wishlist = await _steam.GetWishlistAsync(steamId);
                        if (wishlist.Count > 0)
                            await Clients.Caller.SendAsync("ReceiveWishlist",
                                wishlist.Take(20).Select(w => new { appId = w.AppId, name = w.Name, imageUrl = w.ImageUrl, priority = w.Priority }));
                    }
                    catch { }
                });

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var badges = await _steam.GetBadgesAsync(steamId);
                        if (badges.Count > 0)
                            await Clients.Caller.SendAsync("ReceiveBadges", new { count = badges.Count, totalXp = badges.Sum(b => b.Xp) });
                    }
                    catch { }
                });

                _ = Task.Run(async () =>
                {
                    try
                    {
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
                    }
                    catch { }
                });

                _steam.RecordAccountSnapshot(steamId, totalValue);
                await progress(95, "Finalizando...");
                await Clients.Caller.SendAsync("ReceiveTotalValue", totalValue);
                await progress(100, "Concluído!");
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Friends ─────────────────────────────────────────────────────────
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
                            personastate = state,
                            isOnline = state >= 1,
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

        // ─── Compute Totals for Friend ────────────────────────────────────────
        public async Task ComputeTotalsForSteamId(string steamId, bool calculateGames, bool calculateInventory)
        {
            var progress = MakeProgress("UpdateFriendProgress");
            try
            {
                int gamesCount = 0; double gamesValue = 0; int inventoryCount = 0; double inventoryValue = 0;
                if (calculateGames)
                {
                    var (c, t) = await _steam.CalculateGamesFastAsync(steamId);
                    gamesCount = c; gamesValue = t;
                }
                if (calculateInventory)
                {
                    var (invTotal, byApp) = await _steam.CalculateAllInventoriesParallelAsync(steamId, progress);
                    inventoryValue = invTotal;
                    inventoryCount = byApp.Values.Sum(v => v.Count);
                }
                await Clients.Caller.SendAsync("ReceiveFriendTotalsDetailed", steamId, new
                { gamesCount, gamesValue, inventoryCount, inventoryValue, total = gamesValue + inventoryValue });
                await Clients.Caller.SendAsync("ReceiveFriendTotal", steamId, gamesValue + inventoryValue);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Friend Details ───────────────────────────────────────────────────
        public async Task ComputeFriendDetails(string steamId)
        {
            try
            {
                var summaryTask = _steam.GetPlayerSummariesAsync(steamId);
                var levelTask = _steam.GetSteamLevelAsync(steamId);
                var bansTask = _steam.GetPlayerBansAsync(steamId);
                var recentTask = _steam.GetRecentlyPlayedGamesAsync(steamId, 5);
                var badgesTask = _steam.GetBadgesAsync(steamId);
                var gamesTask = _steam.CalculateGamesFastAsync(steamId);
                var wishlistTask = _steam.GetWishlistAsync(steamId);
                var groupsTask = _steam.GetUserGroupsAsync(steamId);

                await Task.WhenAll(summaryTask, levelTask, bansTask, recentTask, badgesTask, gamesTask, wishlistTask, groupsTask);

                var summary = await summaryTask;
                var level = await levelTask;
                var bans = await bansTask;
                var recent = await recentTask;
                var badges = await badgesTask;
                var (gamesCount, gamesValue) = await gamesTask;
                var wishlist = await wishlistTask;
                var groups = await groupsTask;

                JsonElement player = default;
                if (summary != null)
                    player = summary.Value.GetProperty("response").GetProperty("players").EnumerateArray().FirstOrDefault();

                string Get(JsonElement el, string key)
                    => el.ValueKind != JsonValueKind.Undefined && el.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";

                await Clients.Caller.SendAsync("ReceiveFriendDetails", new
                {
                    steamId,
                    name = Get(player, "personaname"),
                    avatar = Get(player, "avatarfull"),
                    level,
                    personastate = player.ValueKind != JsonValueKind.Undefined && player.TryGetProperty("personastate", out var ps) ? ps.GetInt32() : 0,
                    country = Get(player, "loccountrycode"),
                    lastLogoff = player.ValueKind != JsonValueKind.Undefined && player.TryGetProperty("lastlogoff", out var ll) ? ll.GetInt64() : 0L,
                    profileUrl = Get(player, "profileurl"),
                    created = player.ValueKind != JsonValueKind.Undefined && player.TryGetProperty("timecreated", out var tc) ? tc.GetInt64() : 0L,
                    realName = Get(player, "realname"),
                    gamesCount,
                    gamesValue,
                    badgeCount = badges.Count,
                    totalXp = badges.Sum(b => b.Xp),
                    wishlistCount = wishlist.Count,
                    groupCount = groups.Count,
                    bans = bans != null ? new
                    {
                        bans.VacBanned, bans.NumberOfVacBans, bans.NumberOfGameBans,
                        bans.CommunityBanned, bans.EconomyBan, bans.DaysSinceLastBan
                    } : null,
                    recentGames = recent.Select(g => new
                    {
                        appId = g.AppId,
                        name = g.Name,
                        playtime2weeks = g.Playtime2WeeksMinutes,
                        playtimeForever = g.PlaytimeMinutes,
                        imageUrl = g.ImageUrl
                    })
                });
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Friend Games ─────────────────────────────────────────────────────
        public async Task GetFriendGames(string steamId)
        {
            try
            {
                var games = await _steam.GetOwnedGamesAsync(steamId);
                var sem = new SemaphoreSlim(8, 8);
                var tasks = games.Select(async g =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var (price, image, genre, dev, meta) = await _steam.GetAppDetailsAsync(g.AppId);
                        return new
                        {
                            appId = g.AppId,
                            name = g.Name,
                            playtimeMinutes = g.PlaytimeMinutes,
                            playtime2weeks = g.Playtime2WeeksMinutes,
                            price,
                            imageUrl = string.IsNullOrEmpty(image) ? $"https://cdn.akamai.steamstatic.com/steam/apps/{g.AppId}/header.jpg" : image,
                            genre,
                            developer = dev,
                            metacritic = meta
                        };
                    }
                    finally { sem.Release(); }
                }).ToList();
                var results = await Task.WhenAll(tasks);
                await Clients.Caller.SendAsync("ReceiveFriendGames", steamId, results.OrderByDescending(g => g.playtimeMinutes).ToList());
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Friend Inventory ─────────────────────────────────────────────────
        public async Task GetFriendInventory(string steamId, int appId)
        {
            try
            {
                var (total, items) = await _steam.CalculateInventoryValueAsync(steamId, appId, "");
                await Clients.Caller.SendAsync("ReceiveFriendInventory", steamId, appId,
                    items.Select(it => new
                    {
                        name = it.Name, price = it.Price, unitPrice = it.UnitPrice,
                        count = it.Count, imageUrl = it.ImageUrl, rarity = it.Rarity, type = it.Type
                    }).OrderByDescending(it => it.price).ToList(), total);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Profile Summary ──────────────────────────────────────────────────
        public async Task GetProfileSummary(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var summaryTask = _steam.GetPlayerSummariesAsync(steamId);
                var levelTask = _steam.GetSteamLevelAsync(steamId);
                var bansTask = _steam.GetPlayerBansAsync(steamId);
                var recentTask = _steam.GetRecentlyPlayedGamesAsync(steamId, 5);
                var badgesTask = _steam.GetBadgesAsync(steamId);
                var groupsTask = _steam.GetUserGroupsAsync(steamId);

                await Task.WhenAll(summaryTask, levelTask, bansTask, recentTask, badgesTask, groupsTask);

                var summary = await summaryTask;
                var level = await levelTask;
                var bans = await bansTask;
                var recent = await recentTask;
                var badges = await badgesTask;
                var groups = await groupsTask;

                if (summary == null) { await Clients.Caller.SendAsync("ReceiveError", "Perfil não encontrado."); return; }

                var player = summary.Value.GetProperty("response").GetProperty("players").EnumerateArray().FirstOrDefault();
                if (player.ValueKind == System.Text.Json.JsonValueKind.Undefined) { await Clients.Caller.SendAsync("ReceiveError", "Jogador não encontrado."); return; }

                // Send ReceiveProfileInfo — same event the main calculation uses
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
                    bans = bans != null ? new
                    {
                        bans.VacBanned, bans.NumberOfVacBans, bans.DaysSinceLastBan,
                        bans.NumberOfGameBans, bans.CommunityBanned, bans.EconomyBan
                    } : null,
                    badgeCount = badges.Count,
                    totalXp = badges.Sum(b => b.Xp),
                    groupCount = groups.Count,
                    recentGames = recent.Select(g => new
                    {
                        appId = g.AppId, name = g.Name,
                        playtime2weeks = g.Playtime2WeeksMinutes,
                        playtimeForever = g.PlaytimeMinutes,
                        imageUrl = g.ImageUrl
                    })
                });
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Wishlist ─────────────────────────────────────────────────────────
        public async Task GetWishlist(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var wishlist = await _steam.GetWishlistAsync(steamId);
                await Clients.Caller.SendAsync("ReceiveWishlist",
                    wishlist.Take(30).Select(w => new { appId = w.AppId, name = w.Name, imageUrl = w.ImageUrl, priority = w.Priority, added = w.Added }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Badges & Level ───────────────────────────────────────────────────
        public async Task GetBadgesAndLevel(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var level = await _steam.GetSteamLevelAsync(steamId);
                var badges = await _steam.GetBadgesAsync(steamId);
                await Clients.Caller.SendAsync("ReceiveBadgesAndLevel", new
                {
                    level,
                    badgeCount = badges.Count,
                    totalXp = badges.Sum(b => b.Xp),
                    badges = badges.Take(20).Select(b => new
                    {
                        b.BadgeId, b.Level, b.Xp, b.AppId,
                        imageUrl = b.AppId > 0
                            ? $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{b.AppId}/capsule_sm_120.jpg"
                            : $"https://community.cloudflare.steamstatic.com/public/images/badges/13_tf2/{b.BadgeId}_80px.png"
                    })
                });
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Achievements (single game) ────────────────────────────────────────
        public async Task GetAchievements(string profileUrl, int appId)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var (total, unlocked, pct, list) = await _steam.GetPlayerAchievementsWithIconsAsync(steamId, appId);
                await Clients.Caller.SendAsync("ReceiveAchievements", appId, total, unlocked, pct,
                    list.Take(200).Select(a => new { a.ApiName, a.Name, a.Description, a.Achieved, a.UnlockTime, a.IconUrl }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── All Achievements (all games) ─────────────────────────────────────
        public async Task GetAllAchievements(string profileUrl)
        {
            var progress = MakeProgress();
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                await progress(5, "Buscando biblioteca de jogos...");
                var allGames = await _steam.GetAllGamesAchievementsAsync(steamId, progress);
                await progress(95, $"Enviando {allGames.Count} jogos com conquistas...");

                foreach (var (appId, appName, appIcon, total, unlocked, pct, achievements) in allGames)
                    await Clients.Caller.SendAsync("ReceiveGameAchievements", new
                    {
                        appId, appName, appIcon, total, unlocked,
                        percent = Math.Round(pct, 1),
                        achievements = achievements.Take(500).Select(a => new
                        { a.ApiName, a.Name, a.Description, a.Achieved, a.UnlockTime, a.IconUrl })
                    });

                int totalUnlocked = allGames.Sum(g => g.unlocked);
                int totalAch = allGames.Sum(g => g.total);
                await Clients.Caller.SendAsync("ReceiveAllAchievementsDone", new
                {
                    gamesWithAchievements = allGames.Count,
                    totalAchievements = totalAch,
                    totalUnlocked,
                    overallPercent = totalAch > 0 ? Math.Round((double)totalUnlocked / totalAch * 100.0, 1) : 0.0
                });
                await progress(100, "Conquistas carregadas!");
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Playtime Analytics ───────────────────────────────────────────────
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
                    mostPlayed = analytics.MostPlayedGames.Select(g => new { g.AppId, g.Name, hours = Math.Round(g.PlaytimeMinutes / 60.0, 1), g.ImageUrl }),
                    recentlyPlayed = analytics.RecentlyPlayed.Select(g => new { g.AppId, g.Name, hours2w = Math.Round(g.Playtime2WeeksMinutes / 60.0, 1), g.ImageUrl })
                });
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Profile Comparison ───────────────────────────────────────────────
        public async Task CompareProfiles(string profileUrl1, string profileUrl2)
        {
            try
            {
                var sid1Task = _steam.ResolveSteamIdAsync(profileUrl1);
                var sid2Task = _steam.ResolveSteamIdAsync(profileUrl2);
                await Task.WhenAll(sid1Task, sid2Task);
                var steamId1 = await sid1Task;
                var steamId2 = await sid2Task;

                var comparisonTask = _steam.CompareProfilesAsync(steamId1, steamId2);
                var summariesTask = _steam.GetPlayerSummariesAsync($"{steamId1},{steamId2}");
                await Task.WhenAll(comparisonTask, summariesTask);

                var comparison = await comparisonTask;
                var summaries = await summariesTask;
                var players = new List<JsonElement>();
                if (summaries.HasValue && summaries.Value.TryGetProperty("response", out var response)
                    && response.TryGetProperty("players", out var playersElement))
                    players = playersElement.EnumerateArray().ToList();

                var player1 = players.FirstOrDefault(p => p.TryGetProperty("steamid", out var id) && id.GetString() == steamId1);
                var player2 = players.FirstOrDefault(p => p.TryGetProperty("steamid", out var id) && id.GetString() == steamId2);

                await Clients.Caller.SendAsync("ReceiveProfileComparison", new
                {
                    name1 = player1.ValueKind != JsonValueKind.Undefined && player1.TryGetProperty("personaname", out var pn1) ? pn1.GetString() ?? "" : "",
                    name2 = player2.ValueKind != JsonValueKind.Undefined && player2.TryGetProperty("personaname", out var pn2) ? pn2.GetString() ?? "" : "",
                    avatar1 = player1.ValueKind != JsonValueKind.Undefined && player1.TryGetProperty("avatarfull", out var av1) ? av1.GetString() ?? "" : "",
                    avatar2 = player2.ValueKind != JsonValueKind.Undefined && player2.TryGetProperty("avatarfull", out var av2) ? av2.GetString() ?? "" : "",
                    gamesCount1 = comparison.GamesCount1, gamesCount2 = comparison.GamesCount2,
                    level1 = comparison.Level1, level2 = comparison.Level2,
                    badgeCount1 = comparison.BadgeCount1, badgeCount2 = comparison.BadgeCount2,
                    totalXp1 = comparison.TotalXp1, totalXp2 = comparison.TotalXp2,
                    totalHours1 = Math.Round(comparison.TotalHours1, 0), totalHours2 = Math.Round(comparison.TotalHours2, 0),
                    commonGamesCount = comparison.CommonGamesCount,
                    commonGames = comparison.CommonGames.Take(12),
                    exclusive1 = comparison.ExclusiveGames1Count, exclusive2 = comparison.ExclusiveGames2Count
                });
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Live Player Counts ───────────────────────────────────────────────
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

        // ─── Market Item Price ────────────────────────────────────────────────
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

        // ─── Quick Inventory ─────────────────────────────────────────────────
        public async Task GetQuickInventory(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var apps = new[] { (730, "CS2"), (570, "Dota 2"), (440, "TF2"), (252490, "Rust"), (1172470, "Apex Legends"), (578080, "PUBG") };
                var tasks = apps.Select(async app =>
                {
                    try { return (appName: app.Item2, appId: app.Item1, items: await _steam.GetInventoryQuickAsync(steamId, app.Item1)); }
                    catch { return (appName: app.Item2, appId: app.Item1, items: new List<InventoryItem>()); }
                }).ToList();
                var results = await Task.WhenAll(tasks);
                foreach (var r in results.Where(r => r.items.Count > 0))
                    await Clients.Caller.SendAsync("ReceiveQuickInventory", r.appName, r.appId,
                        r.items.Select(it => new { name = it.Name, count = it.Count, imageUrl = it.ImageUrl, type = it.Type, rarity = it.Rarity }));
                await Clients.Caller.SendAsync("ReceiveQuickInventoryDone", results.Sum(r => r.items.Count));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Price Single Inventory Item ───────────────────────────────────────
        public async Task PriceInventoryItem(string marketHashName, int appId)
        {
            try
            {
                var price = await _steam.GetMarketPriceAsync(marketHashName, appId);
                await Clients.Caller.SendAsync("ReceiveItemPrice", marketHashName, appId, price);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Full Inventory ───────────────────────────────────────────────────
        public async Task GetFullInventory(string profileUrl)
        {
            var progress = MakeProgress();
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var apps = new[] { (730, "CS2"), (570, "Dota 2"), (440, "TF2"), (252490, "Rust"), (1172470, "Apex Legends"), (578080, "PUBG"), (304930, "Unturned") };

                await progress(5, "Buscando inventários de todos os jogos...");
                var fetchTasks = apps.Select(async app =>
                {
                    try { return (appId: app.Item1, appName: app.Item2, items: await _steam.GetInventoryQuickAsync(steamId, app.Item1)); }
                    catch { return (appId: app.Item1, appName: app.Item2, items: new List<InventoryItem>()); }
                }).ToList();

                var allInvs = await Task.WhenAll(fetchTasks);
                int totalItems = allInvs.Sum(r => r.items.Count);

                foreach (var (appId, appName, items) in allInvs.Where(r => r.items.Count > 0))
                    await Clients.Caller.SendAsync("ReceiveInventoryData", appName, items.Select(it => new
                    { name = it.Name, price = (double)-1, unitPrice = (double)-1, count = it.Count, imageUrl = it.ImageUrl, type = it.Type, rarity = it.Rarity, appId }), (double)0);

                await progress(15, $"✓ {totalItems} itens de {allInvs.Count(r => r.items.Count > 0)} jogos. Precificando...");

                double grandTotal = 0;
                int priced = 0;
                foreach (var (appId, appName, items) in allInvs.Where(r => r.items.Count > 0))
                {
                    double gameTotal = 0;
                    var toPrice = items.Take(80).ToList();
                    for (int i = 0; i < toPrice.Count; i++)
                    {
                        var item = toPrice[i];
                        var price = await _steam.GetMarketPriceAsync(item.Name, appId);
                        item.Price = price * item.Count; item.UnitPrice = price;
                        gameTotal += item.Price; priced++;
                        await Clients.Caller.SendAsync("ReceiveItemPriceUpdate", appName, appId, item.Name, price, item.Count);
                        if (priced % 3 == 0 || i == toPrice.Count - 1)
                        {
                            int pct = 15 + (priced * 80 / Math.Max(totalItems, 1));
                            await progress(Math.Min(pct, 95), $"[{appName}] {i + 1}/{toPrice.Count}: {item.Name[..Math.Min(28, item.Name.Length)]}");
                        }
                    }
                    grandTotal += gameTotal;
                    await Clients.Caller.SendAsync("ReceiveInventoryGameTotal", appName, gameTotal);
                }
                await Clients.Caller.SendAsync("ReceiveInventoryTotal", grandTotal);
                await progress(100, $"Concluído! {priced} itens precificados.");
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Single Game Inventory ────────────────────────────────────────────
        public async Task GetSingleGameInventory(string profileUrl, int appId)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var appName = GetAppName(appId);
                var items = await _steam.GetInventoryQuickAsync(steamId, appId);
                await Clients.Caller.SendAsync("ReceiveQuickInventory", appName, appId,
                    items.Select(it => new { name = it.Name, count = it.Count, imageUrl = it.ImageUrl, type = it.Type, rarity = it.Rarity }));
                await Clients.Caller.SendAsync("ReceiveQuickInventoryDone", items.Count);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Snapshots ────────────────────────────────────────────────────────
        public async Task GetSnapshots(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var snaps = _steam.GetAccountSnapshots(steamId);
                await Clients.Caller.SendAsync("ReceiveSnapshots",
                    snaps.Select(s => new { time = s.time.ToString("dd/MM HH:mm"), total = s.total }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Playtime ROI ─────────────────────────────────────────────────────
        public async Task GetPlaytimeROI(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var roi = await _steam.GetPlaytimeROIAsync(steamId);
                await Clients.Caller.SendAsync("ReceivePlaytimeROI", roi.Take(30).Select(r => new
                { appId = r.AppId, name = r.Name, price = r.Price, hours = r.Hours, costPerHour = r.CostPerHour, imageUrl = r.ImageUrl, genre = r.Genre }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Game Scout ───────────────────────────────────────────────────────
        public async Task GetGameScout(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var friends = await _steam.GetFriendListAsync(steamId);
                var friendIds = friends.Take(15).Select(f => f.steamId).ToArray();
                var scout = await _steam.GetGameScoutAsync(steamId, friendIds);
                await Clients.Caller.SendAsync("ReceiveGameScout", scout.Select(g => new
                { appId = g.AppId, name = g.Name, friendsWhoOwn = g.FriendsWhoOwn, avgFriendHours = g.AvgFriendHours, price = g.Price, imageUrl = g.ImageUrl, genre = g.Genre, metacritic = g.MetacriticScore }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Friend Leaderboard ───────────────────────────────────────────────
        public async Task GetFriendLeaderboard(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var friends = await _steam.GetFriendListAsync(steamId);
                var friendIds = friends.Take(15).Select(f => f.steamId).ToArray();
                var board = await _steam.GetFriendLeaderboardAsync(steamId, friendIds);
                await Clients.Caller.SendAsync("ReceiveFriendLeaderboard", board.Select(e => new
                { steamId = e.SteamId, name = e.Name, avatar = e.Avatar, level = e.Level, totalGames = e.TotalGames, totalHours = e.TotalHours, badgeCount = e.BadgeCount, totalXp = e.TotalXp, isMe = e.IsMe }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Trade Tracker ────────────────────────────────────────────────────
        public async Task GetTradeTracker(string profileUrl, int appId)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var tracker = await _steam.GetTradeTrackerAsync(steamId, appId);
                await Clients.Caller.SendAsync("ReceiveTradeTracker", tracker.Select(t => new
                { name = t.Name, currentPrice = t.CurrentPrice, minPrice = t.MinPrice, maxPrice = t.MaxPrice, avgPrice = t.AvgPrice, trend = t.Trend, priceHistory = t.PriceHistory, imageUrl = t.ImageUrl, count = t.Count }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Country Distribution ─────────────────────────────────────────────
        public async Task GetFriendCountries(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var countries = await _steam.GetFriendCountryDistributionAsync(steamId);
                await Clients.Caller.SendAsync("ReceiveFriendCountries",
                    countries.Select(c => new { code = c.Code, count = c.Count }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Gamer DNA ────────────────────────────────────────────────────────
        public async Task GetGamerDna(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var dna = await _steam.GetGamerDnaAsync(steamId);
                await Clients.Caller.SendAsync("ReceiveGamerDna", dna);
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Friend Activity Patterns ─────────────────────────────────────────
        public async Task GetFriendActivityPatterns(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var patterns = await _steam.GetFriendActivityPatternsAsync(steamId);
                await Clients.Caller.SendAsync("ReceiveFriendActivityPatterns", patterns.Select(p => new
                { steamId = p.SteamId, name = p.Name, avatar = p.Avatar, lastLogoffHour = p.LastLogoffHour, activitySlot = p.ActivitySlot, lastLogoffUnix = p.LastLogoffUnix, isOnline = p.IsOnline, playingGame = p.PlayingGame }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Wishlist Analysis ────────────────────────────────────────────────
        public async Task GetWishlistAnalysis(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var analysis = await _steam.GetWishlistAnalysisAsync(steamId);
                await Clients.Caller.SendAsync("ReceiveWishlistAnalysis", new
                {
                    totalItems = analysis.TotalItems, pricedItems = analysis.PricedItems,
                    totalFullPrice = analysis.TotalFullPrice, totalPriorityPrice = analysis.TotalPriorityPrice,
                    likelySaleItems = analysis.LikelySaleItems.Select(i => new
                    { appId = i.AppId, name = i.Name, imageUrl = i.ImageUrl, currentPrice = i.CurrentPrice, saleProbability = i.SaleProbability, genre = i.Genre, metacritic = i.MetacriticScore }),
                    items = analysis.Items.Take(30).Select(i => new
                    { appId = i.AppId, name = i.Name, imageUrl = i.ImageUrl, priority = i.Priority, currentPrice = i.CurrentPrice, saleProbability = i.SaleProbability, genre = i.Genre, developer = i.Developer, metacritic = i.MetacriticScore, added = i.Added })
                });
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ─── NEW: Free Games Today ────────────────────────────────────────────
        // ═══════════════════════════════════════════════════════════════════════
        public async Task GetFreeGamesToday()
        {
            try
            {
                var games = await _steam.GetFreeGamesTodayAsync();
                await Clients.Caller.SendAsync("ReceiveFreeGamesToday", games.Select(g => new
                {
                    appId = g.AppId, name = g.Name, imageUrl = g.ImageUrl,
                    originalPrice = g.OriginalPrice, finalPrice = g.FinalPrice,
                    discountPercent = g.DiscountPercent, isFreeToPlay = g.IsFreeToPlay,
                    endDate = g.EndDate, type = g.Type
                }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── NEW: Steam News Feed ─────────────────────────────────────────────
        public async Task GetGameNews(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var news = await _steam.GetRecentGameNewsAsync(steamId, 5);
                await Clients.Caller.SendAsync("ReceiveGameNews", news.Select(n => new
                {
                    appId = n.AppId, gameName = n.GameName, gameImage = n.GameImage,
                    title = n.Title, url = n.Url, author = n.Author,
                    contents = n.Contents, date = n.Date, feedName = n.FeedName
                }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── NEW: Backlog Analysis ────────────────────────────────────────────
        public async Task GetBacklogAnalysis(string profileUrl)
        {
            var progress = MakeProgress();
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                await progress(10, "Analisando backlog...");
                var analysis = await _steam.GetBacklogAnalysisAsync(steamId);
                await progress(95, "Enviando dados do backlog...");
                await Clients.Caller.SendAsync("ReceiveBacklogAnalysis", new
                {
                    steamId = analysis.SteamId,
                    totalUnplayed = analysis.TotalUnplayed,
                    totalAnalyzed = analysis.TotalAnalyzed,
                    backlogDebt = analysis.BacklogDebt,
                    averagePriceUnplayed = analysis.AveragePriceUnplayed,
                    topPriorityGames = analysis.TopPriorityGames.Select(g => new
                    {
                        appId = g.AppId, name = g.Name, imageUrl = g.ImageUrl,
                        price = g.Price, genre = g.Genre, metacritic = g.MetacriticScore,
                        developer = g.Developer, priorityScore = g.PriorityScore
                    }),
                    genreBreakdown = analysis.GenreBreakdown.Select(g => new { genre = g.Genre, count = g.Count })
                });
                await progress(100, "Backlog analisado!");
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── NEW: Top Rated Unowned Games ────────────────────────────────────
        // Method name matches JS: connection.invoke('GetTopRatedUnownedGames', url, maxPrice)
        public async Task GetTopRatedUnownedGames(string profileUrl, double maxPrice = 50)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var recs = await _steam.GetTopRatedUnownedGamesAsync(steamId, maxPrice);
                await Clients.Caller.SendAsync("ReceiveTopRatedUnowned", recs.Select(r => new
                { appId = r.AppId, name = r.Name, imageUrl = r.ImageUrl, price = r.Price, metacritic = r.MetacriticScore, reviewScore = r.ReviewScore, isFree = r.IsFree }));
            }
            catch (Exception ex) { await Clients.Caller.SendAsync("ReceiveError", ex.Message); }
        }

        // ─── Helper ───────────────────────────────────────────────────────────
        private string GetAppName(int appId) => appId switch
        {
            730 => "CS2", 570 => "Dota 2", 440 => "TF2", 252490 => "Rust",
            1172470 => "Apex Legends", 578080 => "PUBG", 304930 => "Unturned",
            _ => $"App {appId}"
        };
    }
}