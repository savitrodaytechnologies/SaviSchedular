using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using Dapper;
using Hangfire;
using SaviSchedular.Models;
using SaviSchedular.Services;

namespace SaviSchedular.Controllers
{
    /// <summary>
    /// Manages scheduler job instances (schedule CRUD + dashboard + manual trigger)
    /// </summary>
    [RoutePrefix("api/scheduler")]
    public class SchedulerController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        private string ClientIp
            => (Request.Properties.ContainsKey("MS_HttpContext")
                ? ((System.Web.HttpContextWrapper)Request.Properties["MS_HttpContext"]).Request.UserHostAddress
                : "unknown");

        // ─────────────────────────────────────────────────────────────────────
        // GET /api/scheduler — All schedules (with full joined data)
        // ─────────────────────────────────────────────────────────────────────
        [HttpGet, Route("")]
        public HttpResponseMessage GetAll([FromUri] int? productId = null, [FromUri] long? clientId = null,
            [FromUri] string q = null, [FromUri] bool? isActive = null)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    string sql = @"
                        SELECT
                            ji.*,
                            pc.ClientName, pc.ExternalId, pc.CustomBaseUrl,
                            p.ProductName, p.ProductCode, p.BaseUrl, p.ApiToken, p.TokenType, p.TokenHeaderName,
                            jt.JobTypeCode, jt.JobTypeName, jt.DefaultApiPath, jt.HttpMethod
                        FROM SchedulerJobInstances ji
                        JOIN ProductClients pc  ON pc.ClientId  = ji.ClientId
                        JOIN Products p         ON p.ProductId  = ji.ProductId
                        JOIN ProductJobTypes jt ON jt.JobTypeId = ji.JobTypeId
                        WHERE 1=1";
                    if (productId.HasValue) sql += " AND ji.ProductId = @ProductId";
                    if (clientId.HasValue)  sql += " AND ji.ClientId  = @ClientId";
                    if (isActive.HasValue)  sql += " AND ji.IsActive  = @IsActive";
                    if (!string.IsNullOrWhiteSpace(q))
                        sql += " AND (pc.ClientName LIKE @Q OR pc.ExternalId LIKE @Q OR jt.JobTypeName LIKE @Q)";
                    sql += " ORDER BY p.ProductName, pc.ClientName, jt.JobTypeName";

                    var list = conn.Query<SchedulerJobInstanceModel>(sql,
                        new { ProductId = productId, ClientId = clientId, IsActive = isActive, Q = $"%{q}%" }).AsList();
                    // Mask tokens
                    foreach (var item in list)
                    {
                        if (!string.IsNullOrEmpty(item.ApiToken))      item.ApiToken      = "••••••••";
                        if (!string.IsNullOrEmpty(item.CustomApiToken)) item.CustomApiToken = "••••••••";
                    }
                    return Request.CreateResponse(HttpStatusCode.OK, list);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /api/scheduler/save — Create or update a schedule
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost, Route("save")]
        public HttpResponseMessage Save([FromBody] SaveScheduleRequest req)
        {
            if (req == null || req.ClientId <= 0 || req.JobTypeId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "ClientId and JobTypeId are required." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();

                    // Get ProductId from client if not supplied
                    if (req.ProductId <= 0)
                        req.ProductId = conn.ExecuteScalar<int>("SELECT ProductId FROM ProductClients WHERE ClientId=@Id", new { Id = req.ClientId });

                    if (req.InstanceId == 0)
                    {
                        var newId = conn.ExecuteScalar<long>(@"
                            INSERT INTO SchedulerJobInstances
                                (ClientId, ProductId, JobTypeId, CustomApiPath, CustomApiToken, PayloadJson,
                                 ScheduledHour, ScheduledMinute, TimeZone, IsActive, RunOnHolidays, MisfireThresholdMinutes,
                                 CreatedAt, UpdatedAt, CreatedBy)
                            VALUES
                                (@ClientId, @ProductId, @JobTypeId, @CustomApiPath, @CustomApiToken, @PayloadJson,
                                 @ScheduledHour, @ScheduledMinute, @TimeZone, @IsActive, @RunOnHolidays, @MisfireThresholdMinutes,
                                 @Now, @Now, @By);
                            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                            new {
                                req.ClientId, req.ProductId, req.JobTypeId,
                                req.CustomApiPath, req.CustomApiToken, req.PayloadJson,
                                req.ScheduledHour, req.ScheduledMinute,
                                TimeZone = req.TimeZone ?? "India Standard Time",
                                req.IsActive, req.RunOnHolidays,
                                MisfireThresholdMinutes = req.MisfireThresholdMinutes > 0 ? req.MisfireThresholdMinutes : 15,
                                Now = DateTime.Now, By = "Admin"
                            });

                        // Register in Hangfire
                        if (req.IsActive)
                            SchoolSchedulerService.RegisterJobByInstanceId(newId);

                        LoggingService.SaveAuditLog("SchedulerJobInstances", newId.ToString(), "INSERT", null, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { instanceId = newId, message = "Schedule created." });
                    }
                    else
                    {
                        var old = conn.QueryFirstOrDefault("SELECT * FROM SchedulerJobInstances WHERE InstanceId=@Id", new { Id = req.InstanceId });

                        // Preserve existing token if masked placeholder sent
                        string tokenToSave = (req.CustomApiToken == "••••••••" || string.IsNullOrEmpty(req.CustomApiToken))
                            ? conn.ExecuteScalar<string>("SELECT CustomApiToken FROM SchedulerJobInstances WHERE InstanceId=@Id", new { Id = req.InstanceId })
                            : req.CustomApiToken;

                        conn.Execute(@"
                            UPDATE SchedulerJobInstances SET
                                CustomApiPath           = @CustomApiPath,
                                CustomApiToken          = @CustomApiToken,
                                PayloadJson             = @PayloadJson,
                                ScheduledHour           = @ScheduledHour,
                                ScheduledMinute         = @ScheduledMinute,
                                TimeZone                = @TimeZone,
                                IsActive                = @IsActive,
                                RunOnHolidays           = @RunOnHolidays,
                                MisfireThresholdMinutes = @MisfireThresholdMinutes,
                                UpdatedAt               = @Now
                            WHERE InstanceId = @InstanceId",
                            new {
                                req.CustomApiPath, CustomApiToken = tokenToSave, req.PayloadJson,
                                req.ScheduledHour, req.ScheduledMinute,
                                TimeZone = req.TimeZone ?? "India Standard Time",
                                req.IsActive, req.RunOnHolidays,
                                MisfireThresholdMinutes = req.MisfireThresholdMinutes > 0 ? req.MisfireThresholdMinutes : 15,
                                Now = DateTime.Now, req.InstanceId
                            });

                        // Re-register or remove from Hangfire
                        if (req.IsActive)
                            SchoolSchedulerService.RegisterJobByInstanceId(req.InstanceId);
                        else
                            SchoolSchedulerService.RemoveJob(req.InstanceId);

                        LoggingService.SaveAuditLog("SchedulerJobInstances", req.InstanceId.ToString(), "UPDATE", old, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { message = "Schedule updated." });
                    }
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // DELETE /api/scheduler/{instanceId}
        // ─────────────────────────────────────────────────────────────────────
        [HttpDelete, Route("{instanceId:long}")]
        public HttpResponseMessage Delete(long instanceId)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    conn.Execute("DELETE FROM SchedulerJobInstances WHERE InstanceId=@Id", new { Id = instanceId });
                }
                SchoolSchedulerService.RemoveJob(instanceId);
                LoggingService.SaveAuditLog("SchedulerJobInstances", instanceId.ToString(), "DELETE", null, null, "Admin", ClientIp);
                return Request.CreateResponse(HttpStatusCode.OK, new { message = "Schedule deleted." });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /api/scheduler/trigger — Manual trigger
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost, Route("trigger")]
        public HttpResponseMessage Trigger([FromUri] long instanceId)
        {
            if (instanceId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "Invalid instanceId." });
            try
            {
                BackgroundJob.Enqueue(() => SchoolSchedulerService.ExecuteJobAsync(instanceId, true));
                LoggingService.SaveAuditLog("SchedulerJobInstances", instanceId.ToString(),
                    "TRIGGER", null, null, "Admin", ClientIp, "Manual trigger from Admin UI");
                return Request.CreateResponse(HttpStatusCode.OK, new { message = $"Job enqueued for instance {instanceId}." });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET /api/scheduler/dashboard
        // ─────────────────────────────────────────────────────────────────────
        [HttpGet, Route("dashboard")]
        public HttpResponseMessage GetDashboard(
            [FromUri] string q = null,
            [FromUri] int page = 1,
            [FromUri] int pageSize = 10,
            [FromUri] string status = null,
            [FromUri] int? productId = null)
        {
            try
            {
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 100) pageSize = 10;

                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();

                    var stats = conn.QueryFirst<DashboardStats>(@"
                        SELECT
                            (SELECT COUNT(1) FROM Products         WHERE IsActive=1)             AS TotalProducts,
                            (SELECT COUNT(1) FROM ProductClients   WHERE IsActive=1)             AS TotalClients,
                            (SELECT COUNT(1) FROM SchedulerJobInstances WHERE IsActive=1)        AS ActiveSchedules,
                            (SELECT COUNT(1) FROM SchedulerExecutionLogs WHERE Status='SUCCESS'
                                AND CAST(StartedAt AS DATE) = CAST(GETDATE() AS DATE))          AS SuccessToday,
                            (SELECT COUNT(1) FROM SchedulerExecutionLogs WHERE Status='FAILED'
                                AND CAST(StartedAt AS DATE) = CAST(GETDATE() AS DATE))          AS FailedToday,
                            (SELECT COUNT(1) FROM SchedulerExecutionLogs WHERE Status='SKIPPED'
                                AND CAST(StartedAt AS DATE) = CAST(GETDATE() AS DATE))          AS SkippedToday,
                            (SELECT COUNT(1) FROM SchedulerExecutionLogs WHERE Status='RUNNING') AS RunningNow");

                    // Build filter
                    string where = "WHERE 1=1";
                    if (!string.IsNullOrWhiteSpace(q))
                        where += " AND (el.ClientName LIKE @Q OR el.ExternalId LIKE @Q OR el.JobTypeCode LIKE @Q)";
                    if (!string.IsNullOrWhiteSpace(status))
                        where += " AND el.Status = @Status";
                    if (productId.HasValue)
                        where += " AND el.ProductId = @ProductId";

                    int offset = (page - 1) * pageSize;

                    var recentLogs = conn.Query(@$"
                        SELECT el.LogId, el.ClientName, el.ExternalId, el.JobTypeCode,
                               el.TriggerType, el.StartedAt, el.CompletedAt, el.DurationSeconds,
                               el.Status, el.ErrorMessage, el.HttpStatusCode, el.ProductId,
                               p.ProductName
                        FROM SchedulerExecutionLogs el
                        LEFT JOIN Products p ON p.ProductId = el.ProductId
                        {where}
                        ORDER BY el.StartedAt DESC
                        OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY",
                        new { Q = $"%{q}%", Status = status, ProductId = productId, Offset = offset, PageSize = pageSize }).AsList();

                    int total = conn.ExecuteScalar<int>(@$"
                        SELECT COUNT(1) FROM SchedulerExecutionLogs el {where}",
                        new { Q = $"%{q}%", Status = status, ProductId = productId });

                    int totalPages = (int)Math.Ceiling((double)total / pageSize);

                    return Request.CreateResponse(HttpStatusCode.OK, new DashboardResponse
                    {
                        Stats = stats, RecentLogs = recentLogs,
                        Total = total, Page = page, PageSize = pageSize, TotalPages = totalPages
                    });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
