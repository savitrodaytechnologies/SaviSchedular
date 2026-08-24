using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using Dapper;
using SaviSchedular.Models;
using SaviSchedular.Services;

namespace SaviSchedular.Controllers
{
    [RoutePrefix("api/jobtypes")]
    public class JobTypeController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        // GET /api/jobtypes/list
        [HttpGet, Route("list")]
        public HttpResponseMessage GetJobTypes()
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var list = conn.Query<JobTypeModel>(@"
                        SELECT JobTypeId, JobTypeCode, JobTypeName, Description,
                               DefaultApiPath, HttpMethod, IsActive, CreatedAt
                        FROM   SchedulerJobTypes
                        ORDER  BY JobTypeName").AsList();

                    if (list == null || list.Count == 0)
                    {
                        // Auto-seed default job types if table is empty
                        conn.Execute(@"
                            IF NOT EXISTS (SELECT 1 FROM SchedulerJobTypes WHERE JobTypeCode='AbsentWhatsApp')
                                INSERT INTO SchedulerJobTypes (JobTypeCode, JobTypeName, Description, DefaultApiPath, HttpMethod, IsActive)
                                VALUES ('AbsentWhatsApp', 'Absent WhatsApp Alert', 'Send WhatsApp notification to parents of absent students', '/api/asapi/run-absent-whatsapp', 'POST', 1);
                            IF NOT EXISTS (SELECT 1 FROM SchedulerJobTypes WHERE JobTypeCode='FeeReminder')
                                INSERT INTO SchedulerJobTypes (JobTypeCode, JobTypeName, Description, DefaultApiPath, HttpMethod, IsActive)
                                VALUES ('FeeReminder', 'Fee Reminder', 'Send fee payment reminder to parents of students with pending dues', '/api/asapi/run-fee-reminder', 'POST', 1);
                            IF NOT EXISTS (SELECT 1 FROM SchedulerJobTypes WHERE JobTypeCode='AttendanceReport')
                                INSERT INTO SchedulerJobTypes (JobTypeCode, JobTypeName, Description, DefaultApiPath, HttpMethod, IsActive)
                                VALUES ('AttendanceReport', 'Attendance Report Email', 'Send daily attendance report via email', '/api/report/attendance-email', 'POST', 1);
                            IF NOT EXISTS (SELECT 1 FROM SchedulerJobTypes WHERE JobTypeCode='ResultNotification')
                                INSERT INTO SchedulerJobTypes (JobTypeCode, JobTypeName, Description, DefaultApiPath, HttpMethod, IsActive)
                                VALUES ('ResultNotification', 'Result Notification', 'Notify parents about exam results', '/api/asapi/result-notify', 'POST', 1);
                            IF NOT EXISTS (SELECT 1 FROM SchedulerJobTypes WHERE JobTypeCode='CustomApiCall')
                                INSERT INTO SchedulerJobTypes (JobTypeCode, JobTypeName, Description, DefaultApiPath, HttpMethod, IsActive)
                                VALUES ('CustomApiCall', 'Custom API Call', 'Call any custom API endpoint (fully configurable)', NULL, 'POST', 1);
                        ");

                        list = conn.Query<JobTypeModel>(@"
                            SELECT JobTypeId, JobTypeCode, JobTypeName, Description,
                                   DefaultApiPath, HttpMethod, IsActive, CreatedAt
                            FROM   SchedulerJobTypes
                            ORDER  BY JobTypeName").AsList();
                    }

                    return Request.CreateResponse(HttpStatusCode.OK, list);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/jobtypes/save
        [HttpPost, Route("save")]
        public HttpResponseMessage SaveJobType([FromBody] SaveJobTypeRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.JobTypeCode) ||
                string.IsNullOrWhiteSpace(req.JobTypeName))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "JobTypeCode and JobTypeName are required." });

            try
            {
                object oldVals = null;
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    oldVals = conn.QueryFirstOrDefault(
                        "SELECT * FROM SchedulerJobTypes WHERE JobTypeCode=@C",
                        new { C = req.JobTypeCode });

                    conn.Execute(@"
                        IF NOT EXISTS (SELECT 1 FROM SchedulerJobTypes WHERE JobTypeCode=@JobTypeCode)
                            INSERT INTO SchedulerJobTypes
                                (JobTypeCode, JobTypeName, Description, DefaultApiPath, HttpMethod, IsActive)
                            VALUES
                                (@JobTypeCode, @JobTypeName, @Description, @DefaultApiPath, @HttpMethod, 1)
                        ELSE
                            UPDATE SchedulerJobTypes SET
                                JobTypeName = @JobTypeName, Description = @Description,
                                DefaultApiPath = @DefaultApiPath, HttpMethod = @HttpMethod
                            WHERE JobTypeCode = @JobTypeCode",
                        new
                        {
                            req.JobTypeCode, req.JobTypeName, req.Description,
                            req.DefaultApiPath,
                            HttpMethod = req.HttpMethod ?? "POST"
                        });
                }

                LoggingService.SaveAuditLog("SchedulerJobTypes", req.JobTypeCode,
                    oldVals == null ? "INSERT" : "UPDATE", oldVals, req);

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { message = $"Job Type '{req.JobTypeName}' saved." });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/jobtypes/toggle?code=X
        [HttpPost, Route("toggle")]
        public HttpResponseMessage ToggleJobType([FromUri] string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "code required." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var current = conn.QueryFirstOrDefault<JobTypeModel>(
                        "SELECT * FROM SchedulerJobTypes WHERE JobTypeCode=@C", new { C = code });
                    if (current == null)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Job type not found." });

                    bool newActive = !current.IsActive;
                    conn.Execute("UPDATE SchedulerJobTypes SET IsActive=@A WHERE JobTypeCode=@C",
                        new { A = newActive, C = code });

                    LoggingService.SaveAuditLog("SchedulerJobTypes", code, "UPDATE",
                        new { current.IsActive }, new { IsActive = newActive });

                    return Request.CreateResponse(HttpStatusCode.OK,
                        new { isActive = newActive, message = newActive ? "Job Type enabled." : "Job Type disabled." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
