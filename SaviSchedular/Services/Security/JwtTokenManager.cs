using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace SaviSchedular.Services.Security
{
    public class CachedToken
    {
        public string Token { get; set; }
        public DateTime ExpiryUtc { get; set; }
    }

    public static class JwtTokenManager
    {
        // Zero-Leakage: Tokens stored ONLY in Application RAM
        private static readonly ConcurrentDictionary<int, CachedToken> TokenCache
            = new ConcurrentDictionary<int, CachedToken>();

        /// <summary>
        /// Retrieves valid token from RAM cache or fetches a fresh JWT token from TokenUrl using ClientId & ClientSecret
        /// </summary>
        public static async Task<string> GetValidTokenInternalAsync(int productId, string tokenUrl, string clientId, string clientSecret, string fallbackToken)
        {
            // If OAuth2 / Dynamic Token parameters are missing, fallback to Static Product Token
            if (string.IsNullOrWhiteSpace(tokenUrl) || string.IsNullOrWhiteSpace(clientId))
            {
                return fallbackToken;
            }

            // 1. Check RAM Cache
            if (TokenCache.TryGetValue(productId, out var cached))
            {
                // If token is valid and not expiring in next 60 seconds
                if (cached.ExpiryUtc > DateTime.UtcNow.AddSeconds(60) && !string.IsNullOrEmpty(cached.Token))
                {
                    return cached.Token;
                }
            }

            // 2. Fetch fresh token from TokenUrl
            string newToken = await FetchTokenFromAuthServerAsync(tokenUrl, clientId, clientSecret);
            if (!string.IsNullOrEmpty(newToken))
            {
                // Store in RAM Cache with 15 minute lifetime
                TokenCache[productId] = new CachedToken
                {
                    Token = newToken,
                    ExpiryUtc = DateTime.UtcNow.AddMinutes(15)
                };
                return newToken;
            }

            return fallbackToken;
        }

        /// <summary>
        /// Invalidate cached token on 401 Unauthorized
        /// </summary>
        public static void InvalidateToken(int productId)
        {
            TokenCache.TryRemove(productId, out _);
        }

        private static async Task<string> FetchTokenFromAuthServerAsync(string tokenUrl, string clientId, string clientSecret)
        {
            try
            {
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);

                    var form = new Dictionary<string, string>
                    {
                        { "grant_type", "client_credentials" },
                        { "client_id", clientId },
                        { "client_secret", clientSecret }
                    };

                    var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(form));
                    if (response.IsSuccessStatusCode)
                    {
                        string body = await response.Content.ReadAsStringAsync();
                        var json = JObject.Parse(body);
                        return json["access_token"]?.ToString() ?? json["token"]?.ToString() ?? json["jwt"]?.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[JwtTokenManager] Error fetching token from {tokenUrl}: {ex.Message}");
            }
            return null;
        }
    }
}
