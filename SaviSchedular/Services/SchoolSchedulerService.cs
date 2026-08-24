using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using Hangfire;
using SaviSchedular.Models;

namespace SaviSchedular.Services
{
    /// <summary>
    /// Universal Scheduler Service — Hangfire jobs register/execute karta hai.
    /// Multiple job types, per-school API config, timezone support, DB logging.
    /// </summary>
    public class SchoolSchedulerService
    {
        /// <summary>SaviSchedular DB — scheduler tables</summary>
        private static string SchedConn
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        /// <summary>Production DB — SchoolCalendar holiday check ke liye</summary>
        private static string ProdConn
            => ConfigurationManager.ConnectionStrings["DefaultConnection"]?.ConnectionString;

        // ═════════════════════════════════════════════════════════════════════
        // STARTUP — DB se sabhi active jobs load karo aur Hangfire mein register
        // ═════════════════════════════════════════════════════════════════════
        public static void RegisterAllJobsFromDb()
        {
            try
            {
                using (var conn = new SqlConnection(SchedConn))
                {
                    conn.Open();
                    var instances = conn.Query<SchedulerJobInstanceModel>(@"
                        SELECT InstanceId, SchoolId, JobTypeCode,
                               ScheduledHour, ScheduledMinute, TimeZone, IsActive,
                               RunOnHolidays, MisfireThresholdMinutes
                        FROM   SchedulerJobInstances
                        WHERE  IsActive = 1
                        ORDER  BY SchoolId, JobTypeCode").AsList();

                    Console.WriteLine($"[SaviSchedular] {instances.Count} active job(s) DB se load hue.");

                    foreach (var inst in instances)
                        RegisterJob(inst);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaviSchedular] STARTUP ERROR: {ex.Message}");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // REGISTER — Ek job ko Hangfire mein add/update karo
        // ═════════════════════════════════════════════════════════════════════
        public static void RegisterJob(SchedulerJobInstanceModel inst)
        {
            try
            {
                var tz = SafeGetTimezone(inst.TimeZone);
                string jobId = GetJobId(inst.SchoolId, inst.JobTypeCode);

                RecurringJob.AddOrUpdate(
                    jobId,
                    () => ExecuteJobAsync(inst.SchoolId, inst.JobTypeCode, false),
                    Cron.Daily(inst.ScheduledHour, inst.ScheduledMinute),
                    tz
                );

                Console.WriteLine(
                    $"[SaviSchedular] ✓ Registered: School {inst.SchoolId} | {inst.JobTypeCode} → " +
                    $"{inst.ScheduledHour:D2}:{inst.ScheduledMinute:D2} [{inst.TimeZone}]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaviSchedular] RegisterJob ERROR (School {inst.SchoolId} | {inst.JobTypeCode}): {ex.Message}");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // REMOVE — Job ko Hangfire se remove karo
        // ═════════════════════════════════════════════════════════════════════
        public static void RemoveJob(long schoolId, string jobTypeCode)
        {
            string jobId = GetJobId(schoolId, jobTypeCode);
            RecurringJob.RemoveIfExists(jobId);
            Console.WriteLine($"[SaviSchedular] ✗ Removed: School {schoolId} | {jobTypeCode}");
        }

        // ═════════════════════════════════════════════════════════════════════
        // EXECUTE — Main job executor (Hangfire yahi call karta hai)
        // ═════════════════════════════════════════════════════════════════════
        public static async Task ExecuteJobAsync(long schoolId, string jobTypeCode, bool isManual)
        {
            string triggerType = isManual ? "MANUAL" : "SCHEDULED";
            long   logId       = 0;
            string schoolName  = $"School-{schoolId}";

            try
            {
                // ── Step 1: Job instance aur job type info load karo ──────────
                SchedulerJobInstanceModel inst  = null;
                string defaultApiPath           = null;

                using (var conn = new SqlConnection(SchedConn))
                {
                    conn.Open();
                    inst = conn.QueryFirstOrDefault<SchedulerJobInstanceModel>(@"
                        SELECT InstanceId, SchoolId, JobTypeCode,
                               ScheduledHour, ScheduledMinute, TimeZone, IsActive,
                               RunOnHolidays, MisfireThresholdMinutes
                        FROM   SchedulerJobInstances
                        WHERE  SchoolId = @SchoolId AND JobTypeCode = @JobTypeCode",
                        new { SchoolId = schoolId, JobTypeCode = jobTypeCode });

                    defaultApiPath = conn.ExecuteScalar<string>(@"
                        SELECT DefaultApiPath FROM SchedulerJobTypes
                        WHERE JobTypeCode = @JobTypeCode",
                        new { JobTypeCode = jobTypeCode });
                }

                // ── Step 2: Execution log start karo ─────────────────────────
                logId = LoggingService.StartExecutionLog(schoolId, schoolName, jobTypeCode, triggerType);

                // ── Step 3: Validate ──────────────────────────────────────────
                if (inst == null)
                {
                    Console.WriteLine($"[SaviSchedular] School {schoolId} | {jobTypeCode}: Instance not found. Skipping.");
                    LoggingService.CompleteExecutionLog(logId, "SKIPPED", skipReason: "NO_INSTANCE");
                    return;
                }

                if (!inst.IsActive)
                {
                    Console.WriteLine($"[SaviSchedular] School {schoolId} | {jobTypeCode}: Inactive. Skipping.");
                    LoggingService.CompleteExecutionLog(logId, "SKIPPED", skipReason: "INACTIVE");
                    return;
                }

                // ── Step 4: Misfire check (only for scheduled runs) ───────────
                if (!isManual)
                {
                    var tz       = SafeGetTimezone(inst.TimeZone);
                    var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
                    var scheduled= nowLocal.Date.AddHours(inst.ScheduledHour).AddMinutes(inst.ScheduledMinute);
                    double gapMin= Math.Abs((nowLocal - scheduled).TotalMinutes);

                    if (gapMin > inst.MisfireThresholdMinutes)
                    {
                        Console.WriteLine(
                            $"[SaviSchedular] MISFIRE SKIP: School {schoolId} | {jobTypeCode}. " +
                            $"Scheduled {inst.ScheduledHour:D2}:{inst.ScheduledMinute:D2}, " +
                            $"Now {nowLocal:HH:mm}, Gap {gapMin:F1}m > {inst.MisfireThresholdMinutes}m");
                        LoggingService.CompleteExecutionLog(logId, "SKIPPED", skipReason: "MISFIRE");
                        return;
                    }

                    // ── Step 5: Holiday check ─────────────────────────────────
                    if (!inst.RunOnHolidays && IsHolidayCheckEnabled())
                    {
                        bool isHoliday = CheckHoliday(schoolId, nowLocal.Date);
                        if (isHoliday)
                        {
                            Console.WriteLine(
                                $"[SaviSchedular] HOLIDAY SKIP: School {schoolId} | {jobTypeCode} on {nowLocal:yyyy-MM-dd}");
                            LoggingService.CompleteExecutionLog(logId, "SKIPPED", skipReason: "HOLIDAY");
                            TrySendHolidayEmail(schoolId, schoolName, nowLocal.Date);
                            return;
                        }
                    }
                }

                // ── Step 6: API config load karo ─────────────────────────────
                string baseUrl    = null;
                string apiPath    = null;
                string httpMethod = "POST";
                int    timeout    = 15;

                using (var conn = new SqlConnection(SchedConn))
                {
                    conn.Open();
                    var cfg = conn.QueryFirstOrDefault<SchoolApiConfigModel>(@"
                        SELECT BaseUrl, ApiPath, HttpMethod, TimeoutMinutes
                        FROM   SchoolApiConfigs
                        WHERE  SchoolId = @SchoolId AND JobTypeCode = @JobTypeCode AND IsActive = 1",
                        new { SchoolId = schoolId, JobTypeCode = jobTypeCode });

                    if (cfg != null)
                    {
                        baseUrl    = cfg.BaseUrl;
                        apiPath    = cfg.ApiPath ?? defaultApiPath;
                        httpMethod = cfg.HttpMethod ?? "POST";
                        timeout    = cfg.TimeoutMinutes > 0 ? cfg.TimeoutMinutes : 15;
                    }
                }

                // Fallback: Global config → default API path from job type
                if (string.IsNullOrWhiteSpace(baseUrl))
                    baseUrl = GlobalConfigService.Get("DefaultBaseUrl", "http://localhost:44548/");
                if (string.IsNullOrWhiteSpace(apiPath))
                    apiPath = defaultApiPath ?? string.Empty;

                string fullUrl = $"{baseUrl.TrimEnd('/')}/{(apiPath ?? "").TrimStart('/')}";
                if (fullUrl.IndexOf("targetSchoolId=", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string sep = fullUrl.Contains("?") ? "&" : "?";
                    fullUrl += $"{sep}targetSchoolId={schoolId}";
                }

                // ── Step 7: API call karo ─────────────────────────────────────
                Console.WriteLine(
                    $"[SaviSchedular] → Executing [{jobTypeCode}] School {schoolId} ({schoolName}) | {fullUrl}");

                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(timeout);

                    HttpResponseMessage response =
                        httpMethod.ToUpperInvariant() == "GET"
                            ? await client.GetAsync(fullUrl)
                            : await client.PostAsync(fullUrl, null);

                    string body = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine(
                            $"[SaviSchedular] ✓ SUCCESS: School {schoolId} | {jobTypeCode} | HTTP {(int)response.StatusCode}");
                        LoggingService.CompleteExecutionLog(logId, "SUCCESS", fullUrl,
                            (int)response.StatusCode, body);
                    }
                    else
                    {
                        string err = $"HTTP {(int)response.StatusCode}: {body}";
                        Console.WriteLine($"[SaviSchedular] ✗ FAILED: School {schoolId} | {jobTypeCode} | {err}");
                        LoggingService.CompleteExecutionLog(logId, "FAILED", fullUrl,
                            (int)response.StatusCode, body, err);
                        throw new Exception($"API call failed — {err}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaviSchedular] ✗ EXCEPTION: School {schoolId} | {jobTypeCode} | {ex.Message}");
                if (logId > 0)
                    LoggingService.CompleteExecutionLog(logId, "FAILED", errorMessage: ex.Message);
                throw; // Hangfire retry ke liye rethrow
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // HELPERS
        // ─────────────────────────────────────────────────────────────────────

        public static string GetJobId(long schoolId, string jobTypeCode)
            => $"school-{schoolId}-{jobTypeCode?.ToLower()}";

        private static TimeZoneInfo SafeGetTimezone(string tzName)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(tzName ?? "India Standard Time"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        }

        private static bool IsHolidayCheckEnabled()
        {
            string val = GlobalConfigService.Get("HolidayCheckEnabled", "true");
            return val?.ToLower() == "true";
        }

        private static bool CheckHoliday(long schoolId, DateTime date)
        {
            try
            {
                if (string.IsNullOrEmpty(ProdConn)) return false;
                using (var conn = new SqlConnection(ProdConn))
                {
                    conn.Open();
                    return conn.ExecuteScalar<bool>(@"
                        SELECT CASE WHEN COUNT(1) > 0 THEN 1 ELSE 0 END
                        FROM   SchoolCalendar
                        WHERE  schoolId = @SchoolId AND delFlg = 0 AND holiday = 1
                          AND (
                            CAST(calendarDate AS DATE) = @Date
                            OR (@Date BETWEEN CAST(calendarDate AS DATE)
                                         AND CAST(COALESCE(calendarDateTo, calendarDate) AS DATE))
                          )",
                        new { SchoolId = schoolId, Date = date });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaviSchedular] Holiday check error School {schoolId}: {ex.Message}. Proceeding.");
                return false;
            }
        }

        private static void TrySendHolidayEmail(long schoolId, string schoolName, DateTime date)
        {
            try
            {
                string host     = GlobalConfigService.Get("SMTPHost",     "email-smtp.ap-south-1.amazonaws.com");
                int    port     = int.TryParse(GlobalConfigService.Get("SMTPPort", "587"), out int p) ? p : 587;
                string sender   = GlobalConfigService.Get("SMTPSender",   "info@savischools.com");
                string username = GlobalConfigService.Get("SMTPUsername", "");
                string password = GlobalConfigService.Get("SMTPPassword", "");
                string toEmail  = GlobalConfigService.Get("NotificationEmail", "admin@savischools.com");

                using (var mail = new System.Net.Mail.MailMessage())
                {
                    mail.From    = new System.Net.Mail.MailAddress(sender, "SaviSchedular");
                    mail.To.Add(toEmail);
                    mail.Subject = $"[SaviSchedular] Holiday Alert: {schoolName} (ID: {schoolId})";
                    mail.Body    =
                        $"Dear Admin,\n\n" +
                        $"Today ({date:yyyy-MM-dd}) is a holiday for {schoolName} (School ID: {schoolId}).\n" +
                        $"The scheduled job was automatically skipped.\n\n" +
                        $"Regards,\nSaviSchedular";

                    using (var smtp = new System.Net.Mail.SmtpClient(host, port))
                    {
                        smtp.Credentials = new System.Net.NetworkCredential(username, password);
                        smtp.EnableSsl   = true;
                        smtp.Send(mail);
                    }
                }
                Console.WriteLine($"[SaviSchedular] Holiday email sent for School {schoolId}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaviSchedular] Holiday email error: {ex.Message}");
            }
        }
    }
}
