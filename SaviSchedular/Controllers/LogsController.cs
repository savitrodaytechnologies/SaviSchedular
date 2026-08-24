using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using Dapper;
using SaviSchedular.Models;

namespace SaviSchedular.Controllers
{
    [RoutePrefix("api/logs")]
    public class LogsController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        // GET /api/logs/execution?schoolId=X&jobType=Y&status=Z&days=7&page=1&pageSize=50
        [HttpGet, Route("execution")]
        public HttpResponseMessage GetExecutionLogs(
            [FromUri] long?   schoolId = null,
            [FromUri] string  jobType  = null,
            [FromUri] string  status   = null,
            [FromUri] int     days     = 7,
            [FromUri] int     page     = 1,
            [FromUri] int     pageSize = 50)
        {
            try
            {
                if (days < 1)  days     = 7;
                if (days > 365) days    = 365;
                if (page < 1)  page     = 1;
                if (pageSize < 1) pageSize = 50;
                if (pageSize > 200) pageSize = 200;

                int offset = (page - 1) * pageSize;

                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();

                    string where = @"
                        WHERE StartedAt >= DATEADD(DAY, -@Days, GETDATE())
                          AND (@SchoolId IS NULL OR SchoolId   = @SchoolId)
                          AND (@JobType  IS NULL OR JobTypeCode = @JobType)
                          AND (@Status   IS NULL OR Status      = @Status)";

                    int total = conn.ExecuteScalar<int>(
                        $"SELECT COUNT(1) FROM SchedulerExecutionLogs {where}",
                        new { Days = days, SchoolId = schoolId, JobType = jobType, Status = status });

                    var logs = conn.Query<ExecutionLogModel>($@"
                        SELECT LogId, SchoolId, SchoolName, JobTypeCode, TriggerType,
                               StartedAt, CompletedAt, DurationSeconds, Status,
                               SkipReason, ApiUrl, HttpStatusCode, ResponseBody,
                               ErrorMessage, HangfireJobId
                        FROM   SchedulerExecutionLogs
                        {where}
                        ORDER  BY StartedAt DESC
                        OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY",
                        new { Days = days, SchoolId = schoolId, JobType = jobType,
                              Status = status, Offset = offset, PageSize = pageSize }).AsList();

                    return Request.CreateResponse(HttpStatusCode.OK, new
                    {
                        total,
                        page,
                        pageSize,
                        totalPages = (int)Math.Ceiling(total / (double)pageSize),
                        data       = logs
                    });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // GET /api/logs/audit?days=30&table=X&page=1
        [HttpGet, Route("audit")]
        public HttpResponseMessage GetAuditLogs(
            [FromUri] int     days     = 30,
            [FromUri] string  table    = null,
            [FromUri] int     page     = 1,
            [FromUri] int     pageSize = 50)
        {
            try
            {
                if (days < 1) days = 30;
                if (page < 1) page = 1;
                if (pageSize < 1) pageSize = 50;
                if (pageSize > 200) pageSize = 200;

                int offset = (page - 1) * pageSize;

                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();

                    string where = @"
                        WHERE ChangedAt >= DATEADD(DAY, -@Days, GETDATE())
                          AND (@Table IS NULL OR TableName = @Table)";

                    int total = conn.ExecuteScalar<int>(
                        $"SELECT COUNT(1) FROM SchedulerAuditLogs {where}",
                        new { Days = days, Table = table });

                    var logs = conn.Query<AuditLogModel>($@"
                        SELECT AuditId, TableName, RecordId, Action,
                               OldValues, NewValues, ChangedBy, ChangedAt, IPAddress, Notes
                        FROM   SchedulerAuditLogs
                        {where}
                        ORDER  BY ChangedAt DESC
                        OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY",
                        new { Days = days, Table = table, Offset = offset, PageSize = pageSize }).AsList();

                    return Request.CreateResponse(HttpStatusCode.OK, new
                    {
                        total,
                        page,
                        pageSize,
                        totalPages = (int)Math.Ceiling(total / (double)pageSize),
                        data       = logs
                    });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // GET /api/logs/stats?days=7 — Log statistics
        [HttpGet, Route("stats")]
        public HttpResponseMessage GetStats([FromUri] int days = 7)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var stats = conn.Query(@"
                        SELECT
                            JobTypeCode,
                            COUNT(CASE WHEN Status='SUCCESS' THEN 1 END) AS Success,
                            COUNT(CASE WHEN Status='FAILED'  THEN 1 END) AS Failed,
                            COUNT(CASE WHEN Status='SKIPPED' THEN 1 END) AS Skipped,
                            AVG(DurationSeconds) AS AvgDuration
                        FROM SchedulerExecutionLogs
                        WHERE StartedAt >= DATEADD(DAY, -@Days, GETDATE())
                        GROUP BY JobTypeCode
                        ORDER BY JobTypeCode",
                        new { Days = days }).AsList();

                    return Request.CreateResponse(HttpStatusCode.OK, stats);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
