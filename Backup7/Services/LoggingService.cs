using System;
using System.Configuration;
using System.Data.SqlClient;
using Dapper;
using Newtonsoft.Json;
using SaviSchedular.Models;

namespace SaviSchedular.Services
{
    public static class LoggingService
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        // ─────────────────────────────────────────────────────────────────────
        // Start log entry when job begins
        // ─────────────────────────────────────────────────────────────────────
        public static long StartExecutionLog(SchedulerJobInstanceModel inst, string triggerType = "SCHEDULED", string hangfireJobId = null)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    return conn.ExecuteScalar<long>(@"
                        INSERT INTO SchedulerExecutionLogs
                            (InstanceId, ClientId, ProductId, ClientName, ExternalId, JobTypeCode,
                             TriggerType, StartedAt, Status, HangfireJobId)
                        VALUES
                            (@InstanceId, @ClientId, @ProductId, @ClientName, @ExternalId, @JobTypeCode,
                             @TriggerType, @StartedAt, 'RUNNING', @HangfireJobId);
                        SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                        new {
                            InstanceId    = inst.InstanceId,
                            ClientId      = inst.ClientId,
                            ProductId     = inst.ProductId,
                            ClientName    = inst.ClientName,
                            ExternalId    = inst.ExternalId,
                            JobTypeCode   = inst.JobTypeCode,
                            TriggerType   = triggerType ?? "SCHEDULED",
                            StartedAt     = DateTime.Now,
                            HangfireJobId = hangfireJobId
                        });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoggingService] StartLog ERROR: {ex.Message}");
                return 0;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Update log entry when job completes
        // ─────────────────────────────────────────────────────────────────────
        public static void CompleteExecutionLog(long logId, string status, string apiUrl = null,
            int? httpStatusCode = null, string responseBody = null,
            string errorMessage = null, string skipReason = null, string payloadSent = null)
        {
            if (logId <= 0) return;
            try
            {
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
                            PayloadSent     = @PayloadSent,
                            HttpStatusCode  = @HttpStatusCode,
                            ResponseBody    = @ResponseBody,
                            ErrorMessage    = @ErrorMessage,
                            SkipReason      = @SkipReason
                        WHERE LogId = @LogId",
                        new {
                            LogId = logId, CompletedAt = now, Status = status, ApiUrl = apiUrl,
                            PayloadSent = payloadSent, HttpStatusCode = httpStatusCode,
                            ResponseBody = responseBody, ErrorMessage = errorMessage, SkipReason = skipReason
                        });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoggingService] CompleteLog ERROR: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Audit log — config/schedule changes
        // ─────────────────────────────────────────────────────────────────────
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
                        new {
                            TableName = tableName, RecordId  = recordId, Action = action,
                            OldValues = oldValues != null ? JsonConvert.SerializeObject(oldValues) : null,
                            NewValues = newValues != null ? JsonConvert.SerializeObject(newValues) : null,
                            ChangedBy = changedBy ?? "System",
                            IPAddress = ipAddress, Notes = notes, ChangedAt = DateTime.Now
                        });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LoggingService] AuditLog ERROR: {ex.Message}");
            }
        }
    }
}
