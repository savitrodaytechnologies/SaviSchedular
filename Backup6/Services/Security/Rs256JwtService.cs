using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace SaviSchedular.Services.Security
{
    public class RsaKeyPair
    {
        public string PrivateKeyXml { get; set; }
        public string PublicKeyXml { get; set; }
    }

    public static class Rs256JwtService
    {
        /// <summary>
        /// Generates a new 2048-bit RSA Key Pair (Private & Public Key XML format)
        /// </summary>
        public static RsaKeyPair GenerateKeyPair()
        {
            using (var rsa = new RSACryptoServiceProvider(2048))
            {
                rsa.PersistKeyInCsp = false;
                return new RsaKeyPair
                {
                    PrivateKeyXml = rsa.ToXmlString(true),  // Includes Private & Public Key
                    PublicKeyXml = rsa.ToXmlString(false)   // Public Key Only
                };
            }
        }

        /// <summary>
        /// Signs a short-lived (2 minutes) RS256 JWT Token using the target system's RSA Private Key
        /// </summary>
        public static string GenerateRs256JwtToken(string privateKeyXml, string issuer, string audience, int expiryMinutes = 2)
        {
            if (string.IsNullOrWhiteSpace(privateKeyXml))
                throw new ArgumentException("RSA Private Key is required to sign RS256 JWT token.");

            issuer = string.IsNullOrWhiteSpace(issuer) ? "SaviScheduler" : issuer;
            audience = string.IsNullOrWhiteSpace(audience) ? "SaviSchools" : audience;

            // 1. Build Header
            var headerObj = new { alg = "RS256", typ = "JWT" };
            string headerJson = JsonConvert.SerializeObject(headerObj);
            string headerBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));

            // 2. Build Payload
            long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long expUnix = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes).ToUnixTimeSeconds();

            var payloadObj = new
            {
                iss = issuer,
                aud = audience,
                sub = "Scheduler",
                iat = nowUnix,
                exp = expUnix,
                jti = Guid.NewGuid().ToString("N")
            };
            string payloadJson = JsonConvert.SerializeObject(payloadObj);
            string payloadBase64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));

            // 3. Data to sign: Header.Payload
            string unsignedToken = $"{headerBase64}.{payloadBase64}";
            byte[] unsignedData = Encoding.UTF8.GetBytes(unsignedToken);

            // 4. Sign using RSA Private Key & SHA256
            byte[] signatureBytes;
            using (var rsa = new RSACryptoServiceProvider(2048))
            {
                rsa.PersistKeyInCsp = false;
                rsa.FromXmlString(privateKeyXml);
                signatureBytes = rsa.SignData(unsignedData, CryptoConfig.MapNameToOID("SHA256"));
            }

            string signatureBase64 = Base64UrlEncode(signatureBytes);

            // 5. Final RS256 JWT
            return $"{unsignedToken}.{signatureBase64}";
        }

        private static string Base64UrlEncode(byte[] input)
        {
            string base64 = Convert.ToBase64String(input);
            return base64.Split('=')[0]
                .Replace('+', '-')
                .Replace('/', '_');
        }
    }
}
