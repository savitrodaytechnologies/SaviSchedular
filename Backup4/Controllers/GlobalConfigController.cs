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
    [RoutePrefix("api/globalconfig")]
    public class GlobalConfigController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        // GET /api/globalconfig/list
        [HttpGet, Route("list")]
        public HttpResponseMessage GetAll()
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var list = conn.Query<GlobalConfigModel>(@"
                        SELECT ConfigKey, ConfigValue, Description, UpdatedAt, UpdatedBy
                        FROM   SchedulerGlobalConfig
                        ORDER  BY ConfigKey").AsList();
                    return Request.CreateResponse(HttpStatusCode.OK, list);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/globalconfig/update
        [HttpPost, Route("update")]
        public HttpResponseMessage Update([FromBody] UpdateGlobalConfigRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ConfigKey))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "ConfigKey required." });

            try
            {
                string oldVal = null;
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    oldVal = conn.ExecuteScalar<string>(
                        "SELECT ConfigValue FROM SchedulerGlobalConfig WHERE ConfigKey=@K",
                        new { K = req.ConfigKey });

                    conn.Execute(@"
                        UPDATE SchedulerGlobalConfig SET
                            ConfigValue = @ConfigValue,
                            UpdatedAt   = GETDATE(),
                            UpdatedBy   = @UpdatedBy
                        WHERE ConfigKey = @ConfigKey",
                        new { req.ConfigKey, req.ConfigValue, UpdatedBy = req.UpdatedBy ?? "Admin" });
                }

                // Cache invalidate karo
                GlobalConfigService.Invalidate();

                LoggingService.SaveAuditLog("SchedulerGlobalConfig", req.ConfigKey, "UPDATE",
                    new { Value = oldVal }, new { Value = req.ConfigValue }, req.UpdatedBy ?? "Admin");

                return Request.CreateResponse(HttpStatusCode.OK,
                    new { message = $"Setting '{req.ConfigKey}' updated." });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/globalconfig/update-bulk — Multiple settings ek saath update karo
        [HttpPost, Route("update-bulk")]
        public HttpResponseMessage UpdateBulk([FromBody] UpdateGlobalConfigRequest[] reqs)
        {
            if (reqs == null || reqs.Length == 0)
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "No settings provided." });

            int updated = 0;
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    foreach (var req in reqs)
                    {
                        if (string.IsNullOrWhiteSpace(req?.ConfigKey)) continue;
                        conn.Execute(@"
                            UPDATE SchedulerGlobalConfig SET
                                ConfigValue = @V, UpdatedAt = GETDATE(), UpdatedBy = @By
                            WHERE ConfigKey = @K",
                            new { K = req.ConfigKey, V = req.ConfigValue, By = req.UpdatedBy ?? "Admin" });
                        updated++;
                    }
                }
                GlobalConfigService.Invalidate();
                return Request.CreateResponse(HttpStatusCode.OK,
                    new { message = $"{updated} setting(s) updated.", updated });
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
