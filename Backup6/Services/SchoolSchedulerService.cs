using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Hangfire;
using Newtonsoft.Json;
using SaviSchedular.Models;
using SaviSchedular.Services.Security;

namespace SaviSchedular.Services
{
    /// <summary>
    /// Universal Scheduler Service v2.0
    /// Executes jobs for any Product/Client/JobType combination.
    /// Token auth, PayloadJson POST body, Hangfire integration.
    /// </summary>
    public class SchoolSchedulerService
    {
        private static string SchedConn
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        // ═════════════════════════════════════════════════════════════════════
        // STARTUP — Load all active jobs from DB and register in Hangfire
        // ═════════════════════════════════════════════════════════════════════
        public static void RegisterAllJobsFromDb()
        {
            try
            {
                using (var conn = new SqlConnection(SchedConn))
                {
                    conn.Open();
                    var instances = conn.Query<long>(@"
                        SELECT InstanceId FROM SchedulerJobInstances WHERE IsActive=1").AsList();

                    Console.WriteLine($"[SaviSchedular v2] {instances.Count} active job(s) found in DB.");
                    foreach (var instanceId in instances)
                        RegisterJobByInstanceId(instanceId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaviSchedular v2] STARTUP ERROR: {ex.Message}");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // REGISTER — Load instance from DB and add to Hangfire
        // ═════════════════════════════════════════════════════════════════════
        public static void RegisterJobByInstanceId(long instanceId)
        {
            try
            {
                var inst = LoadInstance(instanceId);
                if (inst == null)
                {
                    Console.WriteLine($"[SaviSchedular v2] RegisterJob: InstanceId {instanceId} not found.");
                    return;
                }

                var tz    = SafeGetTimezone(inst.TimeZone);
                string jobId = GetJobId(instanceId);

                RecurringJob.AddOrUpdate(
                    jobId,
                    () => ExecuteJobAsync(instanceId, false),
                    Cron.Daily(inst.ScheduledHour, inst.ScheduledMinute),
                    tz
                );

                Console.WriteLine(
                    $"[SaviSchedular v2] ✓ Registered: [{inst.ProductCode}] {inst.ClientName} ({inst.ExternalId}) | " +
                    $"{inst.JobTypeCode} → {inst.ScheduledHour:D2}:{inst.ScheduledMinute:D2} [{inst.TimeZone}]");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaviSchedular v2] RegisterJob ERROR (Instance {instanceId}): {ex.Message}");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // REMOVE — Remove a job from Hangfire
        // ═════════════════════════════════════════════════════════════════════
        public static void RemoveJob(long instanceId)
        {
            string jobId = GetJobId(instanceId);
            RecurringJob.RemoveIfExists(jobId);
            Console.WriteLine($"[SaviSchedular v2] ✗ Removed: InstanceId {instanceId}");
        }

        // ═════════════════════════════════════════════════════════════════════
        // EXECUTE — Main job executor (called by Hangfire)
        // Disable automatic retries so failed jobs don't re-trigger after 3 mins
        // ═════════════════════════════════════════════════════════════════════
        [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
        public static async Task ExecuteJobAsync(long instanceId, bool isManual)
        {
            string triggerType = isManual ? "MANUAL" : "SCHEDULED";
            long   logId       = 0;

            SchedulerJobInstanceModel inst = null;

            try
            {
                // ── Step 1: Load full instance with joined data ───────────────
                inst = LoadInstance(instanceId);

                if (inst == null)
                {
                    Console.WriteLine($"[SaviSchedular v2] Instance {instanceId} not found. Aborting.");
                    return;
                }

                // ── Step 2: Start log ─────────────────────────────────────────
                logId = LoggingService.StartExecutionLog(inst, triggerType);

                // ── Step 3: Validate active ───────────────────────────────────
                if (!inst.IsActive)
                {
                    LoggingService.CompleteExecutionLog(logId, "SKIPPED", skipReason: "INACTIVE");
                    return;
                }

                // ── Step 4: Scheduled Run Checks (scheduled runs only) ─────────
                if (!isManual)
                {
                    var tz       = SafeGetTimezone(inst.TimeZone);
                    var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
                    var scheduled= nowLocal.Date.AddHours(inst.ScheduledHour).AddMinutes(inst.ScheduledMinute);
                    double gapMin= Math.Abs((nowLocal - scheduled).TotalMinutes);

                    // Tight misfire check: if gap is > 3 minutes, skip late run
                    if (gapMin > 3)
                    {
                        Console.WriteLine($"[SaviSchedular v2] MISFIRE SKIP: Instance {instanceId}. Gap {gapMin:F1}m.");
                        LoggingService.CompleteExecutionLog(logId, "SKIPPED", skipReason: $"MISFIRE (Gap {gapMin:F1}m)");
                        return;
                    }

                    // Check if already executed or running today (using C# local date range to prevent RDS timezone mismatch)
                    using (var conn = new SqlConnection(SchedConn))
                    {
                        DateTime todayStart = DateTime.Now.Date;
                        DateTime todayEnd   = todayStart.AddDays(1);

                        bool alreadyRanToday = conn.ExecuteScalar<bool>(@"
                            SELECT CASE WHEN EXISTS (
                                SELECT 1 FROM SchedulerExecutionLogs
                                WHERE InstanceId = @InstanceId
                                  AND Status IN ('SUCCESS', 'RUNNING')
                                  AND StartedAt >= @TodayStart AND StartedAt < @TodayEnd
                                  AND LogId != @CurrentLogId
                            ) THEN 1 ELSE 0 END",
                            new { InstanceId = instanceId, TodayStart = todayStart, TodayEnd = todayEnd, CurrentLogId = logId });

                        if (alreadyRanToday)
                        {
                            Console.WriteLine($"[SaviSchedular v2] ALREADY EXECUTED TODAY: Instance {instanceId}. Skipping duplicate run.");
                            LoggingService.CompleteExecutionLog(logId, "SKIPPED", skipReason: "ALREADY_EXECUTED_TODAY");
                            return;
                        }
                    }
                }

                // ── Step 5: Build final URL ───────────────────────────────────
                string baseUrl  = inst.CustomBaseUrl ?? inst.BaseUrl;
                string apiPath  = inst.CustomApiPath ?? inst.DefaultApiPath ?? string.Empty;
                string fullUrl  = $"{baseUrl.TrimEnd('/')}/{apiPath.TrimStart('/')}";

                // ── Step 6: Build payload ─────────────────────────────────────
                // Merge PayloadJson + ExternalId auto-injection
                string payloadJson = BuildPayload(inst);

                // ── Step 7: Resolve auth token & In-Memory JWT Cache ─────────
                string authType   = inst.AuthType ?? "Bearer";
                string tokenType  = inst.TokenType ?? "Bearer";
                string headerName = inst.TokenHeaderName ?? "Authorization";
                
                // Decrypt client secret if present
                string decryptedSecret = !string.IsNullOrEmpty(inst.ClientSecret) 
                    ? EncryptionHelper.Decrypt(inst.ClientSecret) 
                    : null;

                // Resolve token dynamically: RS256 Asymmetric JWT or OAuth2 RAM Cache
                string token = null;
                if (authType == "RS256" && !string.IsNullOrWhiteSpace(inst.RsaPrivateKey))
                {
                    string decryptedRsaPrivate = EncryptionHelper.Decrypt(inst.RsaPrivateKey);
                    token = Rs256JwtService.GenerateRs256JwtToken(
                        decryptedRsaPrivate,
                        inst.Issuer ?? "SaviScheduler",
                        inst.Audience ?? inst.ProductCode ?? "SaviSchools",
                        expiryMinutes: 2);
                }
                else
                {
                    token = await JwtTokenManager.GetValidTokenInternalAsync(
                        inst.ProductId, inst.TokenUrl, inst.OAuthClientId, decryptedSecret,
                        inst.CustomApiToken ?? inst.ApiToken);
                }

                // Strict Security Rule: Without valid token, NO API call is allowed
                if (string.IsNullOrWhiteSpace(token))
                {
                    string noTokenError = "Security Violation: API execution blocked because no authentication token or security credentials were provided.";
                    Console.WriteLine($"[SaviSchedular v2] ✗ REJECTED: Instance {instanceId} | {noTokenError}");
                    LoggingService.CompleteExecutionLog(logId, "FAILED", fullUrl, 401, null, noTokenError, payloadSent: payloadJson);
                    throw new Exception(noTokenError);
                }

                // Enforce HTTPS check for remote URLs when using JWT/OAuth2 authentication
                if ((authType == "OAuth2" || authType == "Bearer") && 
                    !fullUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && 
                    !fullUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) &&
                    !fullUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase))
                {
                    string httpWarning = $"Security Error: HTTPS is enforced for JWT authentication. Target URL '{fullUrl}' is insecure.";
                    Console.WriteLine($"[SaviSchedular v2] ✗ REJECTED: Instance {instanceId} | {httpWarning}");
                    LoggingService.CompleteExecutionLog(logId, "FAILED", fullUrl, 400, null, httpWarning, payloadSent: payloadJson);
                    throw new Exception(httpWarning);
                }

                // ── Step 8: Make HTTP call with 401 Single Retry ─────────────
                Console.WriteLine(
                    $"[SaviSchedular v2] → [{inst.ProductCode}] {inst.ClientName} ({inst.ExternalId}) | {inst.JobTypeCode} | {fullUrl}");

                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(15);

                    Action attachAuthHeader = () =>
                    {
                        client.DefaultRequestHeaders.Authorization = null;
                        if (!string.IsNullOrEmpty(token))
                        {
                            if (tokenType == "Bearer" || authType == "OAuth2" || authType == "Bearer")
                                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                            else if (tokenType == "Basic" || authType == "Basic")
                                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                                    Convert.ToBase64String(Encoding.UTF8.GetBytes(token)));
                            else
                                client.DefaultRequestHeaders.Add(headerName, token);
                        }
                    };

                    attachAuthHeader();

                    HttpResponseMessage response;
                    string httpMethod = inst.HttpMethod?.ToUpperInvariant() ?? "POST";

                    Func<Task<HttpResponseMessage>> executeHttpCall = async () =>
                    {
                        if (httpMethod == "GET")
                        {
                            string targetUrl = fullUrl;
                            if (!targetUrl.Contains("externalId=") && !targetUrl.Contains("targetSchoolId="))
                            {
                                string sep = targetUrl.Contains("?") ? "&" : "?";
                                targetUrl += $"{sep}targetId={Uri.EscapeDataString(inst.ExternalId ?? "")}";
                            }
                            return await client.GetAsync(targetUrl);
                        }
                        else
                        {
                            var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
                            return await client.PostAsync(fullUrl, content);
                        }
                    };

                    response = await executeHttpCall();

                    // 401 Single Retry Logic: Regenerate Token / Invalidate RAM Token -> Retry ONCE
                    if (response.StatusCode == HttpStatusCode.Unauthorized)
                    {
                        Console.WriteLine($"[SaviSchedular v2] ⚠️ 401 Unauthorized received for Instance {instanceId}. Regenerating fresh token and retrying ONCE...");
                        
                        if (authType == "RS256" && !string.IsNullOrWhiteSpace(inst.RsaPrivateKey))
                        {
                            string decryptedRsaPrivate = EncryptionHelper.Decrypt(inst.RsaPrivateKey);
                            token = Rs256JwtService.GenerateRs256JwtToken(
                                decryptedRsaPrivate,
                                inst.Issuer ?? "SaviScheduler",
                                inst.Audience ?? inst.ProductCode ?? "SaviSchools",
                                expiryMinutes: 2);
                        }
                        else
                        {
                            JwtTokenManager.InvalidateToken(inst.ProductId);
                            token = await JwtTokenManager.GetValidTokenInternalAsync(
                                inst.ProductId, inst.TokenUrl, inst.OAuthClientId, decryptedSecret,
                                inst.CustomApiToken ?? inst.ApiToken);
                        }

                        attachAuthHeader();
                        response = await executeHttpCall();

                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            string authFailed = "Authentication Failed: 401 Unauthorized returned twice. Retry stopped to prevent DoS loop.";
                            Console.WriteLine($"[SaviSchedular v2] ✗ STOPPED: Instance {instanceId} | {authFailed}");
                            LoggingService.CompleteExecutionLog(logId, "FAILED", fullUrl, (int)response.StatusCode, "Authorization: ********", authFailed, payloadSent: payloadJson);
                            TrySendFailureEmail(inst, fullUrl, authFailed);
                            throw new Exception(authFailed);
                        }
                    }

                    string body = await response.Content.ReadAsStringAsync();

                    if (response.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"[SaviSchedular v2] ✓ SUCCESS: Instance {instanceId} | HTTP {(int)response.StatusCode}");
                        LoggingService.CompleteExecutionLog(logId, "SUCCESS", fullUrl,
                            (int)response.StatusCode, SecureLogger.Sanitize(body), payloadSent: payloadJson);
                    }
                    else
                    {
                        string err = $"HTTP {(int)response.StatusCode}: {SecureLogger.Sanitize(body)}";
                        Console.WriteLine($"[SaviSchedular v2] ✗ FAILED: Instance {instanceId} | {err}");
                        LoggingService.CompleteExecutionLog(logId, "FAILED", fullUrl,
                            (int)response.StatusCode, SecureLogger.Sanitize(body), err, payloadSent: payloadJson);
                        TrySendFailureEmail(inst, fullUrl, err);
                        throw new Exception($"API call failed — {err}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaviSchedular v2] ✗ EXCEPTION: Instance {instanceId} | {ex.Message}");
                if (logId > 0)
                    LoggingService.CompleteExecutionLog(logId, "FAILED", errorMessage: ex.Message);
                if (inst != null && !ex.Message.StartsWith("API call failed —"))
                    TrySendFailureEmail(inst, null, ex.Message);
                throw; // Hangfire retry
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // HELPERS
        // ─────────────────────────────────────────────────────────────────────

        public static string GetJobId(long instanceId) => $"savi-instance-{instanceId}";

        private static void EnsureJobInstancesSchema(SqlConnection conn)
        {
            try
            {
                string sql = @"
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('SchedulerJobInstances') AND name = 'LastStatus')
                    BEGIN
                        ALTER TABLE [dbo].[SchedulerJobInstances] ADD 
                            [LastStatus] NVARCHAR(50) NULL,
                            [LastRunAt]  DATETIME     NULL;
                    END";
                conn.Execute(sql);
            }
            catch { }
        }

        private static SchedulerJobInstanceModel LoadInstance(long instanceId)
        {
            using (var conn = new SqlConnection(SchedConn))
            {
                conn.Open();
                EnsureJobInstancesSchema(conn);
                return conn.QueryFirstOrDefault<SchedulerJobInstanceModel>(@"
                    SELECT
                        ji.InstanceId, ji.ClientId, ji.ProductId, ji.JobTypeId, ji.CustomApiPath, ji.CustomApiToken, ji.PayloadJson,
                        ji.ScheduledHour, ji.ScheduledMinute, ji.TimeZone, ji.IsActive, ji.RunOnHolidays, ji.MisfireThresholdMinutes, ji.LastStatus, ji.LastRunAt, ji.CreatedAt, ji.UpdatedAt, ji.CreatedBy,
                        pc.ClientName, pc.ExternalId, pc.CustomBaseUrl,
                        p.ProductName, p.ProductCode, p.BaseUrl, p.ApiToken, p.TokenType, p.TokenHeaderName,
                        p.AuthType, p.TokenUrl, p.ClientId AS OAuthClientId, p.ClientSecret, p.RsaPrivateKey, p.RsaPublicKey, p.Audience, p.Issuer,
                        jt.JobTypeCode, jt.JobTypeName, jt.DefaultApiPath, jt.HttpMethod
                    FROM SchedulerJobInstances ji
                    JOIN ProductClients pc  ON pc.ClientId  = ji.ClientId
                    JOIN Products p         ON p.ProductId  = ji.ProductId
                    JOIN ProductJobTypes jt ON jt.JobTypeId = ji.JobTypeId
                    WHERE ji.InstanceId = @InstanceId",
                    new { InstanceId = instanceId });
            }
        }

        private static string BuildPayload(SchedulerJobInstanceModel inst)
        {
            try
            {
                // Start from instance PayloadJson or empty object
                var dict = string.IsNullOrWhiteSpace(inst.PayloadJson)
                    ? new System.Collections.Generic.Dictionary<string, object>()
                    : JsonConvert.DeserializeObject<System.Collections.Generic.Dictionary<string, object>>(inst.PayloadJson)
                      ?? new System.Collections.Generic.Dictionary<string, object>();

                // Auto-inject targetId / ExternalId if not already present
                if (!dict.ContainsKey("targetId"))      dict["targetId"]      = inst.ExternalId;
                if (!dict.ContainsKey("externalId"))    dict["externalId"]    = inst.ExternalId;
                if (!dict.ContainsKey("productCode"))   dict["productCode"]   = inst.ProductCode;

                return JsonConvert.SerializeObject(dict);
            }
            catch
            {
                return inst.PayloadJson ?? "{}";
            }
        }

        private static TimeZoneInfo SafeGetTimezone(string tzName)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(tzName ?? "India Standard Time"); }
            catch { return TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"); }
        }

        // ─────────────────────────────────────────────────────────────────────
        // FAILURE EMAIL
        // ─────────────────────────────────────────────────────────────────────
        private static void TrySendFailureEmail(SchedulerJobInstanceModel inst, string apiUrl, string errorMessage)
        {
            try
            {
                string host     = GlobalConfigService.Get("SMTPHost",     "email-smtp.ap-south-1.amazonaws.com");
                int    port     = int.TryParse(GlobalConfigService.Get("SMTPPort", "587"), out int p) ? p : 587;
                string sender   = GlobalConfigService.Get("SMTPSender",   "info@savischools.com");
                string username = GlobalConfigService.Get("SMTPUsername", "");
                string password = GlobalConfigService.Get("SMTPPassword", "");
                string toEmail  = GlobalConfigService.Get("NotificationEmail", "admin@savischools.com");
                string failedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                using (var mail = new System.Net.Mail.MailMessage())
                {
                    mail.From       = new System.Net.Mail.MailAddress(sender, "SaviSchedular");
                    mail.To.Add(toEmail);
                    mail.Subject    = $"[SaviSchedular] ⚠ FAILED: [{inst?.ProductCode}] {inst?.ClientName} ({inst?.ExternalId}) | {inst?.JobTypeCode}";
                    mail.IsBodyHtml = true;
                    mail.Body = $@"
<html><body style='font-family:Segoe UI,Arial,sans-serif;color:#222;background:#f4f4f4;padding:20px;'>
  <div style='max-width:600px;margin:0 auto;background:#fff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,.1);'>
    <div style='background:#c0392b;padding:20px 30px;'>
      <h2 style='color:#fff;margin:0;'>⚠ Scheduler Job Failed</h2>
    </div>
    <div style='padding:24px 30px;'>
      <table style='width:100%;border-collapse:collapse;margin:16px 0;'>
        <tr><td style='padding:8px 12px;background:#f9f9f9;font-weight:600;width:140px;border:1px solid #e0e0e0;'>Product</td><td style='padding:8px 12px;border:1px solid #e0e0e0;'>{inst?.ProductName} ({inst?.ProductCode})</td></tr>
        <tr><td style='padding:8px 12px;background:#f9f9f9;font-weight:600;border:1px solid #e0e0e0;'>Client</td><td style='padding:8px 12px;border:1px solid #e0e0e0;'>{inst?.ClientName}</td></tr>
        <tr><td style='padding:8px 12px;background:#f9f9f9;font-weight:600;border:1px solid #e0e0e0;'>External ID</td><td style='padding:8px 12px;border:1px solid #e0e0e0;'>{inst?.ExternalId}</td></tr>
        <tr><td style='padding:8px 12px;background:#f9f9f9;font-weight:600;border:1px solid #e0e0e0;'>Job Type</td><td style='padding:8px 12px;border:1px solid #e0e0e0;'>{inst?.JobTypeCode}</td></tr>
        <tr><td style='padding:8px 12px;background:#f9f9f9;font-weight:600;border:1px solid #e0e0e0;'>Failed At</td><td style='padding:8px 12px;border:1px solid #e0e0e0;'>{failedAt}</td></tr>
        <tr><td style='padding:8px 12px;background:#f9f9f9;font-weight:600;border:1px solid #e0e0e0;'>API URL</td><td style='padding:8px 12px;border:1px solid #e0e0e0;word-break:break-all;'>{(string.IsNullOrEmpty(apiUrl) ? "N/A" : apiUrl)}</td></tr>
        <tr><td style='padding:8px 12px;background:#fff0f0;font-weight:600;border:1px solid #e0e0e0;color:#c0392b;'>Error</td><td style='padding:8px 12px;background:#fff0f0;border:1px solid #e0e0e0;color:#c0392b;'>{System.Security.SecurityElement.Escape(errorMessage ?? "Unknown error")}</td></tr>
      </table>
    </div>
    <div style='background:#f4f4f4;padding:12px 30px;text-align:center;font-size:12px;color:#888;'>
      Automated alert from SaviSchedular v2.0. Do not reply.
    </div>
  </div>
</body></html>";

                    using (var smtp = new System.Net.Mail.SmtpClient(host, port))
                    {
                        smtp.Credentials = new System.Net.NetworkCredential(username, password);
                        smtp.EnableSsl   = true;
                        smtp.Send(mail);
                    }
                }
                Console.WriteLine($"[SaviSchedular v2] Failure email sent.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SaviSchedular v2] Failure email error: {ex.Message}");
            }
        }
    }
}
