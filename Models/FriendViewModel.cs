public class FriendViewModel
{
    public string SteamId { get; set; } = "";
    public string PersonaName { get; set; } = "";
    public int TotalGames { get; set; }
    public decimal GamesValue { get; set; }
    public int TotalItems { get; set; }
    public decimal InventoryValue { get; set; }
    public decimal TotalValue => GamesValue + InventoryValue;
}
