using System.Text.RegularExpressions;

namespace SteamValue.Helpers
{
    /// <summary>
    /// Helper utilities for Steam API operations
    /// </summary>
    public static class SteamHelpers
    {
        /// <summary>
        /// Extracts Steam ID from various URL formats
        /// Supports: steamcommunity.com/id/vanityurl, /profiles/steamid64, or raw steam id
        /// </summary>
        public static string? ExtractSteamIdFromUrl(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            var trimmed = input.Trim();

            // Direct 64-bit Steam ID in URL: /profiles/76561198...
            var matchNumeric = Regex.Match(trimmed, @"profiles/(\d{17})");
            if (matchNumeric.Success)
                return matchNumeric.Groups[1].Value;

            // Just the Steam ID itself
            if (Regex.IsMatch(trimmed, @"^\d{17}$"))
                return trimmed;

            // Vanity URL: /id/customname
            var matchVanity = Regex.Match(trimmed, @"id/([^/?\s]+)");
            if (matchVanity.Success)
                return matchVanity.Groups[1].Value; // Return vanity name for resolution

            return null;
        }

        /// <summary>
        /// Checks if input is a direct Steam ID (numeric 64-bit)
        /// </summary>
        public static bool IsDirectSteamId(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            return Regex.IsMatch(input.Trim(), @"^\d{17}$");
        }

        /// <summary>
        /// Parses Steam Market price strings (handles various currency formats)
        /// Examples: "R$ 1,50", "$1.50", "€1,50", "¥150"
        /// </summary>
        public static double ParseMarketPrice(string priceString)
        {
            if (string.IsNullOrWhiteSpace(priceString)) return 0;

            // Remove currency symbols and extra spaces
            var cleaned = Regex.Replace(priceString, @"[^\d,.]", "").Trim();
            if (string.IsNullOrEmpty(cleaned)) return 0;

            // Handle BRL format: "1.234,56" -> "1234.56"
            if (Regex.IsMatch(cleaned, @",\d{2}$") && !cleaned.Contains('.'))
            {
                cleaned = cleaned.Replace(",", ".");
            }
            // Handle US format: "1,234.56" -> "1234.56"
            else if (Regex.IsMatch(cleaned, @"\.\d{2}$") && cleaned.Contains(','))
            {
                cleaned = cleaned.Replace(",", "");
            }
            // Mixed format: determine which is decimal separator
            else if (cleaned.Contains(',') && cleaned.Contains('.'))
            {
                int dotIdx = cleaned.LastIndexOf('.');
                int commaIdx = cleaned.LastIndexOf(',');
                cleaned = commaIdx > dotIdx
                    ? cleaned.Replace(".", "").Replace(",", ".")
                    : cleaned.Replace(",", "");
            }
            else
            {
                cleaned = cleaned.Replace(",", "");
            }

            return double.TryParse(cleaned,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var price) ? price : 0;
        }

        /// <summary>
        /// Formats price to BRL currency
        /// </summary>
        public static string FormatBRL(double value)
        {
            return value.ToString("C2", new System.Globalization.CultureInfo("pt-BR"));
        }

        /// <summary>
        /// Builds inventory image URL from icon hash
        /// </summary>
        public static string BuildInventoryImageUrl(string iconHash)
        {
            if (string.IsNullOrWhiteSpace(iconHash)) return "";
            if (iconHash.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return iconHash;
            return $"https://community.cloudflare.steamstatic.com/economy/image/{iconHash.TrimStart('/')}";
        }

        /// <summary>
        /// Builds game icon URL
        /// </summary>
        public static string BuildGameIconUrl(int appId, string iconHash)
        {
            if (string.IsNullOrWhiteSpace(iconHash)) 
                return $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
            return $"https://media.steampowered.com/steamcommunity/public/images/apps/{appId}/{iconHash}.jpg";
        }

        /// <summary>
        /// Builds game header image URL
        /// </summary>
        public static string BuildGameHeaderUrl(int appId)
        {
            return $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg";
        }

        /// <summary>
        /// Builds game capsule image URL
        /// </summary>
        public static string BuildGameCapsuleUrl(int appId, string size = "sm_120")
        {
            return $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/capsule_{size}.jpg";
        }

        /// <summary>
        /// Gets rarity color for inventory items
        /// </summary>
        public static string GetRarityColor(string? rarity)
        {
            if (string.IsNullOrWhiteSpace(rarity)) return "#8fadc8";

            var r = rarity.ToLowerInvariant();

            // CS:GO/CS2 rarity colors
            if (r.Contains("covert")) return "#ff4444";
            if (r.Contains("classified")) return "#eb4b4b";
            if (r.Contains("restricted")) return "#8847ff";
            if (r.Contains("mil-spec") || r.Contains("milspec")) return "#4b69ff";
            if (r.Contains("industrial")) return "#5e98d9";
            if (r.Contains("consumer")) return "#b0c3d9";
            if (r.Contains("contraband")) return "#e4ae39";

            // Dota 2 rarity colors
            if (r.Contains("arcana") || r.Contains("ancient")) return "#eb4b4b";
            if (r.Contains("immortal")) return "#e4ae39";
            if (r.Contains("legendary")) return "#d32ce6";
            if (r.Contains("mythical")) return "#8847ff";
            if (r.Contains("rare")) return "#4b69ff";
            if (r.Contains("uncommon")) return "#5e98d9";
            if (r.Contains("common")) return "#b0c3d9";

            return "#8fadc8";
        }

        /// <summary>
        /// Formats playtime in human-readable format
        /// </summary>
        public static string FormatPlaytime(int minutes)
        {
            if (minutes == 0) return "0 min";
            if (minutes < 60) return $"{minutes} min";
            
            var hours = minutes / 60;
            var remainingMinutes = minutes % 60;
            
            if (remainingMinutes == 0)
                return $"{hours}h";
            
            return $"{hours}h {remainingMinutes}min";
        }

        /// <summary>
        /// Strips HTML tags from string
        /// </summary>
        public static string StripHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            
            // Remove HTML tags
            html = Regex.Replace(html, @"<[^>]*>", " ");
            
            // Remove Steam BB codes
            html = Regex.Replace(html, @"\{[^}]*\}", "");
            html = Regex.Replace(html, @"\[[^\]]*\]", "");
            
            // Normalize whitespace
            html = Regex.Replace(html, @"\s+", " ");
            
            return html.Trim();
        }

        /// <summary>
        /// Determines if an app supports inventory based on known app IDs
        /// </summary>
        public static bool SupportsInventory(int appId)
        {
            return SteamValue.Configuration.SupportedInventoryApps.Apps.ContainsKey(appId);
        }

        /// <summary>
        /// Chunks a list into smaller batches
        /// </summary>
        public static IEnumerable<List<T>> ChunkList<T>(IEnumerable<T> source, int chunkSize)
        {
            var list = source.ToList();
            for (int i = 0; i < list.Count; i += chunkSize)
            {
                yield return list.Skip(i).Take(chunkSize).ToList();
            }
        }

        /// <summary>
        /// Validates Steam ID format (64-bit)
        /// </summary>
        public static bool IsValidSteamId64(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId)) return false;
            if (!Regex.IsMatch(steamId, @"^\d{17}$")) return false;
            
            // Steam IDs start with 7656119...
            return steamId.StartsWith("7656119");
        }

        /// <summary>
        /// Gets country name from country code
        /// </summary>
        public static string GetCountryName(string countryCode)
        {
            var countries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "BR", "Brasil" }, { "US", "Estados Unidos" }, { "DE", "Alemanha" },
                { "GB", "Reino Unido" }, { "UK", "Reino Unido" }, { "FR", "França" },
                { "RU", "Rússia" }, { "PL", "Polônia" }, { "CA", "Canadá" },
                { "AU", "Austrália" }, { "NL", "Holanda" }, { "SE", "Suécia" },
                { "FI", "Finlândia" }, { "NO", "Noruega" }, { "AR", "Argentina" },
                { "MX", "México" }, { "ES", "Espanha" }, { "PT", "Portugal" },
                { "IT", "Itália" }, { "JP", "Japão" }, { "KR", "Coreia do Sul" },
                { "CN", "China" }, { "IN", "Índia" }, { "TR", "Turquia" },
                { "UA", "Ucrânia" }, { "CL", "Chile" }, { "CO", "Colômbia" },
                { "PE", "Peru" }, { "VE", "Venezuela" }, { "AT", "Áustria" },
                { "BE", "Bélgica" }, { "CH", "Suíça" }, { "CZ", "República Tcheca" },
                { "DK", "Dinamarca" }, { "GR", "Grécia" }, { "HU", "Hungria" },
                { "IE", "Irlanda" }, { "RO", "Romênia" }, { "TH", "Tailândia" },
                { "PH", "Filipinas" }, { "SG", "Singapura" }, { "MY", "Malásia" },
                { "ID", "Indonésia" }, { "VN", "Vietnã" }, { "ZA", "África do Sul" },
                { "NZ", "Nova Zelândia" }
            };

            return countries.TryGetValue(countryCode, out var name) ? name : countryCode;
        }

        /// <summary>
        /// Gets flag emoji from country code
        /// </summary>
        public static string GetCountryFlag(string countryCode)
        {
            if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Length != 2)
                return "??";

            // Convert country code to flag emoji (Unicode regional indicator symbols)
            var code = countryCode.ToUpperInvariant();
            if (code == "UK") code = "GB"; // UK uses GB flag
            
            var flags = new Dictionary<string, string>
            {
                { "BR", "????" }, { "US", "????" }, { "DE", "????" }, { "GB", "????" },
                { "FR", "????" }, { "RU", "????" }, { "PL", "????" }, { "CA", "????" },
                { "AU", "????" }, { "NL", "????" }, { "SE", "????" }, { "FI", "????" },
                { "NO", "????" }, { "AR", "????" }, { "MX", "????" }, { "ES", "????" },
                { "PT", "????" }, { "IT", "????" }, { "JP", "????" }, { "KR", "????" },
                { "CN", "????" }, { "IN", "????" }, { "TR", "????" }, { "UA", "????" },
                { "CL", "????" }, { "CO", "????" }, { "PE", "????" }, { "AT", "????" },
                { "BE", "????" }, { "CH", "????" }, { "CZ", "????" }, { "DK", "????" },
                { "GR", "????" }, { "HU", "????" }, { "IE", "????" }, { "RO", "????" },
                { "TH", "????" }, { "PH", "????" }, { "SG", "????" }, { "MY", "????" },
                { "ID", "????" }, { "VN", "????" }, { "ZA", "????" }, { "NZ", "????" }
            };

            return flags.TryGetValue(code, out var flag) ? flag : "??";
        }

        /// <summary>
        /// Calculates retry delay with exponential backoff and jitter
        /// </summary>
        public static int CalculateRetryDelay(int attemptNumber, int baseDelayMs = 1000)
        {
            var exponential = (int)Math.Pow(2, attemptNumber) * baseDelayMs;
            var jitter = Random.Shared.Next(0, 500);
            return Math.Min(exponential + jitter, 30000); // Max 30 seconds
        }

        /// <summary>
        /// Validates app ID format
        /// </summary>
        public static bool IsValidAppId(int appId)
        {
            return appId > 0 && appId <= 3000000; // Steam app IDs range
        }

        /// <summary>
        /// Truncates string to specified length with ellipsis
        /// </summary>
        public static string Truncate(string text, int maxLength)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
                return text;
            
            return text[..(maxLength - 1)] + "…";
        }

        /// <summary>
        /// Calculates percentage safely (avoids division by zero)
        /// </summary>
        public static double SafePercentage(int part, int total)
        {
            if (total <= 0) return 0;
            return Math.Round((double)part / total * 100.0, 2);
        }

        /// <summary>
        /// Gets CS:GO/CS2 wear category from item name
        /// </summary>
        public static string? GetWearCategory(string itemName)
        {
            if (string.IsNullOrWhiteSpace(itemName)) return null;

            var lower = itemName.ToLowerInvariant();
            
            if (lower.Contains("factory new") || lower.Contains("(fn)")) return "Factory New";
            if (lower.Contains("minimal wear") || lower.Contains("(mw)")) return "Minimal Wear";
            if (lower.Contains("field-tested") || lower.Contains("(ft)")) return "Field-Tested";
            if (lower.Contains("well-worn") || lower.Contains("(ww)")) return "Well-Worn";
            if (lower.Contains("battle-scarred") || lower.Contains("(bs)")) return "Battle-Scarred";

            return null;
        }

        /// <summary>
        /// Converts Unix timestamp to DateTime
        /// </summary>
        public static DateTime UnixTimeToDateTime(long unixTime)
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixTime).DateTime;
        }

        /// <summary>
        /// Converts DateTime to Unix timestamp
        /// </summary>
        public static long DateTimeToUnixTime(DateTime dateTime)
        {
            return ((DateTimeOffset)dateTime).ToUnixTimeSeconds();
        }

        /// <summary>
        /// Determines activity time slot from hour (0-23)
        /// </summary>
        public static string GetActivitySlot(int hour)
        {
            return hour switch
            {
                >= 0 and < 6 => "Madrugador",
                >= 6 and < 12 => "Matutino",
                >= 12 and < 18 => "Vespertino",
                _ => "Noturno"
            };
        }

        /// <summary>
        /// Sanitizes item name for market hash name
        /// </summary>
        public static string SanitizeMarketHashName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            
            // Steam market hash names can contain special characters, but we need proper encoding
            return name.Trim();
        }

        /// <summary>
        /// Checks if a price change is significant
        /// </summary>
        public static bool IsSignificantPriceChange(double oldPrice, double newPrice, double thresholdPercent = 5.0)
        {
            if (oldPrice <= 0) return newPrice > 0;
            
            var changePct = Math.Abs((newPrice - oldPrice) / oldPrice * 100.0);
            return changePct >= thresholdPercent;
        }
    }
}
