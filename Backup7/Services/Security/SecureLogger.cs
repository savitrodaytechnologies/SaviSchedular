using System;
using System.Text.RegularExpressions;

namespace SaviSchedular.Services.Security
{
    public static class SecureLogger
    {
        private static readonly Regex TokenRegex = new Regex(
            @"(Authorization|Bearer|Token|secret|key)["":\s=]+([A-Za-z0-9\._\-+=/]{8,})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Sanitizes text to mask tokens and secrets before writing to logs, console, or DB
        /// </summary>
        public static string Sanitize(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;

            // Replace Bearer JWT / raw tokens with Authorization: ********
            string sanitized = TokenRegex.Replace(input, "$1: ********");

            return sanitized;
        }
    }
}
