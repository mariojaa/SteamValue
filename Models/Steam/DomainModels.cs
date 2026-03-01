namespace SteamValue.Models.Steam
{
    // ???????????????????????????????????????????????????????????????????????????
    // Business Domain Models - Used by the application
    // ???????????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Game with enriched data (price, playtime, metadata)
    /// </summary>
    public class GameInfo
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
        public string Publisher { get; set; } = "";
        public int MetacriticScore { get; set; }
        public double CommunityScore { get; set; }
        public double HoursPerDollar { get; set; }
        public int FriendPopularity { get; set; }
        public bool IsFree { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public List<string> Tags { get; set; } = new();
    }

    /// <summary>
    /// Inventory item with price and metadata
    /// </summary>
    public class InventoryItemInfo
    {
        public string Name { get; set; } = "";
        public string MarketHashName { get; set; } = "";
        public double Price { get; set; }
        public double UnitPrice { get; set; }
        public int Count { get; set; } = 1;
        public string ImageUrl { get; set; } = "";
        public string Type { get; set; } = "";
        public string Rarity { get; set; }
        public string RarityColor { get; set; } = "";
        public int AppId { get; set; }
        public bool Tradable { get; set; }
        public bool Marketable { get; set; }
        public bool Commodity { get; set; }
        public List<string> Tags { get; set; } = new();
    }

    /// <summary>
    /// Profile information with extended data
    /// </summary>
    public class ProfileInfo
    {
        public string SteamId { get; set; } = "";
        public string PersonaName { get; set; } = "";
        public string ProfileUrl { get; set; } = "";
        public string Avatar { get; set; } = "";
        public string AvatarMedium { get; set; } = "";
        public string AvatarFull { get; set; } = "";
        public int PersonaState { get; set; }
        public string PersonaStateText { get; set; } = "";
        public int CommunityVisibilityState { get; set; }
        public long LastLogoff { get; set; }
        public long TimeCreated { get; set; }
        public string? RealName { get; set; }
        public string? CountryCode { get; set; }
        public string? StateCode { get; set; }
        public string? CurrentGame { get; set; }
        public string? CurrentGameId { get; set; }
        public int Level { get; set; }
        public int BadgeCount { get; set; }
        public int TotalXp { get; set; }
        public PlayerBanStatus? BanStatus { get; set; }
        public List<GameInfo> RecentGames { get; set; } = new();
    }

    /// <summary>
    /// Wishlist item with current price
    /// </summary>
    public class WishlistItemInfo
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public int Priority { get; set; }
        public long Added { get; set; }
        public string ImageUrl { get; set; } = "";
        public double CurrentPrice { get; set; }
        public double? OriginalPrice { get; set; }
        public int? DiscountPercent { get; set; }
        public string Genre { get; set; } = "";
        public string Developer { get; set; } = "";
        public int MetacriticScore { get; set; }
        public int SaleProbability { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public bool IsFree { get; set; }
        public bool IsReleased { get; set; }
    }

    /// <summary>
    /// Achievement with enriched data
    /// </summary>
    public class AchievementInfo
    {
        public string ApiName { get; set; } = "";
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public bool Achieved { get; set; }
        public long UnlockTime { get; set; }
        public string IconUrl { get; set; } = "";
        public string IconGrayUrl { get; set; } = "";
        public double GlobalPercent { get; set; }
        public bool IsRare { get; set; }
    }

    /// <summary>
    /// Badge with enriched data
    /// </summary>
    public class BadgeInfo
    {
        public int BadgeId { get; set; }
        public int Level { get; set; }
        public long CompletionTime { get; set; }
        public int Xp { get; set; }
        public int Scarcity { get; set; }
        public int? AppId { get; set; }
        public string? AppName { get; set; }
        public string ImageUrl { get; set; } = "";
        public string? BorderColor { get; set; }
    }

    /// <summary>
    /// Friend with enriched profile data
    /// </summary>
    public class FriendInfo
    {
        public string SteamId { get; set; } = "";
        public string PersonaName { get; set; } = "";
        public string Avatar { get; set; } = "";
        public string AvatarMedium { get; set; } = "";
        public int PersonaState { get; set; }
        public string PersonaStateText { get; set; } = "";
        public long LastLogoff { get; set; }
        public long FriendSince { get; set; }
        public string? CountryCode { get; set; }
        public string? CurrentGame { get; set; }
        public string? CurrentGameId { get; set; }
        public bool IsOnline { get; set; }
        public int CommunityVisibilityState { get; set; }
    }
}
