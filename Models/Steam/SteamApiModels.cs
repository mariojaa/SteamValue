namespace SteamValue.Models.Steam
{
    // ???????????????????????????????????????????????????????????????????????????
    // Steam Web API Response Models
    // Based on: https://steamcommunity.com/dev
    // ???????????????????????????????????????????????????????????????????????????

    /// <summary>
    /// Player profile information from ISteamUser/GetPlayerSummaries
    /// </summary>
    public class PlayerSummary
    {
        public string SteamId { get; set; } = "";
        public string PersonaName { get; set; } = "";
        public string ProfileUrl { get; set; } = "";
        public string Avatar { get; set; } = "";
        public string AvatarMedium { get; set; } = "";
        public string AvatarFull { get; set; } = "";
        public int PersonaState { get; set; }
        public int CommunityVisibilityState { get; set; }
        public int ProfileState { get; set; }
        public long LastLogoff { get; set; }
        public long TimeCreated { get; set; }
        public string? RealName { get; set; }
        public string? PrimaryClanId { get; set; }
        public string? LocCountryCode { get; set; }
        public string? LocStateCode { get; set; }
        public string? GameId { get; set; }
        public string? GameExtraInfo { get; set; }
        public int CommentPermission { get; set; }
    }

    /// <summary>
    /// Game information from IPlayerService/GetOwnedGames
    /// </summary>
    public class OwnedGame
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public int PlaytimeForever { get; set; }
        public int Playtime2Weeks { get; set; }
        public string ImgIconUrl { get; set; } = "";
        public string ImgLogoUrl { get; set; } = "";
        public bool HasCommunityVisibleStats { get; set; }
        public int? PlaytimeWindowsForever { get; set; }
        public int? PlaytimeMacForever { get; set; }
        public int? PlaytimeLinuxForever { get; set; }
    }

    /// <summary>
    /// Recently played game from IPlayerService/GetRecentlyPlayedGames
    /// </summary>
    public class RecentlyPlayedGame
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public int Playtime2Weeks { get; set; }
        public int PlaytimeForever { get; set; }
        public string ImgIconUrl { get; set; } = "";
        public string? ImgLogoUrl { get; set; }
    }

    /// <summary>
    /// Player ban status from ISteamUser/GetPlayerBans
    /// </summary>
    public class PlayerBanStatus
    {
        public string SteamId { get; set; } = "";
        public bool CommunityBanned { get; set; }
        public bool VACBanned { get; set; }
        public int NumberOfVACBans { get; set; }
        public int DaysSinceLastBan { get; set; }
        public int NumberOfGameBans { get; set; }
        public string EconomyBan { get; set; } = "none";
    }

    /// <summary>
    /// Friend entry from ISteamUser/GetFriendList
    /// </summary>
    public class SteamFriend
    {
        public string SteamId { get; set; } = "";
        public string Relationship { get; set; } = "friend";
        public long FriendSince { get; set; }
    }

    /// <summary>
    /// Achievement data from ISteamUserStats/GetPlayerAchievements
    /// </summary>
    public class PlayerAchievement
    {
        public string ApiName { get; set; } = "";
        public int Achieved { get; set; }
        public long UnlockTime { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// Achievement schema from ISteamUserStats/GetSchemaForGame
    /// </summary>
    public class AchievementSchema
    {
        public string Name { get; set; } = "";
        public int DefaultValue { get; set; }
        public string DisplayName { get; set; } = "";
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? IconGray { get; set; }
        public bool Hidden { get; set; }
    }

    /// <summary>
    /// Badge information from IPlayerService/GetBadges
    /// </summary>
    public class PlayerBadge
    {
        public int BadgeId { get; set; }
        public int Level { get; set; }
        public long CompletionTime { get; set; }
        public int Xp { get; set; }
        public int Scarcity { get; set; }
        public int? AppId { get; set; }
        public int? CommunityItemId { get; set; }
        public int? BorderColor { get; set; }
    }

    /// <summary>
    /// Steam level from IPlayerService/GetSteamLevel
    /// </summary>
    public class SteamLevel
    {
        public int PlayerLevel { get; set; }
    }

    /// <summary>
    /// User stats for a game from ISteamUserStats/GetUserStatsForGame
    /// </summary>
    public class PlayerStat
    {
        public string Name { get; set; } = "";
        public double Value { get; set; }
    }

    /// <summary>
    /// Global achievement percentages from ISteamUserStats/GetGlobalAchievementPercentagesForApp
    /// </summary>
    public class GlobalAchievementPercentage
    {
        public string Name { get; set; } = "";
        public double Percent { get; set; }
    }

    /// <summary>
    /// Current player count from ISteamUserStats/GetNumberOfCurrentPlayers
    /// </summary>
    public class CurrentPlayers
    {
        public int PlayerCount { get; set; }
        public int Result { get; set; }
    }

    /// <summary>
    /// Steam group info from ISteamUser/GetUserGroupList
    /// </summary>
    public class UserGroup
    {
        public string Gid { get; set; } = "";
    }

    /// <summary>
    /// Wishlist item from Store API
    /// </summary>
    public class WishlistEntry
    {
        public int AppId { get; set; }
        public string Name { get; set; } = "";
        public int Priority { get; set; }
        public long Added { get; set; }
        public int? Capsule { get; set; }
        public long? ReviewScore { get; set; }
        public string? ReviewDesc { get; set; }
        public int? ReviewsTotal { get; set; }
        public int? ReviewsPercent { get; set; }
        public int? ReleaseDate { get; set; }
        public string? ReleaseString { get; set; }
        public int? PlatformIcons { get; set; }
        public int? Subs { get; set; }
        public string? Type { get; set; }
        public List<string>? Screenshots { get; set; }
        public int? ReviewStatus { get; set; }
        public bool? Win { get; set; }
        public bool? Mac { get; set; }
        public bool? Linux { get; set; }
        public bool? Free { get; set; }
    }

    /// <summary>
    /// App details from Store API
    /// </summary>
    public class StoreAppDetails
    {
        public bool Success { get; set; }
        public StoreAppData? Data { get; set; }
    }

    public class StoreAppData
    {
        public string Type { get; set; } = "";
        public string Name { get; set; } = "";
        public int SteamAppId { get; set; }
        public int RequiredAge { get; set; }
        public bool IsFree { get; set; }
        public string? ControllerSupport { get; set; }
        public List<int>? Dlc { get; set; }
        public string DetailedDescription { get; set; } = "";
        public string AboutTheGame { get; set; } = "";
        public string ShortDescription { get; set; } = "";
        public string SupportedLanguages { get; set; } = "";
        public string HeaderImage { get; set; } = "";
        public string Website { get; set; } = "";
        public PcRequirements? PcRequirements { get; set; }
        public MacRequirements? MacRequirements { get; set; }
        public LinuxRequirements? LinuxRequirements { get; set; }
        public List<string>? Developers { get; set; }
        public List<string>? Publishers { get; set; }
        public PriceOverview? PriceOverview { get; set; }
        public List<int>? Packages { get; set; }
        public List<PackageGroup>? PackageGroups { get; set; }
        public Platforms? Platforms { get; set; }
        public Metacritic? Metacritic { get; set; }
        public List<Category>? Categories { get; set; }
        public List<Genre>? Genres { get; set; }
        public List<Screenshot>? Screenshots { get; set; }
        public List<Movie>? Movies { get; set; }
        public Recommendations? Recommendations { get; set; }
        public Achievements? Achievements { get; set; }
        public ReleaseDate? ReleaseDate { get; set; }
        public SupportInfo? SupportInfo { get; set; }
        public string Background { get; set; } = "";
        public string BackgroundRaw { get; set; } = "";
        public ContentDescriptors? ContentDescriptors { get; set; }
    }

    public class PriceOverview
    {
        public string Currency { get; set; } = "";
        public int Initial { get; set; }
        public int Final { get; set; }
        public int DiscountPercent { get; set; }
        public string InitialFormatted { get; set; } = "";
        public string FinalFormatted { get; set; } = "";
    }

    public class PcRequirements
    {
        public string? Minimum { get; set; }
        public string? Recommended { get; set; }
    }

    public class MacRequirements
    {
        public string? Minimum { get; set; }
        public string? Recommended { get; set; }
    }

    public class LinuxRequirements
    {
        public string? Minimum { get; set; }
        public string? Recommended { get; set; }
    }

    public class PackageGroup
    {
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string SelectionText { get; set; } = "";
        public string SaveText { get; set; } = "";
        public int DisplayType { get; set; }
        public string IsRecurringSubscription { get; set; } = "";
        public List<PackageSub>? Subs { get; set; }
    }

    public class PackageSub
    {
        public int PackageId { get; set; }
        public string PercentSavingsText { get; set; } = "";
        public int PercentSavings { get; set; }
        public string OptionText { get; set; } = "";
        public string OptionDescription { get; set; } = "";
        public string CanGetFreeLicense { get; set; } = "";
        public bool IsFreeLicense { get; set; }
        public int PriceInCentsWithDiscount { get; set; }
    }

    public class Platforms
    {
        public bool Windows { get; set; }
        public bool Mac { get; set; }
        public bool Linux { get; set; }
    }

    public class Metacritic
    {
        public int Score { get; set; }
        public string Url { get; set; } = "";
    }

    public class Category
    {
        public int Id { get; set; }
        public string Description { get; set; } = "";
    }

    public class Genre
    {
        public string Id { get; set; } = "";
        public string Description { get; set; } = "";
    }

    public class Screenshot
    {
        public int Id { get; set; }
        public string PathThumbnail { get; set; } = "";
        public string PathFull { get; set; } = "";
    }

    public class Movie
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Thumbnail { get; set; } = "";
        public Dictionary<string, string>? Webm { get; set; }
        public Dictionary<string, string>? Mp4 { get; set; }
        public bool Highlight { get; set; }
    }

    public class Recommendations
    {
        public int Total { get; set; }
    }

    public class Achievements
    {
        public int Total { get; set; }
        public List<AchievementHighlight>? Highlighted { get; set; }
    }

    public class AchievementHighlight
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
    }

    public class ReleaseDate
    {
        public bool ComingSoon { get; set; }
        public string Date { get; set; } = "";
    }

    public class SupportInfo
    {
        public string Url { get; set; } = "";
        public string Email { get; set; } = "";
    }

    public class ContentDescriptors
    {
        public List<int>? Ids { get; set; }
        public string? Notes { get; set; }
    }

    /// <summary>
    /// News item from ISteamNews/GetNewsForApp
    /// </summary>
    public class NewsItem
    {
        public string Gid { get; set; } = "";
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public bool IsExternalUrl { get; set; }
        public string Author { get; set; } = "";
        public string Contents { get; set; } = "";
        public string FeedLabel { get; set; } = "";
        public long Date { get; set; }
        public string FeedName { get; set; } = "";
        public int FeedType { get; set; }
        public int AppId { get; set; }
        public List<string>? Tags { get; set; }
    }

    /// <summary>
    /// Market price overview from Community Market API
    /// </summary>
    public class MarketPriceOverview
    {
        public bool Success { get; set; }
        public string? LowestPrice { get; set; }
        public string? Volume { get; set; }
        public string? MedianPrice { get; set; }
    }

    /// <summary>
    /// Inventory asset from Community Inventory API
    /// </summary>
    public class InventoryAsset
    {
        public string AssetId { get; set; } = "";
        public string ClassId { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public string Amount { get; set; } = "1";
        public bool Tradable { get; set; }
        public bool Marketable { get; set; }
        public bool CommodTity { get; set; }
    }

    /// <summary>
    /// Inventory item description from Community Inventory API
    /// </summary>
    public class InventoryDescription
    {
        public string ClassId { get; set; } = "";
        public string InstanceId { get; set; } = "";
        public bool Tradable { get; set; }
        public bool Marketable { get; set; }
        public bool Commodity { get; set; }
        public string MarketHashName { get; set; } = "";
        public string MarketName { get; set; } = "";
        public string Name { get; set; } = "";
        public string NameColor { get; set; } = "";
        public string BackgroundColor { get; set; } = "";
        public string Type { get; set; } = "";
        public string IconUrl { get; set; } = "";
        public string? IconUrlLarge { get; set; }
        public List<InventoryTag>? Tags { get; set; }
        public List<InventoryDescription>? Descriptions { get; set; }
        public InventoryActions? Actions { get; set; }
        public InventoryMarketActions? MarketActions { get; set; }
    }

    public class InventoryTag
    {
        public string Category { get; set; } = "";
        public string InternalName { get; set; } = "";
        public string LocalizedCategoryName { get; set; } = "";
        public string LocalizedTagName { get; set; } = "";
        public string? Color { get; set; }
    }

    public class InventoryActions
    {
        public string Link { get; set; } = "";
        public string Name { get; set; } = "";
    }

    public class InventoryMarketActions
    {
        public string Link { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>
    /// Schema for game stats from ISteamUserStats/GetSchemaForGame
    /// </summary>
    public class GameSchema
    {
        public string GameName { get; set; } = "";
        public string GameVersion { get; set; } = "";
        public AvailableGameStats? AvailableGameStats { get; set; }
    }

    public class AvailableGameStats
    {
        public List<StatSchema>? Stats { get; set; }
        public List<AchievementSchema>? Achievements { get; set; }
    }

    public class StatSchema
    {
        public string Name { get; set; } = "";
        public int DefaultValue { get; set; }
        public string? DisplayName { get; set; }
    }

    /// <summary>
    /// Global stats from ISteamUserStats/GetGlobalStatsForGame
    /// </summary>
    public class GlobalGameStat
    {
        public long StartDate { get; set; }
        public long EndDate { get; set; }
        public Dictionary<string, long> Data { get; set; } = new();
    }

    /// <summary>
    /// Server info from ISteamApps/GetServersAtAddress
    /// </summary>
    public class GameServer
    {
        public string Addr { get; set; } = "";
        public int Gmsindex { get; set; }
        public int AppId { get; set; }
        public string Gamedir { get; set; } = "";
        public int Region { get; set; }
        public bool Secure { get; set; }
        public bool Lan { get; set; }
        public int Gameport { get; set; }
        public int Specport { get; set; }
    }
}
