namespace SteamValue.Models.Steam
{
    // ???????????????????????????????????????????????????????????????????????????
    // Feature-specific Models (Analytics, Comparisons, etc)
    // ???????????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Playtime analytics and statistics
    /// </summary>
    public class PlaytimeAnalytics
    {
        public int TotalGames { get; set; }
        public int PlayedGames { get; set; }
        public int NeverPlayedGames { get; set; }
        public double TotalHours { get; set; }
        public double AverageHoursPerGame { get; set; }
        public double MedianHoursPerGame { get; set; }
        public List<GameInfo> MostPlayedGames { get; set; } = new();
        public List<GameInfo> RecentlyPlayed { get; set; } = new();
        public double PlaytimePercentile { get; set; }
        public Dictionary<string, int> PlaytimeByGenre { get; set; } = new();
        public Dictionary<string, int> GamesByGenre { get; set; } = new();
    }

    /// <summary>
    /// Profile comparison data
    /// </summary>
    public class ProfileComparison
    {
        public string SteamId1 { get; set; } = "";
        public string SteamId2 { get; set; } = "";
        public string Name1 { get; set; } = "";
        public string Name2 { get; set; } = "";
        public string Avatar1 { get; set; } = "";
        public string Avatar2 { get; set; } = "";
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
        public List<GameInfo> CommonGames { get; set; } = new();
        public int ExclusiveGames1Count { get; set; }
        public int ExclusiveGames2Count { get; set; }
    }

    /// <summary>
    /// ROI analysis per game
    /// </summary>
    public class PlaytimeROI
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public double Price { get; set; }
        public double Hours { get; set; }
        public double CostPerHour { get; set; }
        public string ImageUrl { get; set; } = "";
        public string Genre { get; set; } = "";
        public string ROIRating { get; set; } = "";
    }

    /// <summary>
    /// Game scout - games friends own but you don't
    /// </summary>
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
        public List<string> FriendNames { get; set; } = new();
    }

    /// <summary>
    /// Leaderboard entry for friends ranking
    /// </summary>
    public class LeaderboardEntry
    {
        public int Rank { get; set; }
        public string SteamId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Avatar { get; set; } = "";
        public int Level { get; set; }
        public int TotalGames { get; set; }
        public double TotalHours { get; set; }
        public int BadgeCount { get; set; }
        public int TotalXp { get; set; }
        public bool IsMe { get; set; }
        public double Score { get; set; }
    }

    /// <summary>
    /// Trade tracker item with price history
    /// </summary>
    public class TradeTrackerItem
    {
        public string Name { get; set; } = "";
        public string MarketHashName { get; set; } = "";
        public double CurrentPrice { get; set; }
        public double MinPrice { get; set; }
        public double MaxPrice { get; set; }
        public double AvgPrice { get; set; }
        public double MedianPrice { get; set; }
        public double Trend { get; set; }
        public double RecentChange { get; set; }
        public List<PriceHistoryPoint> PriceHistory { get; set; } = new();
        public string ImageUrl { get; set; } = "";
        public int Count { get; set; }
        public int AppId { get; set; }
        public int Volume7d { get; set; }
        public int Volume30d { get; set; }
    }

    public class PriceHistoryPoint
    {
        public long Timestamp { get; set; }
        public double Price { get; set; }
        public int Volume { get; set; }
        public DateTime Date { get; set; }
    }

    /// <summary>
    /// Country distribution for friends
    /// </summary>
    public class CountryDistribution
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public double Percentage { get; set; }
        public string FlagEmoji { get; set; } = "";
    }

    /// <summary>
    /// Gamer DNA profile
    /// </summary>
    public class GamerDna
    {
        public string SteamId { get; set; } = "";
        public string Archetype { get; set; } = "";
        public string ArchetypeDescription { get; set; } = "";
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
        public int TopGameAppId { get; set; }
        public double TopGameHours { get; set; }
        public double TopGamePercent { get; set; }
        public string TopGameImage { get; set; } = "";
        public double RecentHours2w { get; set; }
        public int BadgeCount { get; set; }
        public int SteamLevel { get; set; }
        public List<string> TopGenres { get; set; } = new();
    }

    /// <summary>
    /// Friend activity pattern
    /// </summary>
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
        public int? PlayingGameAppId { get; set; }
    }

    /// <summary>
    /// Wishlist analysis with predictions
    /// </summary>
    public class WishlistAnalysis
    {
        public string SteamId { get; set; } = "";
        public int TotalItems { get; set; }
        public int PricedItems { get; set; }
        public int UnreleasedItems { get; set; }
        public double TotalFullPrice { get; set; }
        public double TotalPriorityPrice { get; set; }
        public double AveragePrice { get; set; }
        public List<WishlistItemInfo> LikelySaleItems { get; set; } = new();
        public List<WishlistItemInfo> HighPriorityItems { get; set; } = new();
        public List<WishlistItemInfo> Items { get; set; } = new();
        public Dictionary<string, int> GenreBreakdown { get; set; } = new();
    }

    /// <summary>
    /// Free game entry
    /// </summary>
    public class FreeGameEntry
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public double OriginalPrice { get; set; }
        public double FinalPrice { get; set; }
        public int DiscountPercent { get; set; }
        public bool IsFreeToPlay { get; set; }
        public bool IsLimitedTimePromo { get; set; }
        public string? EndDate { get; set; }
        public string Type { get; set; } = "";
        public string Genre { get; set; } = "";
        public int MetacriticScore { get; set; }
    }

    /// <summary>
    /// Game news entry
    /// </summary>
    public class GameNewsEntry
    {
        public int AppId { get; set; }
        public string GameName { get; set; } = "";
        public string GameImage { get; set; } = "";
        public string Gid { get; set; } = "";
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string Author { get; set; } = "";
        public string Contents { get; set; } = "";
        public long Date { get; set; }
        public string FeedName { get; set; } = "";
        public bool IsExternalUrl { get; set; }
    }

    /// <summary>
    /// Backlog analysis
    /// </summary>
    public class BacklogAnalysis
    {
        public string SteamId { get; set; } = "";
        public int TotalUnplayed { get; set; }
        public int TotalAnalyzed { get; set; }
        public double BacklogDebt { get; set; }
        public double AveragePriceUnplayed { get; set; }
        public List<BacklogGame> TopPriorityGames { get; set; } = new();
        public List<GenreCount> GenreBreakdown { get; set; } = new();
        public double TotalPotentialHours { get; set; }
    }

    public class BacklogGame
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public double Price { get; set; }
        public string Genre { get; set; } = "";
        public int MetacriticScore { get; set; }
        public string Developer { get; set; } = "";
        public double PriorityScore { get; set; }
        public int? RecommendationCount { get; set; }
        public DateTime? ReleaseDate { get; set; }
    }

    public class GenreCount
    {
        public string Genre { get; set; } = "";
        public int Count { get; set; }
        public double TotalValue { get; set; }
    }

    /// <summary>
    /// Store recommendation
    /// </summary>
    public class StoreRecommendation
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public double Price { get; set; }
        public int MetacriticScore { get; set; }
        public double ReviewScore { get; set; }
        public int ReviewCount { get; set; }
        public string ReviewText { get; set; } = "";
        public bool IsFree { get; set; }
        public string Genre { get; set; } = "";
        public List<string> Tags { get; set; } = new();
        public DateTime? ReleaseDate { get; set; }
    }

    /// <summary>
    /// Market item watchlist entry (stored in localStorage on client)
    /// </summary>
    public class WatchlistEntry
    {
        public string Name { get; set; } = "";
        public int AppId { get; set; }
        public long AddedAt { get; set; }
        public double? Price { get; set; }
        public double? PrevPrice { get; set; }
        public long? UpdatedAt { get; set; }
    }

    /// <summary>
    /// Account value snapshot for history tracking
    /// </summary>
    public class AccountSnapshot
    {
        public string SteamId { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public double TotalValue { get; set; }
        public double GamesValue { get; set; }
        public double InventoryValue { get; set; }
        public int GamesCount { get; set; }
        public int InventoryCount { get; set; }
    }

    /// <summary>
    /// Live player count data
    /// </summary>
    public class LivePlayerData
    {
        public int AppId { get; set; }
        public string AppName { get; set; } = "";
        public int PlayerCount { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string Trend { get; set; } = "";
    }
}
