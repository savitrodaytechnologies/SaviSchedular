using System;
using System.Configuration;
using System.Data.SqlClient;
using Dapper;
using Newtonsoft.Json;

namespace SaviSchedular.Services
{
    /// <summary>
    /// Execution logs aur audit logs DB mein save karta hai.
    /// Har job run ka start, complete, aur skip track hota hai.
    /// </summary>
    public static class LoggingService
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        // ─────────────────────────────────────────────────────────────────────
        // EXECUTION LOGS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Job start hone par log entry banao, LogId return karo
        /// </summary>
        public static long StartExecutionLog(long schoolId, string schoolName, string jobTypeCode,
            string triggerType, string hangfireJobId = null)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    return conn.ExecuteScalar<long>(@"
                        INSERT INTO SchedulerExecutionLogs
                            (SchoolId, SchoolName, JobTypeCode, TriggerType, StartedAt, Status, HangfireJobId)
                        VALUES
                            (@SchoolId, @SchoolName, @JobTypeCode, @TriggerType, @StartedAt, 'RUNNING', @HangfireJobId);
                        SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                        new { SchoolId = schoolId, SchoolName = schoolName, JobTypeCode = jobTypeCode,
                              TriggerType = triggerType, HangfireJobId = hangfireJobId, StartedAt = DateTime.Now });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoggingService] StartLog ERROR: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Job complete hone par log update karo (success/failed/skipped)
        /// </summary>
        public static void CompleteExecutionLog(long logId, string status, string apiUrl = null,
            int? httpStatusCode = null, string responseBody = null,
            string errorMessage = null, string skipReason = null)
        {
            if (logId <= 0) return;
            try
            {
                // ResponseBody ko 4000 chars tak limit karo
                if (responseBody?.Length > 4000)
                    responseBody = responseBody.Substring(0, 4000) + "...[truncated]";

                var now = DateTime.Now;

                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    conn.Execute(@"
                        UPDATE SchedulerExecutionLogs SET
                            CompletedAt     = @CompletedAt,
                            DurationSeconds = CAST(DATEDIFF(MILLISECOND, StartedAt, @CompletedAt) AS DECIMAL(10,2)) / 1000.0,
                            Status          = @Status,
                            ApiUrl          = @ApiUrl,
                            HttpStatusCode  = @HttpStatusCode,
                            ResponseBody    = @ResponseBody,
                            ErrorMessage    = @ErrorMessage,
                            SkipReason      = @SkipReason
                        WHERE LogId = @LogId",
                        new { LogId = logId, CompletedAt = now, Status = status, ApiUrl = apiUrl,
                              HttpStatusCode = httpStatusCode, ResponseBody = responseBody,
                              ErrorMessage = errorMessage, SkipReason = skipReason });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoggingService] CompleteLog ERROR: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // AUDIT LOGS
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Config change ka audit trail save karo — old vs new values
        /// </summary>
        public static void SaveAuditLog(string tableName, string recordId, string action,
            object oldValues, object newValues, string changedBy = "System",
            string ipAddress = null, string notes = null)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    conn.Execute(@"
                        INSERT INTO SchedulerAuditLogs
                            (TableName, RecordId, Action, OldValues, NewValues, ChangedBy, IPAddress, Notes, ChangedAt)
                        VALUES
                            (@TableName, @RecordId, @Action, @OldValues, @NewValues, @ChangedBy, @IPAddress, @Notes, @ChangedAt)",
                        new
                        {
                            TableName = tableName,
                            RecordId  = recordId,
                            Action    = action,
                            OldValues = oldValues != null ? JsonConvert.SerializeObject(oldValues) : null,
                            NewValues = newValues != null ? JsonConvert.SerializeObject(newValues) : null,
                            ChangedBy = changedBy ?? "System",
                            IPAddress = ipAddress,
                            Notes     = notes,
                            ChangedAt = DateTime.Now
                        });
                }
            }
            catch (Exception ex)
            {
                // Audit log failure se main flow block nahi hona chahiye
                Console.WriteLine($"[LoggingService] AuditLog ERROR: {ex.Message}");
            }
        }
    }
}
