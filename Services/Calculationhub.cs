using Microsoft.AspNetCore.SignalR;
using SteamValue.Models;

namespace SteamValue.Services
{
    /// <summary>
    /// Hub SignalR para comunicação em tempo real com o cliente
    /// Alternativa mais robusta ao Server-Sent Events (SSE)
    /// </summary>
    public class CalculationHub : Hub
    {
        private readonly SteamService _steamService;

        public CalculationHub(SteamService steamService)
        {
            _steamService = steamService;
        }

        /// <summary>
        /// Inicia o cálculo do valor da conta Steam
        /// </summary>
        public async Task StartCalculation(string profileUrl, bool calculateGames, bool calculateInventory)
        {
            Func<int, string, Task> progressCallback = async (p, m) =>
            {
                try
                {
                    await Clients.Caller.SendAsync("UpdateProgress", p, m);
                }
                catch
                {
                    // Client disconnected, ignore
                }
            };

            try
            {
                var steamId = await _steamService.ResolveSteamIdAsync(profileUrl, progressCallback);
                double totalValue = 0;

                // Passo 2: Calcular jogos (20% - 40%)
                if (calculateGames)
                {
                    await Clients.Caller.SendAsync("UpdateProgress", 20, "Buscando jogos da biblioteca...");

                    var (gamesTotal, gamesList) = await _steamService.CalculateGamesValueAsync(steamId, progressCallback);
                    totalValue += gamesTotal;

                    // Enviar dados dos jogos (com imagem) — usar propriedade `price` para consistência
                    var gamesData = gamesList
                        .Select(g => new
                        {
                            name = g.Name,
                            price = g.Price,
                            imageUrl = string.IsNullOrWhiteSpace(g.ImageUrl) ? $"https://cdn.akamai.steamstatic.com/steam/apps/{g.AppId}/header.jpg" : g.ImageUrl,
                            appId = g.AppId,
                            playtimeMinutes = g.PlaytimeMinutes
                        }).ToList();

                    await Clients.Caller.SendAsync("ReceiveGamesData", gamesData, gamesTotal);
                }

                // Passo 3: Calcular inventários (40% - 90%)
                if (calculateInventory)
                {
                    // CS2
                    await Clients.Caller.SendAsync("UpdateProgress", 50, "Analisando inventário CS2...");
                    var (cs2Total, cs2List) = await _steamService.CalculateInventoryValueAsync(steamId, 730, "CS2", progressCallback);
                    totalValue += cs2Total;

                    var cs2Data = cs2List
                        .Select(item => new
                        {
                            name = item.Name,
                            price = item.Price,
                            imageUrl = string.IsNullOrWhiteSpace(item.ImageUrl) ? string.Empty : item.ImageUrl,
                            appId = 730
                        }).ToList();

                    await Clients.Caller.SendAsync("ReceiveInventoryData", "CS2", cs2Data, cs2Total);

                    // Dota 2
                    await Clients.Caller.SendAsync("UpdateProgress", 65, "Analisando inventário Dota 2...");
                    var (dota2Total, dota2List) = await _steamService.CalculateInventoryValueAsync(steamId, 570, "Dota 2", progressCallback);
                    totalValue += dota2Total;

                    var dota2Data = dota2List
                        .Select(item => new
                        {
                            name = item.Name,
                            price = item.Price,
                            imageUrl = string.IsNullOrWhiteSpace(item.ImageUrl) ? string.Empty : item.ImageUrl,
                            appId = 570
                        }).ToList();

                    await Clients.Caller.SendAsync("ReceiveInventoryData", "Dota 2", dota2Data, dota2Total);

                    // TF2
                    await Clients.Caller.SendAsync("UpdateProgress", 80, "Analisando inventário TF2...");
                    var (tf2Total, tf2List) = await _steamService.CalculateInventoryValueAsync(steamId, 440, "TF2", progressCallback);
                    totalValue += tf2Total;

                    var tf2Data = tf2List
                        .Select(item => new
                        {
                            name = item.Name,
                            price = item.Price,
                            imageUrl = string.IsNullOrWhiteSpace(item.ImageUrl) ? string.Empty : item.ImageUrl,
                            appId = 440
                        }).ToList();

                    await Clients.Caller.SendAsync("ReceiveInventoryData", "TF2", tf2Data, tf2Total);
                }

                // Passo 4: Finalizar (90% - 100%)
                await Clients.Caller.SendAsync("UpdateProgress", 90, "Calculando valor total...");
                await Clients.Caller.SendAsync("ReceiveTotalValue", totalValue);
                await Clients.Caller.SendAsync("UpdateProgress", 100, "Cálculo concluído!");
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // Compute totals for a given steamId (used for friends comparison)
        public async Task ComputeTotalsForSteamId(string steamId, bool calculateGames, bool calculateInventory)
        {
            Func<int, string, Task> progressCallback = async (p, m) =>
            {
                try
                {
                    await Clients.Caller.SendAsync("UpdateProgress", p, m);
                }
                catch { }
            };

            try
            {
                double totalValue = 0;

                if (calculateGames)
                {
                    var (gamesTotal, _) = await _steamService.CalculateGamesValueAsync(steamId, progressCallback);
                    totalValue += gamesTotal;
                }

                if (calculateInventory)
                {
                    var (invTotal, _) = await _steamService.CalculateInventoryValueAsync(steamId, 730, "CS2", progressCallback);
                    totalValue += invTotal;
                    // Note: inventory for other apps omitted for quick friend compare
                }

                await Clients.Caller.SendAsync("ReceiveFriendTotal", steamId, totalValue);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        // Métodos auxiliares para extrair nome e valor
        private string ExtractGameName(string gameString)
        {
            var parts = gameString.Split(": R$ ");
            return parts.Length > 0 ? parts[0].Trim() : gameString;
        }

        private double ExtractGameValue(string gameString)
        {
            var parts = gameString.Split(": R$ ");
            if (parts.Length > 1)
            {
                return double.Parse(parts[1].Trim());
            }
            return 0;
        }

        private string ExtractItemName(string itemString)
        {
            var parts = itemString.Split(": R$ ");
            return parts.Length > 0 ? parts[0].Trim() : itemString;
        }

        private double ExtractItemValue(string itemString)
        {
            var parts = itemString.Split(": R$ ");
            if (parts.Length > 1)
            {
                return double.Parse(parts[1].Trim());
            }
            return 0;
        }

        // Novos métodos para recursos adicionais
        public async Task GetProfileSummary(string profileUrl)
        {
            try
            {
                var steamId = await _steamService.ResolveSteamIdAsync(profileUrl);
                var summary = await _steamService.GetPlayerSummariesAsync(steamId);
                await Clients.Caller.SendAsync("ReceiveProfileSummary", summary);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        public async Task GetFriends(string profileUrl)
        {
            try
            {
                var steamId = await _steamService.ResolveSteamIdAsync(profileUrl);
                var friends = await _steamService.GetFriendListAsync(steamId);
                await Clients.Caller.SendAsync("ReceiveFriends", friends);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        public async Task GetSnapshots(string profileUrl)
        {
            try
            {
                var steamId = await _steamService.ResolveSteamIdAsync(profileUrl);
                var snaps = _steamService.GetAccountSnapshots(steamId);
                await Clients.Caller.SendAsync("ReceiveSnapshots", snaps);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        public async Task GetAchievements(string profileUrl, int appId)
        {
            try
            {
                var steamId = await _steamService.ResolveSteamIdAsync(profileUrl);
                var percent = await _steamService.GetPlayerAchievementPercentageAsync(steamId, appId);
                await Clients.Caller.SendAsync("ReceiveAchievements", appId, percent);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }

        public async Task GetMarketOverview(int appId, string marketHashName)
        {
            try
            {
                var price = await _steamService.GetMarketPriceOverviewAsync(marketHashName, appId);
                await Clients.Caller.SendAsync("ReceiveMarketOverview", appId, marketHashName, price);
            }
            catch (Exception ex)
            {
                await Clients.Caller.SendAsync("ReceiveError", ex.Message);
            }
        }
    }
}