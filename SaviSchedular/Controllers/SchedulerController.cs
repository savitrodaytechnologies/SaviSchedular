using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using Dapper;
using Hangfire;
using SaviSchedular.Models;
using SaviSchedular.Services;

namespace SaviSchedular.Controllers
{
    [RoutePrefix("api/scheduler")]
    public class SchedulerController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        private string ClientIp
            => System.Web.HttpContext.Current?.Request?.UserHostAddress ?? "unknown";

        // ─────────────────────────────────────────────────────────────────────
        // GET /api/scheduler/list — Sabhi schedules list karo
        // ─────────────────────────────────────────────────────────────────────
        [HttpGet, Route("list")]
        public HttpResponseMessage GetSchedules()
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var list = conn.Query(@"
                        SELECT ji.InstanceId, ji.SchoolId, ji.JobTypeCode,
                               jt.JobTypeName, ji.ScheduledHour, ji.ScheduledMinute,
                               ji.TimeZone, ji.IsActive, ji.RunOnHolidays,
                               ji.MisfireThresholdMinutes, ji.CreatedAt, ji.UpdatedAt, ji.CreatedBy
                        FROM   SchedulerJobInstances ji
                        LEFT JOIN SchedulerJobTypes jt ON ji.JobTypeCode = jt.JobTypeCode
                        ORDER  BY ji.SchoolId, ji.JobTypeCode").AsList();
                    return Request.CreateResponse(HttpStatusCode.OK, list);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /api/scheduler/save — Schedule add ya update karo
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost, Route("save")]
        public HttpResponseMessage SaveSchedule([FromBody] SaveScheduleRequest req)
        {
            if (req == null || req.SchoolId <= 0 || string.IsNullOrWhiteSpace(req.JobTypeCode) ||
                req.Hour < 0 || req.Hour > 23 || req.Minute < 0 || req.Minute > 59)
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "Invalid request: SchoolId, JobTypeCode, Hour (0-23) and Minute (0-59) are required." });

            try
            {
                object oldVals = null;
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    oldVals = conn.QueryFirstOrDefault(@"
                        SELECT SchoolId, JobTypeCode, ScheduledHour, ScheduledMinute, TimeZone, IsActive
                        FROM   SchedulerJobInstances
                        WHERE  SchoolId = @SchoolId AND JobTypeCode = @JobTypeCode",
                        new { req.SchoolId, req.JobTypeCode });

                    conn.Execute(@"
                        IF NOT EXISTS (SELECT 1 FROM SchedulerJobInstances
                                       WHERE SchoolId=@SchoolId AND JobTypeCode=@JobTypeCode)
                            INSERT INTO SchedulerJobInstances
                                (SchoolId, JobTypeCode, ScheduledHour, ScheduledMinute,
                                 TimeZone, IsActive, RunOnHolidays, MisfireThresholdMinutes, CreatedBy)
                            VALUES
                                (@SchoolId, @JobTypeCode, @Hour, @Minute,
                                 @TimeZone, 1, @RunOnHolidays, @Misfire, @CreatedBy)
                        ELSE
                            UPDATE SchedulerJobInstances SET
                                ScheduledHour = @Hour, ScheduledMinute = @Minute,
                                TimeZone   = @TimeZone,   IsActive = 1,          RunOnHolidays = @RunOnHolidays,
                                MisfireThresholdMinutes = @Misfire, UpdatedAt = GETDATE()
                            WHERE SchoolId=@SchoolId AND JobTypeCode=@JobTypeCode",
                        new
                        {
                            req.SchoolId, req.JobTypeCode,
                            Hour     = req.Hour,   Minute = req.Minute,
                            TimeZone = req.TimeZone ?? "India Standard Time",
                            RunOnHolidays = req.RunOnHolidays,
                            Misfire       = req.MisfireThresholdMinutes > 0 ? req.MisfireThresholdMinutes : 15,
                            CreatedBy     = req.CreatedBy ?? "Admin"
                        });
                }

                // Hangfire mein register/update karo
                var inst = new SchedulerJobInstanceModel
                {
                    SchoolId = req.SchoolId, JobTypeCode = req.JobTypeCode,
                    ScheduledHour = req.Hour, ScheduledMinute = req.Minute,
                    TimeZone = req.TimeZone ?? "India Standard Time", IsActive = true,
                    RunOnHolidays = req.RunOnHolidays,
                    MisfireThresholdMinutes = req.MisfireThresholdMinutes > 0 ? req.MisfireThresholdMinutes : 15
                };
                SchoolSchedulerService.RegisterJob(inst);

                // Audit log
                LoggingService.SaveAuditLog("SchedulerJobInstances",
                    $"{req.SchoolId}_{req.JobTypeCode}",
                    oldVals == null ? "INSERT" : "UPDATE",
                    oldVals, inst, req.CreatedBy ?? "Admin", ClientIp);

                return Request.CreateResponse(HttpStatusCode.OK, new
                {
                    message = $"Schedule saved: School {req.SchoolId} | {req.JobTypeCode} at " +
                              $"{req.Hour:D2}:{req.Minute:D2} [{req.TimeZone ?? "India Standard Time"}]"
                });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // DELETE /api/scheduler/delete?schoolId=X&jobType=Y
        // ─────────────────────────────────────────────────────────────────────
        [HttpDelete, Route("delete")]
        public HttpResponseMessage DeleteSchedule([FromUri] long schoolId, [FromUri] string jobType)
        {
            if (schoolId <= 0 || string.IsNullOrWhiteSpace(jobType))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "schoolId and jobType are both required." });

            try
            {
                object oldVals = null;
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    oldVals = conn.QueryFirstOrDefault(
                        "SELECT * FROM SchedulerJobInstances WHERE SchoolId=@S AND JobTypeCode=@J",
                        new { S = schoolId, J = jobType });
                    conn.Execute(
                        "DELETE FROM SchedulerJobInstances WHERE SchoolId=@S AND JobTypeCode=@J",
                        new { S = schoolId, J = jobType });
                }
                SchoolSchedulerService.RemoveJob(schoolId, jobType);
                LoggingService.SaveAuditLog("SchedulerJobInstances", $"{schoolId}_{jobType}",
                    "DELETE", oldVals, null, "Admin", ClientIp);

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { message = $"Schedule deleted: School {schoolId} | {jobType}" });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /api/scheduler/toggle?schoolId=X&jobType=Y — Enable / Disable
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost, Route("toggle")]
        public HttpResponseMessage ToggleSchedule([FromUri] long schoolId, [FromUri] string jobType)
        {
            if (schoolId <= 0 || string.IsNullOrWhiteSpace(jobType))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "Invalid parameters." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var inst = conn.QueryFirstOrDefault<SchedulerJobInstanceModel>(
                        "SELECT * FROM SchedulerJobInstances WHERE SchoolId=@S AND JobTypeCode=@J",
                        new { S = schoolId, J = jobType });

                    if (inst == null)
                        return Request.CreateResponse(HttpStatusCode.NotFound,
                            new { error = "Schedule not found." });

                    bool newActive = !inst.IsActive;
                    conn.Execute(@"
                        UPDATE SchedulerJobInstances SET IsActive=@A, UpdatedAt=GETDATE()
                        WHERE SchoolId=@S AND JobTypeCode=@J",
                        new { A = newActive, S = schoolId, J = jobType });

                    if (newActive) SchoolSchedulerService.RegisterJob(inst);
                    else           SchoolSchedulerService.RemoveJob(schoolId, jobType);

                    LoggingService.SaveAuditLog("SchedulerJobInstances", $"{schoolId}_{jobType}",
                        "UPDATE", new { inst.IsActive }, new { IsActive = newActive }, "Admin", ClientIp);

                    return Request.CreateResponse(HttpStatusCode.OK,
                        new { isActive = newActive, message = newActive ? "Schedule enabled." : "Schedule disabled." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /api/scheduler/trigger?schoolId=X&jobType=Y — Manual trigger
        // ─────────────────────────────────────────────────────────────────────
        [HttpPost, Route("trigger")]
        public HttpResponseMessage TriggerManually([FromUri] long schoolId,
            [FromUri] string jobType = "AbsentWhatsApp")
        {
            if (schoolId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "Invalid School ID." });

            try
            {
                BackgroundJob.Enqueue(
                    () => SchoolSchedulerService.ExecuteJobAsync(schoolId, jobType, true));

                LoggingService.SaveAuditLog("SchedulerJobInstances", $"{schoolId}_{jobType}",
                    "TRIGGER", null, null, "Admin", ClientIp,
                    $"Manual trigger from Admin UI");

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { message = $"Job enqueued: School {schoolId} | {jobType}" });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET /api/scheduler/dashboard — Dashboard stats with search & pagination
        // ─────────────────────────────────────────────────────────────────────
        [HttpGet, Route("dashboard")]
        public HttpResponseMessage GetDashboard(
            [FromUri] string q = null,
            [FromUri] int page = 1,
            [FromUri] int pageSize = 10,
            [FromUri] string status = null)
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
                            (SELECT COUNT(DISTINCT SchoolId) FROM SchedulerJobInstances WHERE IsActive=1) AS ActiveSchools,
                            (SELECT COUNT(1)                 FROM SchedulerJobInstances WHERE IsActive=1) AS ActiveJobs,
                            (SELECT COUNT(1) FROM SchedulerExecutionLogs WHERE Status='SUCCESS'
                                AND CAST(StartedAt AS DATE) = CAST(GETDATE() AS DATE))                   AS SuccessToday,
                            (SELECT COUNT(1) FROM SchedulerExecutionLogs WHERE Status='FAILED'
                                AND CAST(StartedAt AS DATE) = CAST(GETDATE() AS DATE))                   AS FailedToday,
                            (SELECT COUNT(1) FROM SchedulerExecutionLogs WHERE Status='SKIPPED'
                                AND CAST(StartedAt AS DATE) = CAST(GETDATE() AS DATE))                   AS SkippedToday,
                            (SELECT COUNT(1) FROM SchedulerExecutionLogs WHERE Status='RUNNING')         AS RunningNow");

                    string whereClause = "WHERE 1=1";
                    var p = new DynamicParameters();

                    if (!string.IsNullOrWhiteSpace(q))
                    {
                        whereClause += " AND (CAST(SchoolId AS VARCHAR) LIKE @Q OR JobTypeCode LIKE @Q OR TriggerType LIKE @Q OR Status LIKE @Q)";
                        p.Add("Q", "%" + q.Trim() + "%");
                    }
                    if (!string.IsNullOrWhiteSpace(status))
                    {
                        whereClause += " AND Status = @Status";
                        p.Add("Status", status.Trim());
                    }

                    int total = conn.ExecuteScalar<int>($"SELECT COUNT(1) FROM SchedulerExecutionLogs {whereClause}", p);

                    int offset = (page - 1) * pageSize;
                    p.Add("Offset", offset);
                    p.Add("PageSize", pageSize);

                    var recentLogs = conn.Query($@"
                        SELECT LogId, SchoolId, JobTypeCode, TriggerType,
                               StartedAt, DurationSeconds, Status, SkipReason, ErrorMessage
                        FROM   SchedulerExecutionLogs
                        {whereClause}
                        ORDER  BY StartedAt DESC
                        OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY", p).AsList();

                    int totalPages = (int)Math.Ceiling((double)total / pageSize);

                    return Request.CreateResponse(HttpStatusCode.OK, new DashboardResponse
                    {
                        Stats = stats,
                        RecentLogs = recentLogs,
                        Total = total,
                        Page = page,
                        PageSize = pageSize,
                        TotalPages = totalPages
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
