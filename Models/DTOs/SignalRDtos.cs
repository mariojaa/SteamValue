namespace SteamValue.Models.DTOs
{
    // ???????????????????????????????????????????????????????????????????????????
    // Data Transfer Objects for SignalR Communication
    // These are lightweight objects optimized for JSON serialization
    // ???????????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Profile info DTO for client
    /// </summary>
    public class ProfileInfoDto
    {
        public string SteamId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Avatar { get; set; } = "";
        public int Personastate { get; set; }
        public string? Country { get; set; }
        public long LastLogoff { get; set; }
        public string? ProfileUrl { get; set; }
        public long Created { get; set; }
        public int Level { get; set; }
        public int BadgeCount { get; set; }
        public PlayerBansDto? Bans { get; set; }
        public List<RecentGameDto> RecentGames { get; set; } = new();
    }

    public class PlayerBansDto
    {
        public bool VacBanned { get; set; }
        public int NumberOfVacBans { get; set; }
        public int DaysSinceLastBan { get; set; }
        public int NumberOfGameBans { get; set; }
        public bool CommunityBanned { get; set; }
        public string EconomyBan { get; set; } = "none";
    }

    public class RecentGameDto
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public int Playtime2weeks { get; set; }
        public int PlaytimeForever { get; set; }
        public string ImageUrl { get; set; } = "";
    }

    /// <summary>
    /// Game DTO for client
    /// </summary>
    public class GameDto
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public double Price { get; set; }
        public string ImageUrl { get; set; } = "";
        public int PlaytimeMinutes { get; set; }
        public int Playtime2weeks { get; set; }
        public string Genre { get; set; } = "";
        public string Developer { get; set; } = "";
        public int Metacritic { get; set; }
        public double CommunityScore { get; set; }
        public double HoursPerDollar { get; set; }
    }

    /// <summary>
    /// Inventory item DTO for client
    /// </summary>
    public class InventoryItemDto
    {
        public string Name { get; set; } = "";
        public double Price { get; set; }
        public double UnitPrice { get; set; }
        public int Count { get; set; }
        public string ImageUrl { get; set; } = "";
        public string Type { get; set; } = "";
        public string Rarity { get; set; } = "";
        public int AppId { get; set; }
    }

    /// <summary>
    /// Wishlist item DTO for client
    /// </summary>
    public class WishlistItemDto
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public string ImageUrl { get; set; } = "";
        public int Priority { get; set; }
        public long Added { get; set; }
    }

    /// <summary>
    /// Badge DTO for client
    /// </summary>
    public class BadgeDto
    {
        public int BadgeId { get; set; }
        public int Level { get; set; }
        public int Xp { get; set; }
        public int? AppId { get; set; }
        public string ImageUrl { get; set; } = "";
    }

    /// <summary>
    /// Achievement DTO for client
    /// </summary>
    public class AchievementDto
    {
        public string ApiName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Achieved { get; set; }
        public long UnlockTime { get; set; }
        public string IconUrl { get; set; } = "";
        public double GlobalPercent { get; set; }
    }

    /// <summary>
    /// Playtime analytics DTO for client
    /// </summary>
    public class PlaytimeAnalyticsDto
    {
        public int TotalGames { get; set; }
        public int PlayedGames { get; set; }
        public int NeverPlayedGames { get; set; }
        public double TotalHours { get; set; }
        public double AverageHours { get; set; }
        public double PlayedPercent { get; set; }
        public List<MostPlayedGameDto> MostPlayed { get; set; } = new();
    }

    public class MostPlayedGameDto
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public double Hours { get; set; }
        public string ImageUrl { get; set; } = "";
    }

    /// <summary>
    /// Profile comparison DTO for client
    /// </summary>
    public class ProfileComparisonDto
    {
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
        public List<CommonGameDto> CommonGames { get; set; } = new();
    }

    public class CommonGameDto
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public string ImageUrl { get; set; } = "";
    }

    /// <summary>
    /// Live player count DTO for client
    /// </summary>
    public class LivePlayerCountDto
    {
        public int AppId { get; set; }
        public int Count { get; set; }
    }

    /// <summary>
    /// ROI item DTO for client
    /// </summary>
    public class ROIItemDto
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public double Price { get; set; }
        public double Hours { get; set; }
        public double CostPerHour { get; set; }
        public string ImageUrl { get; set; } = "";
        public string Genre { get; set; } = "";
    }

    /// <summary>
    /// Game scout item DTO for client
    /// </summary>
    public class GameScoutDto
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public int FriendsWhoOwn { get; set; }
        public double AvgFriendHours { get; set; }
        public double Price { get; set; }
        public string ImageUrl { get; set; } = "";
        public string Genre { get; set; } = "";
        public int Metacritic { get; set; }
    }

    /// <summary>
    /// Leaderboard entry DTO for client
    /// </summary>
    public class LeaderboardEntryDto
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

    /// <summary>
    /// Trade tracker item DTO for client
    /// </summary>
    public class TradeTrackerItemDto
    {
        public string Name { get; set; } = "";
        public double CurrentPrice { get; set; }
        public double MinPrice { get; set; }
        public double MaxPrice { get; set; }
        public double AvgPrice { get; set; }
        public double Trend { get; set; }
        public double RecentChange { get; set; }
        public List<object> PriceHistory { get; set; } = new();
        public string ImageUrl { get; set; } = "";
        public int Count { get; set; }
    }

    /// <summary>
    /// Country distribution DTO for client
    /// </summary>
    public class CountryDto
    {
        public string Code { get; set; } = "";
        public int Count { get; set; }
    }

    /// <summary>
    /// Snapshot DTO for client
    /// </summary>
    public class SnapshotDto
    {
        public string Time { get; set; } = "";
        public double Total { get; set; }
    }

    /// <summary>
    /// Friend DTO for client (Friends page)
    /// </summary>
    public class FriendDto
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Avatar { get; set; } = "";
        public int Visibility { get; set; }
        public int Personastate { get; set; }
        public bool IsOnline { get; set; }
        public long LastLogoff { get; set; }
        public string? Country { get; set; }
        public long FriendSince { get; set; }
        public string? GameId { get; set; }
        public string? GameExtra { get; set; }
    }
}
