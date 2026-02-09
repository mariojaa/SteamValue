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

                    // Enviar dados dos jogos (com imagem)
                    var gamesData = gamesList
                        .Select(g => new
                        {
                            name = g.Name,
                            value = g.Price,
                            imageUrl = string.IsNullOrWhiteSpace(g.ImageUrl) ? $"https://cdn.akamai.steamstatic.com/steam/apps/{g.AppId}/header.jpg" : g.ImageUrl,
                            appId = g.AppId
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
                            value = item.Price,
                            imageUrl = item.ImageUrl
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
                            value = item.Price,
                            imageUrl = item.ImageUrl
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
                            value = item.Price,
                            imageUrl = item.ImageUrl
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
    }
}