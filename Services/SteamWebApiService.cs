using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using SteamValue.Configuration;
using SteamValue.Models.Steam;
using SteamValue.Extensions;
using static SteamValue.Configuration.SteamApiEndpoints;

namespace SteamValue.Services
{
    /// <summary>
    /// Service for Steam Web API operations
    /// Implements endpoints from https://steamcommunity.com/dev
    /// </summary>
    public class SteamWebApiService
    {
        private readonly SteamHttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly SteamApiConfig _config;
        private readonly ILogger<SteamWebApiService> _logger;

        public SteamWebApiService(
            SteamHttpClient httpClient,
            IMemoryCache cache,
            IOptions<SteamApiConfig> config,
            ILogger<SteamWebApiService> logger)
        {
            _httpClient = httpClient;
            _cache = cache;
            _config = config.Value;
            _logger = logger;
        }

        // ???????????????????????????????????????????????????????????????????????
        // ISteamUser Interface
        // ???????????????????????????????????????????????????????????????????????

        /// <summary>
        /// Resolves vanity URL to Steam ID
        /// API: ISteamUser/ResolveVanityURL/v1
        /// </summary>
        public async Task<string?> ResolveVanityUrlAsync(string vanityUrl, CancellationToken cancellationToken = default)
        {
            var cacheKey = $"vanity:{vanityUrl}";
            if (_cache.TryGetValue(cacheKey, out string? cached))
                return cached;

            var url = $"{ResolveVanityUrl}?key={_config.ApiKey}&vanityurl={vanityUrl}";
            
            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to resolve vanity URL {VanityUrl}: {StatusCode}", 
                        vanityUrl, response.StatusCode);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                if (!doc.TryGetProperty("response", out var resp))
                    return null;

                if (resp.IsSuccess() && resp.TryGetProperty("steamid", out var steamId))
                {
                    var id = steamId.GetString();
                    _cache.Set(cacheKey, id, TimeSpan.FromHours(24)); // Vanity URLs rarely change
                    return id;
                }

                _logger.LogWarning("Vanity URL {VanityUrl} not found or failed", vanityUrl);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving vanity URL {VanityUrl}", vanityUrl);
                return null;
            }
        }

        /// <summary>
        /// Gets player summaries (profile information)
        /// API: ISteamUser/GetPlayerSummaries/v2
        /// Maximum 100 Steam IDs per request
        /// </summary>
        public async Task<List<PlayerSummary>> GetPlayerSummariesAsync(
            IEnumerable<string> steamIds,
            CancellationToken cancellationToken = default)
        {
            var idList = steamIds.ToList();
            if (!idList.Any()) return new List<PlayerSummary>();

            var allPlayers = new List<PlayerSummary>();

            // Process in chunks of 100 (API limit)
            foreach (var chunk in Helpers.SteamHelpers.ChunkList(idList, 100))
            {
                var chunkKey = $"summaries:{string.Join(",", chunk.OrderBy(x => x))}";
                
                if (_cache.TryGetValue(chunkKey, out List<PlayerSummary>? cached) && cached != null)
                {
                    allPlayers.AddRange(cached);
                    continue;
                }

                var url = $"{GetPlayerSummaries}?key={_config.ApiKey}&steamids={string.Join(",", chunk)}";

                try
                {
                    var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to get player summaries: {StatusCode}", response.StatusCode);
                        continue;
                    }

                    var json = await response.Content.ReadAsStringAsync(cancellationToken);
                    var doc = JsonSerializer.Deserialize<JsonElement>(json);

                    if (!doc.TryGetProperty("response", out var resp) ||
                        !resp.TryGetProperty("players", out var players))
                        continue;

                    var chunkPlayers = new List<PlayerSummary>();

                    foreach (var player in players.EnumerateArray())
                    {
                        var summary = new PlayerSummary
                        {
                            SteamId = player.GetStringOrEmpty("steamid"),
                            PersonaName = player.GetStringOrEmpty("personaname"),
                            ProfileUrl = player.GetStringOrEmpty("profileurl"),
                            Avatar = player.GetStringOrEmpty("avatar"),
                            AvatarMedium = player.GetStringOrEmpty("avatarmedium"),
                            AvatarFull = player.GetStringOrEmpty("avatarfull"),
                            PersonaState = player.GetInt32OrDefault("personastate"),
                            CommunityVisibilityState = player.GetInt32OrDefault("communityvisibilitystate"),
                            ProfileState = player.GetInt32OrDefault("profilestate"),
                            LastLogoff = player.GetInt64OrDefault("lastlogoff"),
                            TimeCreated = player.GetInt64OrDefault("timecreated"),
                            RealName = player.GetStringOrEmpty("realname"),
                            PrimaryClanId = player.GetStringOrEmpty("primaryclanid"),
                            LocCountryCode = player.GetStringOrEmpty("loccountrycode"),
                            LocStateCode = player.GetStringOrEmpty("locstatecode"),
                            GameId = player.GetStringOrEmpty("gameid"),
                            GameExtraInfo = player.GetStringOrEmpty("gameextrainfo"),
                            CommentPermission = player.GetInt32OrDefault("commentpermission")
                        };

                        chunkPlayers.Add(summary);
                    }

                    _cache.Set(chunkKey, chunkPlayers, TimeSpan.FromMinutes(_config.Cache.PlayerSummaryMinutes));
                    allPlayers.AddRange(chunkPlayers);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting player summaries for chunk");
                }
            }

            return allPlayers;
        }

        /// <summary>
        /// Gets single player summary
        /// </summary>
        public async Task<PlayerSummary?> GetPlayerSummaryAsync(
            string steamId,
            CancellationToken cancellationToken = default)
        {
            var summaries = await GetPlayerSummariesAsync(new[] { steamId }, cancellationToken);
            return summaries.FirstOrDefault();
        }

        /// <summary>
        /// Gets friend list for a user
        /// API: ISteamUser/GetFriendList/v1
        /// </summary>
        public async Task<List<SteamFriend>> GetFriendListAsync(
            string steamId,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"friends:{steamId}";
            if (_cache.TryGetValue(cacheKey, out List<SteamFriend>? cached) && cached != null)
                return cached;

            var url = $"{GetFriendList}?key={_config.ApiKey}&steamid={steamId}&relationship=friend";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to get friend list for {SteamId}: {StatusCode}",
                        steamId, response.StatusCode);
                    return new List<SteamFriend>();
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var friends = new List<SteamFriend>();

                if (doc.TryGetProperty("friendslist", out var friendsList) &&
                    friendsList.TryGetProperty("friends", out var friendsArray))
                {
                    foreach (var friend in friendsArray.EnumerateArray())
                    {
                        friends.Add(new SteamFriend
                        {
                            SteamId = friend.GetStringOrEmpty("steamid"),
                            Relationship = friend.GetStringOrEmpty("relationship"),
                            FriendSince = friend.GetInt64OrDefault("friend_since")
                        });
                    }
                }

                _cache.Set(cacheKey, friends, TimeSpan.FromMinutes(_config.Cache.FriendListMinutes));
                return friends;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting friend list for {SteamId}", steamId);
                return new List<SteamFriend>();
            }
        }

        /// <summary>
        /// Gets player ban status
        /// API: ISteamUser/GetPlayerBans/v1
        /// </summary>
        public async Task<PlayerBanStatus?> GetPlayerBansAsync(
            string steamId,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"bans:{steamId}";
            if (_cache.TryGetValue(cacheKey, out PlayerBanStatus? cached))
                return cached;

            var url = $"{GetPlayerBans}?key={_config.ApiKey}&steamids={steamId}";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                if (!doc.TryGetProperty("players", out var players))
                    return null;

                var player = players.EnumerateArray().FirstOrDefault();
                if (player.ValueKind == JsonValueKind.Undefined)
                    return null;

                var banStatus = new PlayerBanStatus
                {
                    SteamId = player.GetStringOrEmpty("SteamId"),
                    CommunityBanned = player.GetBoolOrDefault("CommunityBanned"),
                    VACBanned = player.GetBoolOrDefault("VACBanned"),
                    NumberOfVACBans = player.GetInt32OrDefault("NumberOfVACBans"),
                    DaysSinceLastBan = player.GetInt32OrDefault("DaysSinceLastBan"),
                    NumberOfGameBans = player.GetInt32OrDefault("NumberOfGameBans"),
                    EconomyBan = player.GetStringOrEmpty("EconomyBan")
                };

                _cache.Set(cacheKey, banStatus, TimeSpan.FromHours(_config.Cache.BadgesHours));
                return banStatus;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting bans for {SteamId}", steamId);
                return null;
            }
        }

        /// <summary>
        /// Gets user's group list
        /// API: ISteamUser/GetUserGroupList/v1
        /// </summary>
        public async Task<List<UserGroup>> GetUserGroupListAsync(
            string steamId,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"groups:{steamId}";
            if (_cache.TryGetValue(cacheKey, out List<UserGroup>? cached) && cached != null)
                return cached;

            var url = $"{GetUserGroupList}?key={_config.ApiKey}&steamid={steamId}";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return new List<UserGroup>();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var groups = new List<UserGroup>();

                if (doc.TryGetProperty("response", out var resp) &&
                    resp.TryGetProperty("groups", out var groupsArray))
                {
                    foreach (var group in groupsArray.EnumerateArray())
                    {
                        groups.Add(new UserGroup
                        {
                            Gid = group.GetStringOrEmpty("gid")
                        });
                    }
                }

                _cache.Set(cacheKey, groups, TimeSpan.FromHours(1));
                return groups;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting groups for {SteamId}", steamId);
                return new List<UserGroup>();
            }
        }

        // ???????????????????????????????????????????????????????????????????????
        // IPlayerService Interface
        // ???????????????????????????????????????????????????????????????????????

        /// <summary>
        /// Gets owned games for a user
        /// API: IPlayerService/GetOwnedGames/v1
        /// </summary>
        public async Task<List<OwnedGame>> GetOwnedGamesAsync(
            string steamId,
            bool includeAppInfo = true,
            bool includePlayedFreeGames = true,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"owned:{steamId}:{includeAppInfo}:{includePlayedFreeGames}";
            if (_cache.TryGetValue(cacheKey, out List<OwnedGame>? cached) && cached != null)
                return cached;

            var url = $"{GetOwnedGames}?key={_config.ApiKey}&steamid={steamId}" +
                      $"&include_appinfo={includeAppInfo.ToString().ToLower()}" +
                      $"&include_played_free_games={includePlayedFreeGames.ToString().ToLower()}" +
                      "&include_free_sub=true";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Failed to get owned games for {SteamId}: {StatusCode}",
                        steamId, response.StatusCode);
                    return new List<OwnedGame>();
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var games = new List<OwnedGame>();

                if (doc.TryGetProperty("response", out var resp) &&
                    resp.TryGetProperty("games", out var gamesArray))
                {
                    foreach (var game in gamesArray.EnumerateArray())
                    {
                        games.Add(new OwnedGame
                        {
                            AppId = game.GetInt32OrDefault("appid"),
                            Name = game.GetStringOrEmpty("name"),
                            PlaytimeForever = game.GetInt32OrDefault("playtime_forever"),
                            Playtime2Weeks = game.GetInt32OrDefault("playtime_2weeks"),
                            ImgIconUrl = game.GetStringOrEmpty("img_icon_url"),
                            ImgLogoUrl = game.GetStringOrEmpty("img_logo_url"),
                            HasCommunityVisibleStats = game.GetBoolOrDefault("has_community_visible_stats"),
                            PlaytimeWindowsForever = game.GetInt32OrDefault("playtime_windows_forever"),
                            PlaytimeMacForever = game.GetInt32OrDefault("playtime_mac_forever"),
                            PlaytimeLinuxForever = game.GetInt32OrDefault("playtime_linux_forever")
                        });
                    }
                }

                _cache.Set(cacheKey, games, TimeSpan.FromMinutes(_config.Cache.OwnedGamesMinutes));
                return games;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting owned games for {SteamId}", steamId);
                return new List<OwnedGame>();
            }
        }

        /// <summary>
        /// Gets recently played games
        /// API: IPlayerService/GetRecentlyPlayedGames/v1
        /// </summary>
        public async Task<List<RecentlyPlayedGame>> GetRecentlyPlayedGamesAsync(
            string steamId,
            int count = 10,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"recent:{steamId}:{count}";
            if (_cache.TryGetValue(cacheKey, out List<RecentlyPlayedGame>? cached) && cached != null)
                return cached;

            var url = $"{GetRecentlyPlayedGames}?key={_config.ApiKey}&steamid={steamId}&count={count}";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return new List<RecentlyPlayedGame>();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var games = new List<RecentlyPlayedGame>();

                if (doc.TryGetProperty("response", out var resp) &&
                    resp.TryGetProperty("games", out var gamesArray))
                {
                    foreach (var game in gamesArray.EnumerateArray())
                    {
                        games.Add(new RecentlyPlayedGame
                        {
                            AppId = game.GetInt32OrDefault("appid"),
                            Name = game.GetStringOrEmpty("name"),
                            Playtime2Weeks = game.GetInt32OrDefault("playtime_2weeks"),
                            PlaytimeForever = game.GetInt32OrDefault("playtime_forever"),
                            ImgIconUrl = game.GetStringOrEmpty("img_icon_url"),
                            ImgLogoUrl = game.GetStringOrEmpty("img_logo_url")
                        });
                    }
                }

                _cache.Set(cacheKey, games, TimeSpan.FromMinutes(10));
                return games;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting recent games for {SteamId}", steamId);
                return new List<RecentlyPlayedGame>();
            }
        }

        /// <summary>
        /// Gets Steam level for a user
        /// API: IPlayerService/GetSteamLevel/v1
        /// </summary>
        public async Task<int> GetSteamLevelAsync(
            string steamId,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"level:{steamId}";
            if (_cache.TryGetValue(cacheKey, out int cached))
                return cached;

            var url = $"{GetSteamLevel}?key={_config.ApiKey}&steamid={steamId}";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return 0;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var level = 0;
                if (doc.TryGetProperty("response", out var resp))
                    level = resp.GetInt32OrDefault("player_level");

                _cache.Set(cacheKey, level, TimeSpan.FromHours(_config.Cache.BadgesHours));
                return level;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting level for {SteamId}", steamId);
                return 0;
            }
        }

        /// <summary>
        /// Gets badges for a user
        /// API: IPlayerService/GetBadges/v1
        /// </summary>
        public async Task<List<PlayerBadge>> GetBadgesAsync(
            string steamId,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"badges:{steamId}";
            if (_cache.TryGetValue(cacheKey, out List<PlayerBadge>? cached) && cached != null)
                return cached;

            var url = $"{GetBadges}?key={_config.ApiKey}&steamid={steamId}";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return new List<PlayerBadge>();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var badges = new List<PlayerBadge>();

                if (doc.TryGetProperty("response", out var resp) &&
                    resp.TryGetProperty("badges", out var badgesArray))
                {
                    foreach (var badge in badgesArray.EnumerateArray())
                    {
                        badges.Add(new PlayerBadge
                        {
                            BadgeId = badge.GetInt32OrDefault("badgeid"),
                            Level = badge.GetInt32OrDefault("level"),
                            CompletionTime = badge.GetInt64OrDefault("completion_time"),
                            Xp = badge.GetInt32OrDefault("xp"),
                            Scarcity = badge.GetInt32OrDefault("scarcity"),
                            AppId = badge.TryGetProperty("appid", out var appId) && appId.ValueKind == JsonValueKind.Number
                                ? appId.GetInt32() : null,
                            CommunityItemId = badge.TryGetProperty("communityitemid", out var cid) && cid.ValueKind == JsonValueKind.Number
                                ? cid.GetInt32() : null,
                            BorderColor = badge.TryGetProperty("border_color", out var bc) && bc.ValueKind == JsonValueKind.Number
                                ? bc.GetInt32() : null
                        });
                    }
                }

                _cache.Set(cacheKey, badges, TimeSpan.FromHours(_config.Cache.BadgesHours));
                return badges;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting badges for {SteamId}", steamId);
                return new List<PlayerBadge>();
            }
        }

        // ???????????????????????????????????????????????????????????????????????
        // ISteamUserStats Interface
        // ???????????????????????????????????????????????????????????????????????

        /// <summary>
        /// Gets player achievements for a specific game
        /// API: ISteamUserStats/GetPlayerAchievements/v1
        /// </summary>
        public async Task<(bool success, int total, int unlocked, List<PlayerAchievement> achievements)> GetPlayerAchievementsAsync(
            string steamId,
            int appId,
            string language = "english",
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"ach:{steamId}:{appId}:{language}";
            if (_cache.TryGetValue(cacheKey, out (bool, int, int, List<PlayerAchievement>)? cached) && cached.HasValue)
                return cached.Value;

            var url = $"{GetPlayerAchievements}?key={_config.ApiKey}&steamid={steamId}&appid={appId}&l={language}";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return (false, 0, 0, new List<PlayerAchievement>());

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                if (!doc.TryGetProperty("playerstats", out var playerStats))
                    return (false, 0, 0, new List<PlayerAchievement>());

                var success = playerStats.GetBoolOrDefault("success", true);
                if (!success)
                    return (false, 0, 0, new List<PlayerAchievement>());

                var achievements = new List<PlayerAchievement>();
                var unlocked = 0;

                if (playerStats.TryGetProperty("achievements", out var achArray))
                {
                    foreach (var ach in achArray.EnumerateArray())
                    {
                        var achieved = ach.GetInt32OrDefault("achieved");
                        if (achieved == 1) unlocked++;

                        achievements.Add(new PlayerAchievement
                        {
                            ApiName = ach.GetStringOrEmpty("apiname"),
                            Achieved = achieved,
                            UnlockTime = ach.GetInt64OrDefault("unlocktime"),
                            Name = ach.GetStringOrEmpty("name"),
                            Description = ach.GetStringOrEmpty("description")
                        });
                    }
                }

                var result = (true, achievements.Count, unlocked, achievements);
                _cache.Set(cacheKey, result, TimeSpan.FromHours(1));
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting achievements for {SteamId} app {AppId}", steamId, appId);
                return (false, 0, 0, new List<PlayerAchievement>());
            }
        }

        /// <summary>
        /// Gets schema for a game (achievements, stats definitions)
        /// API: ISteamUserStats/GetSchemaForGame/v2
        /// </summary>
        public async Task<GameSchema?> GetSchemaForGameAsync(
            int appId,
            string language = "english",
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"schema:{appId}:{language}";
            if (_cache.TryGetValue(cacheKey, out GameSchema? cached))
                return cached;

            var url = $"{GetSchemaForGame}?key={_config.ApiKey}&appid={appId}&l={language}";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                if (!doc.TryGetProperty("game", out var game))
                    return null;

                var schema = new GameSchema
                {
                    GameName = game.GetStringOrEmpty("gameName"),
                    GameVersion = game.GetStringOrEmpty("gameVersion")
                };

                if (game.TryGetProperty("availableGameStats", out var stats))
                {
                    schema.AvailableGameStats = new AvailableGameStats();

                    // Parse achievements
                    if (stats.TryGetProperty("achievements", out var achArray))
                    {
                        schema.AvailableGameStats.Achievements = new List<AchievementSchema>();
                        foreach (var ach in achArray.EnumerateArray())
                        {
                            schema.AvailableGameStats.Achievements.Add(new AchievementSchema
                            {
                                Name = ach.GetStringOrEmpty("name"),
                                DefaultValue = ach.GetInt32OrDefault("defaultvalue"),
                                DisplayName = ach.GetStringOrEmpty("displayName"),
                                Description = ach.GetStringOrEmpty("description"),
                                Icon = ach.GetStringOrEmpty("icon"),
                                IconGray = ach.GetStringOrEmpty("icongray"),
                                Hidden = ach.GetBoolOrDefault("hidden")
                            });
                        }
                    }

                    // Parse stats
                    if (stats.TryGetProperty("stats", out var statsArray))
                    {
                        schema.AvailableGameStats.Stats = new List<StatSchema>();
                        foreach (var stat in statsArray.EnumerateArray())
                        {
                            schema.AvailableGameStats.Stats.Add(new StatSchema
                            {
                                Name = stat.GetStringOrEmpty("name"),
                                DefaultValue = stat.GetInt32OrDefault("defaultvalue"),
                                DisplayName = stat.GetStringOrEmpty("displayName")
                            });
                        }
                    }
                }

                _cache.Set(cacheKey, schema, TimeSpan.FromHours(12)); // Schema rarely changes
                return schema;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting schema for app {AppId}", appId);
                return null;
            }
        }

        /// <summary>
        /// Gets current number of players for an app
        /// API: ISteamUserStats/GetNumberOfCurrentPlayers/v1
        /// </summary>
        public async Task<int> GetNumberOfCurrentPlayersAsync(
            int appId,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"players:{appId}";
            if (_cache.TryGetValue(cacheKey, out int cached))
                return cached;

            var url = $"{GetNumberOfCurrentPlayers}?appid={appId}";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return 0;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var playerCount = 0;
                if (doc.TryGetProperty("response", out var resp))
                    playerCount = resp.GetInt32OrDefault("player_count");

                _cache.Set(cacheKey, playerCount, TimeSpan.FromMinutes(_config.Cache.LivePlayerCountMinutes));
                return playerCount;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting player count for app {AppId}", appId);
                return 0;
            }
        }

        /// <summary>
        /// Gets user stats for a game
        /// API: ISteamUserStats/GetUserStatsForGame/v2
        /// </summary>
        public async Task<Dictionary<string, double>> GetUserStatsForGameAsync(
            string steamId,
            int appId,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"userstats:{steamId}:{appId}";
            if (_cache.TryGetValue(cacheKey, out Dictionary<string, double>? cached) && cached != null)
                return cached;

            var url = $"{GetUserStatsForGame}?key={_config.ApiKey}&steamid={steamId}&appid={appId}";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return new Dictionary<string, double>();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var stats = new Dictionary<string, double>();

                if (doc.TryGetProperty("playerstats", out var playerStats) &&
                    playerStats.TryGetProperty("stats", out var statsArray))
                {
                    foreach (var stat in statsArray.EnumerateArray())
                    {
                        var name = stat.GetStringOrEmpty("name");
                        var value = stat.GetDoubleOrDefault("value");
                        if (!string.IsNullOrEmpty(name))
                            stats[name] = value;
                    }
                }

                _cache.Set(cacheKey, stats, TimeSpan.FromHours(2));
                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user stats for {SteamId} app {AppId}", steamId, appId);
                return new Dictionary<string, double>();
            }
        }

        /// <summary>
        /// Gets global achievement percentages for an app
        /// API: ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2
        /// </summary>
        public async Task<Dictionary<string, double>> GetGlobalAchievementPercentagesAsync(
            int appId,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"globalachpct:{appId}";
            if (_cache.TryGetValue(cacheKey, out Dictionary<string, double>? cached) && cached != null)
                return cached;

            var url = $"{GetGlobalAchievementPercentagesForApp}?gameid={appId}";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return new Dictionary<string, double>();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var percentages = new Dictionary<string, double>();

                if (doc.TryGetProperty("achievementpercentages", out var achPercentages) &&
                    achPercentages.TryGetProperty("achievements", out var achArray))
                {
                    foreach (var ach in achArray.EnumerateArray())
                    {
                        var name = ach.GetStringOrEmpty("name");
                        var percent = ach.GetDoubleOrDefault("percent");
                        if (!string.IsNullOrEmpty(name))
                            percentages[name] = percent;
                    }
                }

                _cache.Set(cacheKey, percentages, TimeSpan.FromHours(24)); // Global stats change slowly
                return percentages;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting global achievement percentages for app {AppId}", appId);
                return new Dictionary<string, double>();
            }
        }

        // ???????????????????????????????????????????????????????????????????????
        // ISteamNews Interface
        // ???????????????????????????????????????????????????????????????????????

        /// <summary>
        /// Gets news for an app
        /// API: ISteamNews/GetNewsForApp/v2
        /// </summary>
        public async Task<List<NewsItem>> GetNewsForAppAsync(
            int appId,
            int count = 10,
            int maxLength = 300,
            CancellationToken cancellationToken = default)
        {
            var cacheKey = $"news:{appId}:{count}";
            if (_cache.TryGetValue(cacheKey, out List<NewsItem>? cached) && cached != null)
                return cached;

            var url = $"{GetNewsForApp}?appid={appId}&count={count}&maxlength={maxLength}&format=json";

            try
            {
                var response = await _httpClient.GetWebApiAsync(url, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    return new List<NewsItem>();

                var json = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<JsonElement>(json);

                var newsItems = new List<NewsItem>();

                if (doc.TryGetProperty("appnews", out var appNews) &&
                    appNews.TryGetProperty("newsitems", out var newsArray))
                {
                    foreach (var news in newsArray.EnumerateArray())
                    {
                        newsItems.Add(new NewsItem
                        {
                            Gid = news.GetStringOrEmpty("gid"),
                            Title = news.GetStringOrEmpty("title"),
                            Url = news.GetStringOrEmpty("url"),
                            IsExternalUrl = news.GetBoolOrDefault("is_external_url"),
                            Author = news.GetStringOrEmpty("author"),
                            Contents = news.GetStringOrEmpty("contents"),
                            FeedLabel = news.GetStringOrEmpty("feedlabel"),
                            Date = news.GetInt64OrDefault("date"),
                            FeedName = news.GetStringOrEmpty("feedname"),
                            FeedType = news.GetInt32OrDefault("feed_type"),
                            AppId = news.GetInt32OrDefault("appid")
                        });
                    }
                }

                _cache.Set(cacheKey, newsItems, TimeSpan.FromHours(_config.Cache.NewsHours));
                return newsItems;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting news for app {AppId}", appId);
                return new List<NewsItem>();
            }
        }
    }
}
