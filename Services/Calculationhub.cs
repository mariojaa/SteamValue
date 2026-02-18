using Microsoft.AspNetCore.SignalR;
using System.Text.Json;

namespace SteamValue.Services
{
    public class CalculationHub : Hub
    {
        private readonly SteamService _steam;

        public CalculationHub(SteamService steam)
        {
            _steam = steam;
        }

        // ─────────────────────────────────────────────────────────────
        //  Cálculo principal (Index)
        // ─────────────────────────────────────────────────────────────
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

                // Buscar info do perfil em paralelo
                var summaryTask = _steam.GetPlayerSummariesAsync(steamId);
                var levelTask = _steam.GetSteamLevelAsync(steamId);
                var bansTask = _steam.GetPlayerBansAsync(steamId);
                var recentTask = _steam.GetRecentlyPlayedGamesAsync(steamId, 5);

                await Task.WhenAll(summaryTask, levelTask, bansTask, recentTask);

                var summary = await summaryTask;
                var level = await levelTask;
                var bans = await bansTask;
                var recentGames = await recentTask;

                // Enviar perfil detalhado ao cliente
                if (summary != null)
                {
                    var player = summary.Value
                        .GetProperty("response")
                        .GetProperty("players")
                        .EnumerateArray()
                        .FirstOrDefault();

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
                                bans.VacBanned,
                                bans.NumberOfVacBans,
                                bans.DaysSinceLastBan,
                                bans.NumberOfGameBans,
                                bans.CommunityBanned,
                                bans.EconomyBan
                            } : null,
                            recentGames = recentGames.Select(g => new
                            {
                                appId = g.AppId,
                                name = g.Name,
                                playtime2weeks = g.Playtime2WeeksMinutes,
                                playtimeForever = g.PlaytimeMinutes,
                                imageUrl = g.ImageUrl
                            })
                        });
                    }
                }

                // Jogos
                if (calculateGames)
                {
                    await progress(20, "Calculando valor dos jogos...");
                    var (gamesTotal, gamesList) = await _steam.CalculateGamesValueAsync(steamId, progress);
                    totalValue += gamesTotal;

                    var gamesData = gamesList.Select(g => new
                    {
                        name = g.Name,
                        price = g.Price,
                        imageUrl = g.ImageUrl,
                        appId = g.AppId,
                        playtimeMinutes = g.PlaytimeMinutes,
                        playtime2weeks = g.Playtime2WeeksMinutes
                    }).ToList();

                    await Clients.Caller.SendAsync("ReceiveGamesData", gamesData, gamesTotal);
                }

                // Inventários
                if (calculateInventory)
                {
                    var inventories = new[]
                    {
                        (730, "CS2"),
                        (570, "Dota 2"),
                        (440, "TF2"),
                        (252490, "Rust"),
                        (1172470, "Apex Legends"),
                    };

                    int startPct = 50;
                    int step = 8;

                    foreach (var (appId, gameName) in inventories)
                    {
                        await progress(startPct, $"Analisando inventário {gameName}...");
                        var (invTotal, invList) = await _steam.CalculateInventoryValueAsync(
                            steamId, appId, gameName, progress);
                        totalValue += invTotal;

                        if (invList.Count > 0)
                        {
                            var invData = invList.Select(it => new
                            {
                                name = it.Name,
                                price = it.Price,
                                imageUrl = it.ImageUrl,
                                appId
                            }).ToList();
                            await Clients.Caller.SendAsync("ReceiveInventoryData", gameName, invData, invTotal);
                        }
                        startPct = Math.Min(startPct + step, 90);
                    }
                }

                // Wishlist (bônus)
                var wishlist = await _steam.GetWishlistAsync(steamId);
                if (wishlist.Count > 0)
                {
                    await Clients.Caller.SendAsync("ReceiveWishlist", wishlist.Take(20).Select(w => new
                    {
                        appId = w.AppId,
                        name = w.Name,
                        imageUrl = w.ImageUrl,
                        priority = w.Priority
                    }));
                }

                // Badges
                var badges = await _steam.GetBadgesAsync(steamId);
                if (badges.Count > 0)
                {
                    await Clients.Caller.SendAsync("ReceiveBadges", new
                    {
                        count = badges.Count,
                        totalXp = badges.Sum(b => b.Xp)
                    });
                }

                _steam.RecordAccountSnapshot(steamId, totalValue);
                await progress(95, "Finalizando...");
                await Clients.Caller.SendAsync("ReceiveTotalValue", totalValue);
                await progress(100, "Concluído!");
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Amigos — buscar lista com status online/offline
        // ─────────────────────────────────────────────────────────────
        public async Task GetFriends(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var friendList = await _steam.GetFriendListAsync(steamId);

                if (!friendList.Any())
                {
                    await Clients.Caller.SendAsync("ReceiveFriends", new List<object>());
                    return;
                }

                var friendIds = friendList.Select(f => f.steamId).ToList();

                // Buscar summaries em batch (chunks de 100)
                var summaryJson = await _steam.GetPlayerSummariesAsync(string.Join(",", friendIds));
                if (summaryJson == null)
                {
                    await Clients.Caller.SendAsync("ReceiveFriends", new List<object>());
                    return;
                }

                var friendSinceLookup = friendList.ToDictionary(f => f.steamId, f => f.friendSince);

                var friends = summaryJson.Value
                    .GetProperty("response")
                    .GetProperty("players")
                    .EnumerateArray()
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
                                ? af.GetString()
                                : (p.TryGetProperty("avatarmedium", out var am) ? am.GetString() : ""),
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
                    // Online primeiro, depois offline por último online
                    .OrderByDescending(f => f.isOnline)
                    .ThenByDescending(f => f.lastLogoff)
                    .ToList();

                await Clients.Caller.SendAsync("ReceiveFriends", friends);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Totais de amigo — rápido e paralelo
        // ─────────────────────────────────────────────────────────────
        public async Task ComputeTotalsForSteamId(string steamId, bool calculateGames, bool calculateInventory)
        {
            Func<int, string, Task> progress = async (p, m) =>
            {
                try { await Clients.Caller.SendAsync("UpdateFriendProgress", steamId, p, m); } catch { }
            };

            try
            {
                int gamesCount = 0;
                double gamesValue = 0;
                int inventoryCount = 0;
                double inventoryValue = 0;

                var tasks = new List<Task>();

                if (calculateGames)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        var (count, total) = await _steam.CalculateGamesFastAsync(steamId);
                        gamesCount = count;
                        gamesValue = total;
                    }));
                }

                if (calculateInventory)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        var (total, items) = await _steam.CalculateInventoryValueAsync(steamId, 730, "CS2", progress);
                        inventoryCount = items.Count;
                        inventoryValue = total;
                    }));
                }

                await Task.WhenAll(tasks);

                var totalValue = gamesValue + inventoryValue;

                await Clients.Caller.SendAsync("ReceiveFriendTotalsDetailed", steamId, new
                {
                    gamesCount,
                    gamesValue,
                    inventoryCount,
                    inventoryValue,
                    total = totalValue
                });

                await Clients.Caller.SendAsync("ReceiveFriendTotal", steamId, totalValue);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Jogos de um amigo
        // ─────────────────────────────────────────────────────────────
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
                        var (price, image) = await _steam.GetAppDetailsAsync(g.AppId);
                        return new
                        {
                            appId = g.AppId,
                            name = g.Name,
                            playtimeMinutes = g.PlaytimeMinutes,
                            playtime2weeks = g.Playtime2WeeksMinutes,
                            price,
                            imageUrl = string.IsNullOrEmpty(image)
                                ? $"https://cdn.akamai.steamstatic.com/steam/apps/{g.AppId}/header.jpg"
                                : image
                        };
                    }
                    finally { sem.Release(); }
                }).ToList();

                var results = await Task.WhenAll(tasks);
                await Clients.Caller.SendAsync("ReceiveFriendGames", steamId, results
                    .OrderByDescending(g => g.price).ToList());
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Inventário de um amigo
        // ─────────────────────────────────────────────────────────────
        public async Task GetFriendInventory(string steamId, int appId)
        {
            try
            {
                var (total, items) = await _steam.CalculateInventoryValueAsync(steamId, appId, "");
                var list = items.Select(it => new
                {
                    name = it.Name,
                    price = it.Price,
                    imageUrl = it.ImageUrl
                }).OrderByDescending(it => it.price).ToList();

                await Clients.Caller.SendAsync("ReceiveFriendInventory", steamId, appId, list, total);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Resumo do perfil (usado no Index e Friends)
        // ─────────────────────────────────────────────────────────────
        public async Task GetProfileSummary(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);

                var summaryTask = _steam.GetPlayerSummariesAsync(steamId);
                var levelTask = _steam.GetSteamLevelAsync(steamId);
                var bansTask = _steam.GetPlayerBansAsync(steamId);
                var recentTask = _steam.GetRecentlyPlayedGamesAsync(steamId, 5);

                await Task.WhenAll(summaryTask, levelTask, bansTask, recentTask);

                var summary = await summaryTask;
                var level = await levelTask;
                var bans = await bansTask;
                var recent = await recentTask;

                await Clients.Caller.SendAsync("ReceiveProfileSummary", new
                {
                    summary,
                    level,
                    bans,
                    recentGames = recent.Select(g => new
                    {
                        appId = g.AppId,
                        name = g.Name,
                        playtime2weeks = g.Playtime2WeeksMinutes,
                        imageUrl = g.ImageUrl
                    })
                });
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Totais rápidos do perfil (para painel lateral do Friends)
        // ─────────────────────────────────────────────────────────────
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

                await Clients.Caller.SendAsync("ReceiveProfileTotalsDetailed", new
                {
                    gamesCount,
                    gamesValue,
                    inventoryCount = invItems.Count,
                    inventoryValue = invValue,
                    total = gamesValue + invValue
                });

                await Clients.Caller.SendAsync("ReceiveProfileTotal", gamesValue + invValue);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Snapshots históricos
        // ─────────────────────────────────────────────────────────────
        public async Task GetSnapshots(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var snaps = _steam.GetAccountSnapshots(steamId);
                await Clients.Caller.SendAsync("ReceiveSnapshots", snaps.Select(s => new
                {
                    time = s.time.ToString("dd/MM/yyyy HH:mm"),
                    total = s.total
                }));
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Conquistas
        // ─────────────────────────────────────────────────────────────
        public async Task GetAchievements(string profileUrl, int appId)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var (total, unlocked, pct) = await _steam.GetPlayerAchievementsAsync(steamId, appId);
                await Clients.Caller.SendAsync("ReceiveAchievements", appId, total, unlocked, pct);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Wishlist
        // ─────────────────────────────────────────────────────────────
        public async Task GetWishlist(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var wishlist = await _steam.GetWishlistAsync(steamId);
                await Clients.Caller.SendAsync("ReceiveWishlist", wishlist.Take(30).Select(w => new
                {
                    appId = w.AppId,
                    name = w.Name,
                    imageUrl = w.ImageUrl,
                    priority = w.Priority
                }));
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Badges e nível
        // ─────────────────────────────────────────────────────────────
        public async Task GetBadgesAndLevel(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var levelTask = _steam.GetSteamLevelAsync(steamId);
                var badgesTask = _steam.GetBadgesAsync(steamId);
                await Task.WhenAll(levelTask, badgesTask);
                var level = await levelTask;
                var badges = await badgesTask;
                await Clients.Caller.SendAsync("ReceiveBadgesAndLevel", new
                {
                    level,
                    badgeCount = badges.Count,
                    totalXp = badges.Sum(b => b.Xp),
                    badges = badges.Take(12).Select(b => new { b.BadgeId, b.Level, b.Xp, b.AppId })
                });
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Jogos recentes
        // ─────────────────────────────────────────────────────────────
        public async Task GetRecentGames(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var recent = await _steam.GetRecentlyPlayedGamesAsync(steamId, 10);
                await Clients.Caller.SendAsync("ReceiveRecentGames", recent.Select(g => new
                {
                    appId = g.AppId,
                    name = g.Name,
                    playtime2weeks = g.Playtime2WeeksMinutes,
                    playtimeForever = g.PlaytimeMinutes,
                    imageUrl = g.ImageUrl
                }));
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Bans
        // ─────────────────────────────────────────────────────────────
        public async Task GetPlayerBans(string profileUrl)
        {
            try
            {
                var steamId = await _steam.ResolveSteamIdAsync(profileUrl);
                var bans = await _steam.GetPlayerBansAsync(steamId);
                await Clients.Caller.SendAsync("ReceivePlayerBans", bans);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────────
        //  Detalhes completos de um amigo
        // ─────────────────────────────────────────────────────────────
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
                {
                    player = summary.Value.GetProperty("response").GetProperty("players")
                        .EnumerateArray().FirstOrDefault();
                }

                await Clients.Caller.SendAsync("ReceiveFriendDetails", new
                {
                    steamId,
                    name = player.ValueKind != JsonValueKind.Undefined
                        ? player.TryGetProperty("personaname", out var pn) ? pn.GetString() : ""
                        : "",
                    avatar = player.ValueKind != JsonValueKind.Undefined
                        ? (player.TryGetProperty("avatarfull", out var af) ? af.GetString() : "")
                        : "",
                    level,
                    personastate = player.ValueKind != JsonValueKind.Undefined
                        ? (player.TryGetProperty("personastate", out var ps) ? ps.GetInt32() : 0)
                        : 0,
                    country = player.ValueKind != JsonValueKind.Undefined
                        ? (player.TryGetProperty("loccountrycode", out var cc) ? cc.GetString() : "")
                        : "",
                    gamesCount,
                    gamesValue,
                    bans = bans != null ? new
                    {
                        bans.VacBanned,
                        bans.NumberOfVacBans,
                        bans.NumberOfGameBans
                    } : null,
                    recentGames = recent.Select(g => new
                    {
                        appId = g.AppId,
                        name = g.Name,
                        playtime2weeks = g.Playtime2WeeksMinutes,
                        imageUrl = g.ImageUrl
                    })
                });
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }
    }
}
