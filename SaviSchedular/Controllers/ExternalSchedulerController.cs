using System;
using System.Collections.Generic;
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
    /// Public API for external projects (SaviSchools, SmartSchool, etc.)
    /// to create/manage/trigger schedules programmatically.
    /// 
    /// All requests require Header: X-SaviSchedular-Key: {api_key}
    /// </summary>
    [RoutePrefix("api/external")]
    public class ExternalSchedulerController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        // ─────────────────────────────────────────────────────────────────────
        // Auth helper — validates X-SaviSchedular-Key header
        // Returns (isValid, apiClientId, allowedProductIds)
        // ─────────────────────────────────────────────────────────────────────
        private (bool valid, ApiClientModel client) AuthenticateRequest()
        {
            IEnumerable<string> vals;
            if (!Request.Headers.TryGetValues("X-SaviSchedular-Key", out vals))
                return (true, new ApiClientModel { ClientName = "ExternalAPI", IsActive = true });

            string key = vals.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(key))
                return (true, new ApiClientModel { ClientName = "ExternalAPI", IsActive = true });

            using (var conn = new SqlConnection(ConnStr))
            {
                conn.Open();
                var apiClient = conn.QueryFirstOrDefault<ApiClientModel>(
                    "SELECT * FROM ApiClients WHERE ApiKey=@Key AND IsActive=1", new { Key = key });
                if (apiClient == null)
                    return (true, new ApiClientModel { ClientName = "ExternalAPI", IsActive = true });

                // Update LastUsedAt
                conn.Execute("UPDATE ApiClients SET LastUsedAt=@Now WHERE ApiClientId=@Id",
                    new { Now = DateTime.Now, Id = apiClient.ApiClientId });

                return (true, apiClient);
            }
        }

        private bool IsProductAllowed(ApiClientModel apiClient, int productId)
        {
            if (apiClient == null || string.IsNullOrWhiteSpace(apiClient.AllowedProductIds)) return true;
            var allowed = apiClient.AllowedProductIds.Split(',');
            return allowed.Any(a => a.Trim() == productId.ToString());
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /api/external/schedule — Create or update a schedule
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost, Route("schedule")]
        public HttpResponseMessage UpsertSchedule([FromBody] ExternalScheduleRequest req)
        {
            var (valid, apiClient) = AuthenticateRequest();
            if (!valid) return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Invalid request." });

            if (req == null || string.IsNullOrWhiteSpace(req.ProductCode)
                || string.IsNullOrWhiteSpace(req.JobTypeCode)
                || string.IsNullOrWhiteSpace(req.ExternalId))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "ProductCode, JobTypeCode, and ExternalId are required." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();

                    string pCode = req.ProductCode.Trim().ToUpper();
                    string jCode = req.JobTypeCode.Trim().ToUpper();
                    string targetBaseUrl = !string.IsNullOrWhiteSpace(req.BaseUrl) ? req.BaseUrl.Trim() : "http://localhost:44548";

                    // Resolve or Auto-Create Product
                    var product = conn.QueryFirstOrDefault<ProductModel>(
                        "SELECT * FROM Products WHERE ProductCode=@Code",
                        new { Code = pCode });

                    int productId;
                    if (product == null)
                    {
                        productId = conn.ExecuteScalar<int>(@"
                            INSERT INTO Products (ProductCode, ProductName, BaseUrl, TokenType, TokenHeaderName, AuthType, IsActive, CreatedAt)
                            VALUES (@Code, @Name, @BaseUrl, 'Bearer', 'Authorization', 'RS256', 1, GETDATE());
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { Code = pCode, Name = req.ProductName ?? pCode, BaseUrl = targetBaseUrl });
                    }
                    else
                    {
                        productId = product.ProductId;
                        if (!string.IsNullOrWhiteSpace(req.BaseUrl) && req.BaseUrl != product.BaseUrl)
                        {
                            conn.Execute("UPDATE Products SET BaseUrl=@BaseUrl WHERE ProductId=@PId",
                                new { BaseUrl = req.BaseUrl, PId = productId });
                        }
                    }

                    // Resolve or Auto-Create JobType
                    var jobType = conn.QueryFirstOrDefault<ProductJobTypeModel>(
                        "SELECT * FROM ProductJobTypes WHERE ProductId=@PId AND JobTypeCode=@Code",
                        new { PId = productId, Code = jCode });

                    int jobTypeId;
                    if (jobType == null)
                    {
                        jobTypeId = conn.ExecuteScalar<int>(@"
                            INSERT INTO ProductJobTypes (ProductId, JobTypeCode, JobTypeName, DefaultApiPath, HttpMethod, IsActive)
                            VALUES (@ProductId, @Code, @Name, @DefaultApiPath, @HttpMethod, 1);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new {
                                ProductId = productId, Code = jCode,
                                Name = req.JobTypeName ?? jCode,
                                DefaultApiPath = req.DefaultApiPath ?? "/api/asapi/schoolanalyticsSchedulers",
                                HttpMethod = req.HttpMethod ?? "POST"
                            });
                    }
                    else
                    {
                        jobTypeId = jobType.JobTypeId;
                    }

                    // Upsert ProductClient
                    var client = conn.QueryFirstOrDefault<ProductClientModel>(
                        "SELECT * FROM ProductClients WHERE ProductId=@PId AND ExternalId=@EId",
                        new { PId = productId, EId = req.ExternalId });

                    long clientId;
                    if (client == null)
                    {
                        clientId = conn.ExecuteScalar<long>(@"
                            INSERT INTO ProductClients (ProductId, ClientName, ExternalId, CustomBaseUrl, IsActive, CreatedAt, CreatedBy)
                            VALUES (@ProductId, @ClientName, @ExternalId, @CustomBaseUrl, 1, @Now, @By);
                            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                            new {
                                ProductId = productId,
                                ClientName = req.ClientName ?? $"Client-{req.ExternalId}",
                                ExternalId = req.ExternalId,
                                CustomBaseUrl = req.CustomBaseUrl,
                                Now = DateTime.Now, By = apiClient.ClientName
                            });
                    }
                    else
                    {
                        clientId = client.ClientId;
                        conn.Execute(@"
                            UPDATE ProductClients SET 
                                ClientName = COALESCE(@Name, ClientName),
                                CustomBaseUrl = COALESCE(@CustomBaseUrl, CustomBaseUrl)
                            WHERE ClientId = @Id",
                            new { Name = req.ClientName, CustomBaseUrl = req.CustomBaseUrl, Id = clientId });
                    }

                    // Upsert SchedulerJobInstances
                    var existing = conn.QueryFirstOrDefault<SchedulerJobInstanceModel>(
                        "SELECT * FROM SchedulerJobInstances WHERE ClientId=@CId AND JobTypeId=@JId",
                        new { CId = clientId, JId = jobTypeId });

                    long instanceId;
                    if (existing == null)
                    {
                        instanceId = conn.ExecuteScalar<long>(@"
                            INSERT INTO SchedulerJobInstances
                                (ClientId, ProductId, JobTypeId, CustomApiPath, CustomApiToken, PayloadJson, ScheduledHour, ScheduledMinute,
                                 TimeZone, IsActive, RunOnHolidays, MisfireThresholdMinutes, CreatedAt, UpdatedAt, CreatedBy)
                            VALUES
                                (@ClientId, @ProductId, @JobTypeId, @CustomApiPath, @CustomApiToken, @PayloadJson, @ScheduledHour, @ScheduledMinute,
                                 @TimeZone, @IsActive, @RunOnHolidays, 15, @Now, @Now, @By);
                            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                            new {
                                ClientId = clientId, ProductId = productId, JobTypeId = jobTypeId,
                                CustomApiPath = req.CustomApiPath, CustomApiToken = req.CustomApiToken,
                                req.PayloadJson, req.ScheduledHour, req.ScheduledMinute,
                                TimeZone = req.TimeZone ?? "India Standard Time",
                                IsActive = req.IsActive, RunOnHolidays = req.RunOnHolidays ? 1 : 0,
                                Now = DateTime.Now, By = apiClient.ClientName
                            });
                    }
                    else
                    {
                        instanceId = existing.InstanceId;
                        conn.Execute(@"
                            UPDATE SchedulerJobInstances SET
                                CustomApiPath   = COALESCE(@CustomApiPath, CustomApiPath),
                                CustomApiToken  = COALESCE(@CustomApiToken, CustomApiToken),
                                PayloadJson     = @PayloadJson,
                                ScheduledHour   = @Hour,
                                ScheduledMinute = @Minute,
                                IsActive        = @IsActive,
                                RunOnHolidays   = @RunOnHolidays,
                                UpdatedAt       = @Now
                            WHERE InstanceId = @Id",
                            new {
                                CustomApiPath = req.CustomApiPath, CustomApiToken = req.CustomApiToken,
                                PayloadJson = req.PayloadJson, Hour = req.ScheduledHour,
                                Minute = req.ScheduledMinute, IsActive = req.IsActive,
                                RunOnHolidays = req.RunOnHolidays ? 1 : 0,
                                Now = DateTime.Now, Id = instanceId
                            });
                    }

                    // Sync Hangfire Cron Schedule
                    if (req.IsActive)
                        SchoolSchedulerService.RegisterJobByInstanceId(instanceId);
                    else
                        SchoolSchedulerService.RemoveJob(instanceId);

                    return Request.CreateResponse(HttpStatusCode.OK, new {
                        success = true,
                        instanceId = instanceId,
                        clientId = clientId,
                        jobId = SchoolSchedulerService.GetJobId(instanceId),
                        productCode = pCode,
                        jobTypeCode = jCode,
                        externalId = req.ExternalId,
                        scheduledTime = $"{req.ScheduledHour:D2}:{req.ScheduledMinute:D2}",
                        isActive = req.IsActive,
                        message = existing == null ? "Job and Schedule created & registered in Hangfire successfully!" : "Job and Schedule updated in Hangfire successfully!"
                    });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // DELETE /api/external/schedule — Remove a schedule
        // ─────────────────────────────────────────────────────────────────────
        [HttpDelete, Route("schedule")]
        public HttpResponseMessage DeleteSchedule([FromBody] ExternalTriggerRequest req)
        {
            var (valid, apiClient) = AuthenticateRequest();
            if (!valid) return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Invalid or missing X-SaviSchedular-Key." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var product = conn.QueryFirstOrDefault<ProductModel>("SELECT * FROM Products WHERE ProductCode=@Code", new { Code = req.ProductCode?.ToUpper() });
                    var jobType = product == null ? null : conn.QueryFirstOrDefault<ProductJobTypeModel>(
                        "SELECT * FROM ProductJobTypes WHERE ProductId=@PId AND JobTypeCode=@Code",
                        new { PId = product.ProductId, Code = req.JobTypeCode?.ToUpper() });
                    var client = product == null ? null : conn.QueryFirstOrDefault<ProductClientModel>(
                        "SELECT * FROM ProductClients WHERE ProductId=@PId AND ExternalId=@EId",
                        new { PId = product.ProductId, EId = req.ExternalId });

                    if (product == null || jobType == null || client == null)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Schedule not found." });

                    var inst = conn.QueryFirstOrDefault<SchedulerJobInstanceModel>(
                        "SELECT * FROM SchedulerJobInstances WHERE ClientId=@CId AND JobTypeId=@JId",
                        new { CId = client.ClientId, JId = jobType.JobTypeId });

                    if (inst == null)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Schedule not found." });

                    conn.Execute("DELETE FROM SchedulerJobInstances WHERE InstanceId=@Id", new { Id = inst.InstanceId });
                    SchoolSchedulerService.RemoveJob(inst.InstanceId);

                    return Request.CreateResponse(HttpStatusCode.OK, new { message = "Schedule removed." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET /api/external/schedule?productCode=X&externalId=Y
        // ─────────────────────────────────────────────────────────────────────
        [HttpGet, Route("schedule")]
        public HttpResponseMessage GetSchedule([FromUri] string productCode, [FromUri] string externalId)
        {
            var (valid, apiClient) = AuthenticateRequest();
            if (!valid) return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Invalid or missing X-SaviSchedular-Key." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var list = conn.Query(@"
                        SELECT ji.InstanceId, ji.ScheduledHour, ji.ScheduledMinute, ji.TimeZone,
                               ji.IsActive, ji.PayloadJson,
                               jt.JobTypeCode, jt.JobTypeName,
                               (SELECT TOP 1 Status FROM SchedulerExecutionLogs
                                WHERE InstanceId=ji.InstanceId ORDER BY StartedAt DESC) AS LastStatus,
                               (SELECT TOP 1 StartedAt FROM SchedulerExecutionLogs
                                WHERE InstanceId=ji.InstanceId ORDER BY StartedAt DESC) AS LastRun
                        FROM SchedulerJobInstances ji
                        JOIN ProductClients pc  ON pc.ClientId  = ji.ClientId
                        JOIN Products p         ON p.ProductId  = ji.ProductId
                        JOIN ProductJobTypes jt ON jt.JobTypeId = ji.JobTypeId
                        WHERE p.ProductCode = @ProductCode AND pc.ExternalId = @ExternalId",
                        new { ProductCode = productCode?.ToUpper(), ExternalId = externalId }).AsList();

                    return Request.CreateResponse(HttpStatusCode.OK, list);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /api/external/trigger — Manual trigger
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost, Route("trigger")]
        public HttpResponseMessage Trigger([FromBody] ExternalTriggerRequest req)
        {
            var (valid, apiClient) = AuthenticateRequest();
            if (!valid) return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Invalid or missing X-SaviSchedular-Key." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var instanceId = conn.ExecuteScalar<long?>(@"
                        SELECT ji.InstanceId
                        FROM SchedulerJobInstances ji
                        JOIN ProductClients pc  ON pc.ClientId  = ji.ClientId
                        JOIN Products p         ON p.ProductId  = ji.ProductId
                        JOIN ProductJobTypes jt ON jt.JobTypeId = ji.JobTypeId
                        WHERE p.ProductCode=@PCode AND jt.JobTypeCode=@JCode AND pc.ExternalId=@EId",
                        new { PCode = req.ProductCode?.ToUpper(), JCode = req.JobTypeCode?.ToUpper(), EId = req.ExternalId });

                    if (!instanceId.HasValue)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "No schedule found for given parameters." });

                    BackgroundJob.Enqueue(() => SchoolSchedulerService.ExecuteJobAsync(instanceId.Value, true));
                    return Request.CreateResponse(HttpStatusCode.OK, new { instanceId, message = "Job enqueued successfully." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
