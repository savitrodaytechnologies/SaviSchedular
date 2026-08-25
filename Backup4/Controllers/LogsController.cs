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

        // GET /api/logs?productId=&clientId=&status=&q=&page=&pageSize=
        [HttpGet, Route("")]
        public HttpResponseMessage GetLogs(
            [FromUri] int? productId = null,
            [FromUri] long? clientId = null,
            [FromUri] string status  = null,
            [FromUri] string q       = null,
            [FromUri] int page       = 1,
            [FromUri] int pageSize   = 20)
        {
            try
            {
                if (page < 1) page = 1;
                if (pageSize < 1 || pageSize > 200) pageSize = 20;

                int offset = (page - 1) * pageSize;
                var p = new DynamicParameters();
                p.Add("Offset", offset);
                p.Add("PageSize", pageSize);

                string where = "WHERE 1=1";
                if (productId.HasValue)             { where += " AND el.ProductId = @ProductId"; p.Add("ProductId", productId.Value); }
                if (clientId.HasValue)              { where += " AND el.ClientId  = @ClientId";  p.Add("ClientId", clientId.Value); }
                if (!string.IsNullOrWhiteSpace(status)) { where += " AND el.Status = @Status"; p.Add("Status", status); }
                if (!string.IsNullOrWhiteSpace(q))      { where += " AND (el.ClientName LIKE @Q OR el.ExternalId LIKE @Q OR el.JobTypeCode LIKE @Q OR el.ErrorMessage LIKE @Q)"; p.Add("Q", "%" + q.Trim() + "%"); }

                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();

                    string logSql = "SELECT el.*, p.ProductName FROM SchedulerExecutionLogs el LEFT JOIN Products p ON p.ProductId = el.ProductId " + where + " ORDER BY el.StartedAt DESC OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";
                    string countSql = "SELECT COUNT(1) FROM SchedulerExecutionLogs el " + where;

                    var logs = conn.Query(logSql, p).AsList();
                    int total = conn.ExecuteScalar<int>(countSql, p);

                    return Request.CreateResponse(HttpStatusCode.OK, new {
                        logs, total, page, pageSize,
                        totalPages = (int)Math.Ceiling((double)total / pageSize)
                    });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // GET /api/logs/{logId} — Full detail of one log entry
        [HttpGet, Route("{logId:long}")]
        public HttpResponseMessage GetById(long logId)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var log = conn.QueryFirstOrDefault<ExecutionLogModel>(
                        "SELECT * FROM SchedulerExecutionLogs WHERE LogId=@LogId", new { LogId = logId });
                    if (log == null)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Log not found." });
                    return Request.CreateResponse(HttpStatusCode.OK, log);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // DELETE /api/logs/purge?olderThanDays=30
        [HttpDelete, Route("purge")]
        public HttpResponseMessage Purge([FromUri] int olderThanDays = 30)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    int deleted = conn.Execute(
                        "DELETE FROM SchedulerExecutionLogs WHERE StartedAt < @Cutoff",
                        new { Cutoff = DateTime.Now.AddDays(-olderThanDays) });
                    return Request.CreateResponse(HttpStatusCode.OK, new { message = $"Purged {deleted} log entries older than {olderThanDays} days." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
