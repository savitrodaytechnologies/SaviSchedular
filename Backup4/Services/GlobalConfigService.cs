using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using Dapper;

namespace SaviSchedular.Services
{
    /// <summary>
    /// DB se global settings load karta hai with 5-minute in-memory cache.
    /// Web.config ke AppSettings ki jagah yeh use hoga.
    /// </summary>
    public static class GlobalConfigService
    {
        private static Dictionary<string, string> _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _lastLoaded = DateTime.MinValue;
        private static readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(5);
        private static readonly object _lock = new object();

        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        /// <summary>
        /// Key ke liye value return karo. Cache expired ho to DB se reload.
        /// </summary>
        public static string Get(string key, string defaultValue = null)
        {
            EnsureLoaded();
            return _cache.TryGetValue(key, out var val) ? val : defaultValue;
        }

        /// <summary>
        /// Cache ko manually invalidate karo (settings update ke baad call karo)
        /// </summary>
        public static void Invalidate()
        {
            lock (_lock) { _lastLoaded = DateTime.MinValue; }
        }

        /// <summary>
        /// Sab settings ek dictionary mein return karo
        /// </summary>
        public static Dictionary<string, string> GetAll()
        {
            EnsureLoaded();
            return new Dictionary<string, string>(_cache, StringComparer.OrdinalIgnoreCase);
        }

        private static void EnsureLoaded()
        {
            if ((DateTime.UtcNow - _lastLoaded) < _cacheDuration) return;
            lock (_lock)
            {
                if ((DateTime.UtcNow - _lastLoaded) < _cacheDuration) return;
                Reload();
            }
        }

        public static void Reload()
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var rows = conn.Query("SELECT ConfigKey, ConfigValue FROM SchedulerGlobalConfig");
                    var tmp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var r in rows)
                        tmp[(string)r.ConfigKey] = (string)r.ConfigValue;
                    _cache = tmp;
                    _lastLoaded = DateTime.UtcNow;
                    Console.WriteLine($"[GlobalConfigService] {tmp.Count} settings loaded from DB.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GlobalConfigService] ERROR loading config: {ex.Message}");
            }
        }
    }
}
