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
                                DefaultApiPath = req.DefaultApiPath ?? "/api/scheduled-publishing/execution",
                                HttpMethod = req.HttpMethod ?? "POST"
                            });
                    }
                    else
                    {
                        jobTypeId = jobType.JobTypeId;
                        if (!string.IsNullOrWhiteSpace(req.DefaultApiPath) && req.DefaultApiPath != jobType.DefaultApiPath)
                        {
                            conn.Execute("UPDATE ProductJobTypes SET DefaultApiPath=@DefaultApiPath WHERE JobTypeId=@JId",
                                new { DefaultApiPath = req.DefaultApiPath, JId = jobTypeId });
                        }
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

                    // Parse ScheduledTime if provided
                    int hour = req.ScheduledHour;
                    int minute = req.ScheduledMinute;
                    string timeStr = req.ScheduledTime;

                    if (!string.IsNullOrWhiteSpace(timeStr))
                    {
                        var parts = timeStr.Trim().Split(':');
                        if (parts.Length >= 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
                        {
                            hour = h;
                            minute = m;
                        }
                    }
                    else
                    {
                        timeStr = $"{hour:D2}:{minute:D2}";
                    }

                    string freqType = !string.IsNullOrWhiteSpace(req.FrequencyType) ? req.FrequencyType.Trim().ToUpper() : "DAILY";

                    // Determine incremented Job Name (_1, _2, etc.)
                    string baseJobName = "Campaign Social Post";
                    long reqCampaignId = 0;

                    if (!string.IsNullOrWhiteSpace(req.PayloadJson))
                    {
                        try
                        {
                            using var doc = System.Text.Json.JsonDocument.Parse(req.PayloadJson);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("jobName", out var jElem) && !string.IsNullOrWhiteSpace(jElem.GetString()))
                            {
                                baseJobName = jElem.GetString()!.Trim();
                            }
                            else if (root.TryGetProperty("jobTypeName", out var jtElem) && !string.IsNullOrWhiteSpace(jtElem.GetString()))
                            {
                                baseJobName = jtElem.GetString()!.Trim();
                            }

                            if (root.TryGetProperty("campaignId", out var cElem) && cElem.TryGetInt64(out long cId))
                            {
                                reqCampaignId = cId;
                            }
                        }
                        catch { }
                    }

                    // Strip any trailing _1, _2 suffix from base name to prevent compounding suffixes
                    baseJobName = System.Text.RegularExpressions.Regex.Replace(baseJobName, @"_\d+$", "");

                    // Count existing instances for this client & job type
                    int existingCount = conn.ExecuteScalar<int>(
                        "SELECT COUNT(1) FROM SchedulerJobInstances WHERE ClientId=@CId AND JobTypeId=@JId",
                        new { CId = clientId, JId = jobTypeId });

                    string finalJobName = existingCount > 0 ? $"{baseJobName}_{existingCount}" : baseJobName;

                    // Inject updated jobName into PayloadJson
                    string finalPayloadJson = req.PayloadJson;
                    if (!string.IsNullOrWhiteSpace(finalPayloadJson))
                    {
                        try
                        {
                            var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(finalPayloadJson) ?? new Dictionary<string, object>();
                            dict["jobName"] = finalJobName;
                            dict["jobTypeName"] = finalJobName;
                            finalPayloadJson = System.Text.Json.JsonSerializer.Serialize(dict);
                        }
                        catch { }
                    }

                    // Always insert a new Schedule Instance (no duplicate overwrite)
                    long instanceId = conn.ExecuteScalar<long>(@"
                        INSERT INTO SchedulerJobInstances
                            (ClientId, ProductId, JobTypeId, CustomApiPath, CustomApiToken, PayloadJson, ScheduledHour, ScheduledMinute,
                             ScheduledTime, FrequencyType, ScheduledDays, DayOfMonth, MonthOfYear, MultipleTimes, IntervalValue, IntervalUnit, ScheduleRules, CronExpression,
                             TimeZone, IsActive, RunOnHolidays, MisfireThresholdMinutes, CreatedAt, UpdatedAt, CreatedBy)
                        VALUES
                            (@ClientId, @ProductId, @JobTypeId, @CustomApiPath, @CustomApiToken, @PayloadJson, @ScheduledHour, @ScheduledMinute,
                             @ScheduledTime, @FrequencyType, @ScheduledDays, @DayOfMonth, @MonthOfYear, @MultipleTimes, @IntervalValue, @IntervalUnit, @ScheduleRules, @CronExpression,
                             @TimeZone, @IsActive, @RunOnHolidays, 15, @Now, @Now, @By);
                        SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                        new {
                            ClientId = clientId, ProductId = productId, JobTypeId = jobTypeId,
                            CustomApiPath = req.CustomApiPath, CustomApiToken = req.CustomApiToken,
                            PayloadJson = finalPayloadJson, ScheduledHour = hour, ScheduledMinute = minute,
                            ScheduledTime = timeStr, FrequencyType = freqType,
                            ScheduledDays = req.ScheduledDays, DayOfMonth = req.DayOfMonth, MonthOfYear = req.MonthOfYear,
                            MultipleTimes = req.MultipleTimes, IntervalValue = req.IntervalValue, IntervalUnit = req.IntervalUnit,
                            ScheduleRules = req.ScheduleRules, CronExpression = req.CronExpression,
                            TimeZone = req.TimeZone ?? "India Standard Time",
                            IsActive = req.IsActive, RunOnHolidays = req.RunOnHolidays ? 1 : 0,
                            Now = DateTime.Now, By = apiClient.ClientName
                        });

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
                        SELECT ji.InstanceId, ji.ScheduledHour, ji.ScheduledMinute, ji.ScheduledTime, ji.FrequencyType, ji.TimeZone,
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

        // ─────────────────────────────────────────────────────────────────────
        // POST /api/external/schedule/toggle/{instanceId:long}?isActive=true|false
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost, Route("schedule/toggle/{instanceId:long}")]
        public HttpResponseMessage ToggleScheduleInstance(long instanceId, [FromUri] bool isActive)
        {
            var (valid, apiClient) = AuthenticateRequest();
            if (!valid) return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Invalid or missing X-SaviSchedular-Key." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var updated = conn.Execute(@"
                        UPDATE SchedulerJobInstances SET IsActive=@IsActive, UpdatedAt=GETDATE() WHERE InstanceId=@InstanceId",
                        new { IsActive = isActive, InstanceId = instanceId });

                    if (updated == 0)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Schedule instance not found." });

                    if (isActive)
                        SchoolSchedulerService.RegisterJobByInstanceId(instanceId);
                    else
                        SchoolSchedulerService.RemoveJob(instanceId);

                    return Request.CreateResponse(HttpStatusCode.OK, new { success = true, instanceId, isActive });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // DELETE /api/external/schedule/delete-instance/{instanceId:long}
        // ─────────────────────────────────────────────────────────────────────
        [HttpDelete, Route("schedule/delete-instance/{instanceId:long}")]
        public HttpResponseMessage DeleteScheduleInstance(long instanceId)
        {
            var (valid, apiClient) = AuthenticateRequest();
            if (!valid) return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Invalid or missing X-SaviSchedular-Key." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var deleted = conn.Execute("DELETE FROM SchedulerJobInstances WHERE InstanceId=@InstanceId", new { InstanceId = instanceId });
                    if (deleted == 0)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Schedule instance not found." });

                    SchoolSchedulerService.RemoveJob(instanceId);
                    return Request.CreateResponse(HttpStatusCode.OK, new { success = true, instanceId, message = "Schedule removed." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /api/external/schedule/update-instance
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost, Route("schedule/update-instance")]
        public HttpResponseMessage UpdateScheduleInstance([FromBody] ExternalInstanceUpdateRequest req)
        {
            var (valid, apiClient) = AuthenticateRequest();
            if (!valid) return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Invalid or missing X-SaviSchedular-Key." });

            if (req == null || req.InstanceId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "InstanceId is required." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();

                    int hour = 0, minute = 0;
                    string timeStr = req.ScheduledTime;
                    if (!string.IsNullOrWhiteSpace(timeStr))
                    {
                        var parts = timeStr.Trim().Split(':');
                        if (parts.Length >= 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
                        {
                            hour = h;
                            minute = m;
                        }
                    }

                    string freqType = !string.IsNullOrWhiteSpace(req.FrequencyType) ? req.FrequencyType.Trim().ToUpper() : "DAILY";

                    var updated = conn.Execute(@"
                        UPDATE SchedulerJobInstances SET
                            ScheduledHour   = @Hour,
                            ScheduledMinute = @Minute,
                            ScheduledTime   = @Time,
                            FrequencyType   = @Freq,
                            ScheduledDays   = @Days,
                            DayOfMonth      = @Dom,
                            MonthOfYear     = @Moy,
                            MultipleTimes   = @Mtimes,
                            IntervalValue   = @Ival,
                            IntervalUnit    = @Iunit,
                            TimeZone        = COALESCE(@TZ, TimeZone),
                            PayloadJson     = COALESCE(@Payload, PayloadJson),
                            IsActive        = @IsActive,
                            UpdatedAt       = GETDATE()
                        WHERE InstanceId = @InstanceId",
                        new {
                            Hour = hour,
                            Minute = minute,
                            Time = timeStr,
                            Freq = freqType,
                            Days = req.ScheduledDays,
                            Dom = req.DayOfMonth,
                            Moy = req.MonthOfYear,
                            Mtimes = req.MultipleTimes,
                            Ival = req.IntervalValue,
                            Iunit = req.IntervalUnit,
                            TZ = req.TimeZone,
                            Payload = req.PayloadJson,
                            IsActive = req.IsActive,
                            InstanceId = req.InstanceId
                        });

                    if (updated == 0)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Schedule instance not found." });

                    if (req.IsActive)
                        SchoolSchedulerService.RegisterJobByInstanceId(req.InstanceId);
                    else
                        SchoolSchedulerService.RemoveJob(req.InstanceId);

                    return Request.CreateResponse(HttpStatusCode.OK, new { success = true, instanceId = req.InstanceId, message = "Schedule instance updated successfully." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
