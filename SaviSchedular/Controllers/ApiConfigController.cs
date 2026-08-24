using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using Dapper;
using SaviSchedular.Models;
using SaviSchedular.Services;

namespace SaviSchedular.Controllers
{
    [RoutePrefix("api/apiconfig")]
    public class ApiConfigController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        // GET /api/apiconfig/list?schoolId=X
        [HttpGet, Route("list")]
        public HttpResponseMessage GetConfigs([FromUri] long? schoolId = null)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    string where = schoolId.HasValue ? "WHERE c.SchoolId = @SchoolId" : "";
                    var list = conn.Query($@"
                        SELECT c.ConfigId, c.SchoolId, c.JobTypeCode, jt.JobTypeName,
                               c.BaseUrl, c.ApiPath, c.HttpMethod, c.CustomHeaders,
                               c.TimeoutMinutes, c.IsActive, c.CreatedAt, c.UpdatedAt
                        FROM   SchoolApiConfigs c
                        LEFT JOIN SchedulerJobTypes jt ON c.JobTypeCode = jt.JobTypeCode
                        {where}
                        ORDER  BY c.SchoolId, c.JobTypeCode",
                        new { SchoolId = schoolId }).AsList();
                    return Request.CreateResponse(HttpStatusCode.OK, list);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/apiconfig/save
        [HttpPost, Route("save")]
        public HttpResponseMessage SaveConfig([FromBody] SaveApiConfigRequest req)
        {
            if (req == null || req.SchoolId <= 0 || string.IsNullOrWhiteSpace(req.JobTypeCode) ||
                string.IsNullOrWhiteSpace(req.BaseUrl))
                return Request.CreateResponse(HttpStatusCode.BadRequest,
                    new { error = "SchoolId, JobTypeCode, and BaseUrl are required." });

            try
            {
                object oldVals = null;
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    oldVals = conn.QueryFirstOrDefault(@"
                        SELECT BaseUrl, ApiPath, HttpMethod
                        FROM   SchoolApiConfigs
                        WHERE  SchoolId=@S AND JobTypeCode=@J",
                        new { S = req.SchoolId, J = req.JobTypeCode });

                    conn.Execute(@"
                        IF NOT EXISTS (SELECT 1 FROM SchoolApiConfigs WHERE SchoolId=@SchoolId AND JobTypeCode=@JobTypeCode)
                            INSERT INTO SchoolApiConfigs
                                (SchoolId, JobTypeCode, BaseUrl, ApiPath, HttpMethod, CustomHeaders, TimeoutMinutes, IsActive)
                            VALUES
                                (@SchoolId, @JobTypeCode, @BaseUrl, @ApiPath, @HttpMethod, @CustomHeaders, @Timeout, 1)
                        ELSE
                            UPDATE SchoolApiConfigs SET
                                BaseUrl = @BaseUrl, ApiPath = @ApiPath, HttpMethod = @HttpMethod,
                                CustomHeaders = @CustomHeaders, TimeoutMinutes = @Timeout,
                                IsActive = 1, UpdatedAt = GETDATE()
                            WHERE SchoolId=@SchoolId AND JobTypeCode=@JobTypeCode",
                        new
                        {
                            req.SchoolId, req.JobTypeCode, req.BaseUrl, req.ApiPath,
                            HttpMethod    = req.HttpMethod ?? "POST",
                            CustomHeaders = req.CustomHeaders,
                            Timeout       = req.TimeoutMinutes > 0 ? req.TimeoutMinutes : 15
                        });
                }

                LoggingService.SaveAuditLog("SchoolApiConfigs", $"{req.SchoolId}_{req.JobTypeCode}",
                    oldVals == null ? "INSERT" : "UPDATE", oldVals, req);

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { message = $"API config saved for School {req.SchoolId} | {req.JobTypeCode}" });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // DELETE /api/apiconfig/delete?configId=X
        [HttpDelete, Route("delete")]
        public HttpResponseMessage DeleteConfig([FromUri] long configId)
        {
            if (configId <= 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "Invalid configId." });

            try
            {
                object oldVals = null;
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    oldVals = conn.QueryFirstOrDefault("SELECT * FROM SchoolApiConfigs WHERE ConfigId=@Id",
                        new { Id = configId });
                    conn.Execute("DELETE FROM SchoolApiConfigs WHERE ConfigId=@Id", new { Id = configId });
                }
                LoggingService.SaveAuditLog("SchoolApiConfigs", configId.ToString(), "DELETE", oldVals, null);
                return Request.CreateResponse(HttpStatusCode.OK, new { message = "API config deleted." });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/apiconfig/test — Connection test karo
        [HttpPost, Route("test")]
        public async Task<HttpResponseMessage> TestConnection([FromBody] TestConnectionRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.BaseUrl))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "BaseUrl required." });

            string url = $"{req.BaseUrl.TrimEnd('/')}/{(req.ApiPath ?? "").TrimStart('/')}";
            if (url.IndexOf("targetSchoolId=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                string sep = url.Contains("?") ? "&" : "?";
                url += $"{sep}targetSchoolId={req.SchoolId}";
            }
            if (url.IndexOf("testMode=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                string sep = url.Contains("?") ? "&" : "?";
                url += $"{sep}testMode=true";
            }

            try
            {
                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                    System.Net.Http.HttpResponseMessage resp;

                    if ((req.HttpMethod ?? "POST").ToUpper() == "GET")
                        resp = await client.GetAsync(url);
                    else
                        resp = await client.PostAsync(url, null);

                    string body = await resp.Content.ReadAsStringAsync();

                    return Request.CreateResponse(HttpStatusCode.OK, new
                    {
                        success        = resp.IsSuccessStatusCode,
                        statusCode     = (int)resp.StatusCode,
                        statusText     = resp.StatusCode.ToString(),
                        responseBody   = body?.Length > 500 ? body.Substring(0, 500) + "..." : body,
                        testedUrl      = url
                    });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.OK, new
                {
                    success    = false,
                    statusCode = 0,
                    statusText = "Connection Failed",
                    error      = ex.Message,
                    testedUrl  = url
                });
            }
        }
    }
}
