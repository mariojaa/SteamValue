namespace SteamValue.Configuration
{
    /// <summary>
    /// Steam API configuration settings
    /// Based on Steam Web API documentation: https://steamcommunity.com/dev
    /// </summary>
    public class SteamApiConfig
    {
        public const string ConfigSection = "SteamApi";

        /// <summary>
        /// Steam Web API Key - Required for most API calls
        /// Get yours at: https://steamcommunity.com/dev/apikey
        /// </summary>
        public string ApiKey { get; set; } = "";

        /// <summary>
        /// Rate limiting configuration
        /// Steam API rate limits: ~100,000 calls per day per API key
        /// Market API: much stricter (1 request per 5 seconds recommended)
        /// </summary>
        public RateLimitConfig RateLimits { get; set; } = new();

        /// <summary>
        /// Caching configuration
        /// </summary>
        public CacheConfig Cache { get; set; } = new();

        /// <summary>
        /// Timeout configurations
        /// </summary>
        public TimeoutConfig Timeouts { get; set; } = new();
    }

    public class RateLimitConfig
    {
        /// <summary>
        /// Minimum delay between Steam Web API calls (milliseconds)
        /// </summary>
        public int WebApiDelayMs { get; set; } = 100;

        /// <summary>
        /// Minimum delay between Market API calls (milliseconds)
        /// Recommended: 5000ms (5 seconds) to avoid 429 errors
        /// </summary>
        public int MarketApiDelayMs { get; set; } = 5000;

        /// <summary>
        /// Minimum delay between Store API calls (milliseconds)
        /// </summary>
        public int StoreApiDelayMs { get; set; } = 1000;

        /// <summary>
        /// Maximum concurrent requests to Store API
        /// </summary>
        public int StoreApiMaxConcurrency { get; set; } = 8;

        /// <summary>
        /// Circuit breaker threshold (consecutive 429 errors)
        /// </summary>
        public int CircuitBreakerThreshold { get; set; } = 3;

        /// <summary>
        /// Circuit breaker cooldown period (seconds)
        /// </summary>
        public int CircuitBreakerCooldownSeconds { get; set; } = 120;

        /// <summary>
        /// Maximum retry attempts for failed requests
        /// </summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>
        /// Base delay for exponential backoff (milliseconds)
        /// </summary>
        public int ExponentialBackoffBaseMs { get; set; } = 1000;
    }

    public class CacheConfig
    {
        /// <summary>
        /// Cache duration for player summaries (minutes)
        /// </summary>
        public int PlayerSummaryMinutes { get; set; } = 10;

        /// <summary>
        /// Cache duration for owned games (minutes)
        /// </summary>
        public int OwnedGamesMinutes { get; set; } = 15;

        /// <summary>
        /// Cache duration for app details (hours)
        /// </summary>
        public int AppDetailsHours { get; set; } = 6;

        /// <summary>
        /// Cache duration for market prices (hours)
        /// </summary>
        public int MarketPriceHours { get; set; } = 3;

        /// <summary>
        /// Cache duration for inventory data (minutes)
        /// </summary>
        public int InventoryMinutes { get; set; } = 15;

        /// <summary>
        /// Cache duration for friend list (minutes)
        /// </summary>
        public int FriendListMinutes { get; set; } = 15;

        /// <summary>
        /// Cache duration for badges (hours)
        /// </summary>
        public int BadgesHours { get; set; } = 1;

        /// <summary>
        /// Cache duration for wishlist (minutes)
        /// </summary>
        public int WishlistMinutes { get; set; } = 30;

        /// <summary>
        /// Cache duration for live player counts (minutes)
        /// </summary>
        public int LivePlayerCountMinutes { get; set; } = 5;

        /// <summary>
        /// Cache duration for news (hours)
        /// </summary>
        public int NewsHours { get; set; } = 2;
    }

    public class TimeoutConfig
    {
        /// <summary>
        /// HTTP client timeout (seconds)
        /// </summary>
        public int HttpClientTimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// SignalR client timeout (seconds)
        /// </summary>
        public int SignalRClientTimeoutSeconds { get; set; } = 1800;

        /// <summary>
        /// SignalR keep-alive interval (seconds)
        /// </summary>
        public int SignalRKeepAliveSeconds { get; set; } = 10;

        /// <summary>
        /// SignalR handshake timeout (seconds)
        /// </summary>
        public int SignalRHandshakeTimeoutSeconds { get; set; } = 30;
    }

    /// <summary>
    /// Steam API endpoint URLs
    /// </summary>
    public static class SteamApiEndpoints
    {
        // ISteamUser
        public const string ResolveVanityUrl = "https://api.steampowered.com/ISteamUser/ResolveVanityURL/v1/";
        public const string GetPlayerSummaries = "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/";
        public const string GetFriendList = "https://api.steampowered.com/ISteamUser/GetFriendList/v1/";
        public const string GetPlayerBans = "https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/";
        public const string GetUserGroupList = "https://api.steampowered.com/ISteamUser/GetUserGroupList/v1/";

        // IPlayerService
        public const string GetOwnedGames = "https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/";
        public const string GetRecentlyPlayedGames = "https://api.steampowered.com/IPlayerService/GetRecentlyPlayedGames/v1/";
        public const string GetSteamLevel = "https://api.steampowered.com/IPlayerService/GetSteamLevel/v1/";
        public const string GetBadges = "https://api.steampowered.com/IPlayerService/GetBadges/v1/";
        public const string GetCommunityBadgeProgress = "https://api.steampowered.com/IPlayerService/GetCommunityBadgeProgress/v1/";

        // ISteamUserStats
        public const string GetPlayerAchievements = "https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v1/";
        public const string GetUserStatsForGame = "https://api.steampowered.com/ISteamUserStats/GetUserStatsForGame/v2/";
        public const string GetSchemaForGame = "https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/";
        public const string GetNumberOfCurrentPlayers = "https://api.steampowered.com/ISteamUserStats/GetNumberOfCurrentPlayers/v1/";
        public const string GetGlobalAchievementPercentagesForApp = "https://api.steampowered.com/ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/";
        public const string GetGlobalStatsForGame = "https://api.steampowered.com/ISteamUserStats/GetGlobalStatsForGame/v1/";

        // ISteamNews
        public const string GetNewsForApp = "https://api.steampowered.com/ISteamNews/GetNewsForApp/v2/";

        // ISteamApps
        public const string GetAppList = "https://api.steampowered.com/ISteamApps/GetAppList/v2/";
        public const string UpToDateCheck = "https://api.steampowered.com/ISteamApps/UpToDateCheck/v1/";
        public const string GetServersAtAddress = "https://api.steampowered.com/ISteamApps/GetServersAtAddress/v1/";

        // Store API (no authentication required)
        public const string StoreAppDetails = "https://store.steampowered.com/api/appdetails";
        public const string StoreSearch = "https://store.steampowered.com/api/storesearch/";
        public const string StoreFeaturedCategories = "https://store.steampowered.com/api/featuredcategories/";
        public const string StoreFeatured = "https://store.steampowered.com/api/featured/";

        // Community Market API (no authentication, but heavily rate limited)
        public const string MarketPriceOverview = "https://steamcommunity.com/market/priceoverview/";
        public const string MarketPriceHistory = "https://steamcommunity.com/market/pricehistory/";
        public const string MarketSearch = "https://steamcommunity.com/market/search/render/";
        public const string MarketListings = "https://steamcommunity.com/market/listings/";

        // Community API (no authentication)
        public const string Inventory = "https://steamcommunity.com/inventory/";
        public const string Wishlist = "https://store.steampowered.com/wishlist/profiles/";
    }

    /// <summary>
    /// Steam persona states (online status)
    /// </summary>
    public static class SteamPersonaState
    {
        public const int Offline = 0;
        public const int Online = 1;
        public const int Busy = 2;
        public const int Away = 3;
        public const int Snooze = 4;
        public const int LookingToTrade = 5;
        public const int LookingToPlay = 6;

        public static string GetStateText(int state) => state switch
        {
            1 => "Online",
            2 => "Ocupado",
            3 => "Ausente",
            4 => "Cochilando",
            5 => "Quer Trocar",
            6 => "Quer Jogar",
            _ => "Offline"
        };
    }

    /// <summary>
    /// Steam visibility states
    /// </summary>
    public static class SteamVisibilityState
    {
        public const int Private = 1;
        public const int FriendsOnly = 2;
        public const int Public = 3;
    }

    /// <summary>
    /// Supported inventory app IDs
    /// </summary>
    public static class SupportedInventoryApps
    {
        public static readonly Dictionary<int, string> Apps = new()
        {
            { 730, "CS2" },
            { 570, "Dota 2" },
            { 440, "TF2" },
            { 252490, "Rust" },
            { 1172470, "Apex Legends" },
            { 578080, "PUBG" },
            { 304930, "Unturned" },
            { 271590, "GTA V" },
            { 218620, "Payday 2" },
            { 892970, "Valheim" }
        };

        public static string GetAppName(int appId) => Apps.TryGetValue(appId, out var name) ? name : $"App {appId}";
    }
}
