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
    public class ProductJobTypesController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        private string ClientIp
            => (Request.Properties.ContainsKey("MS_HttpContext")
                ? ((System.Web.HttpContextWrapper)Request.Properties["MS_HttpContext"]).Request.UserHostAddress
                : "unknown");

        // GET /api/jobtypes?productId=1
        [HttpGet, Route("")]
        public HttpResponseMessage GetAll([FromUri] int? productId = null)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    string sql = @"
                        SELECT jt.*, p.ProductName
                        FROM ProductJobTypes jt
                        JOIN Products p ON p.ProductId = jt.ProductId";
                    if (productId.HasValue)
                        sql += " WHERE jt.ProductId = @ProductId";
                    sql += " ORDER BY p.ProductName, jt.JobTypeName";

                    var list = conn.Query<ProductJobTypeModel>(sql, new { ProductId = productId }).AsList();
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
        public HttpResponseMessage Save([FromBody] SaveJobTypeRequest req)
        {
            if (req == null || req.ProductId <= 0 || string.IsNullOrWhiteSpace(req.JobTypeCode))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "ProductId and JobTypeCode are required." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    if (req.JobTypeId == 0)
                    {
                        var newId = conn.ExecuteScalar<int>(@"
                            INSERT INTO ProductJobTypes (ProductId, JobTypeCode, JobTypeName, DefaultApiPath, HttpMethod, Description, IsActive, CreatedAt)
                            VALUES (@ProductId, @JobTypeCode, @JobTypeName, @DefaultApiPath, @HttpMethod, @Description, @IsActive, @Now);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new {
                                req.ProductId, req.JobTypeCode, req.JobTypeName, req.DefaultApiPath,
                                HttpMethod = req.HttpMethod ?? "POST", req.Description,
                                IsActive = true, Now = DateTime.Now
                            });
                        LoggingService.SaveAuditLog("ProductJobTypes", newId.ToString(), "INSERT", null, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { jobTypeId = newId, message = "Job Type created." });
                    }
                    else
                    {
                        var old = conn.QueryFirstOrDefault<ProductJobTypeModel>("SELECT * FROM ProductJobTypes WHERE JobTypeId=@Id", new { Id = req.JobTypeId });
                        conn.Execute(@"
                            UPDATE ProductJobTypes SET
                                JobTypeCode    = @JobTypeCode,
                                JobTypeName    = @JobTypeName,
                                DefaultApiPath = @DefaultApiPath,
                                HttpMethod     = @HttpMethod,
                                Description    = @Description,
                                IsActive       = @IsActive
                            WHERE JobTypeId = @JobTypeId",
                            new {
                                req.JobTypeCode, req.JobTypeName, req.DefaultApiPath,
                                HttpMethod = req.HttpMethod ?? "POST",
                                req.Description, req.IsActive, req.JobTypeId
                            });
                        LoggingService.SaveAuditLog("ProductJobTypes", req.JobTypeId.ToString(), "UPDATE", old, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { message = "Job Type updated." });
                    }
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // DELETE /api/jobtypes/{id}
        [HttpDelete, Route("{id:int}")]
        public HttpResponseMessage Delete(int id)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    int instCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM SchedulerJobInstances WHERE JobTypeId=@Id", new { Id = id });
                    if (instCount > 0)
                        return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = $"Cannot delete: {instCount} schedule(s) use this job type." });

                    conn.Execute("DELETE FROM ProductJobTypes WHERE JobTypeId=@Id", new { Id = id });
                    LoggingService.SaveAuditLog("ProductJobTypes", id.ToString(), "DELETE", null, null, "Admin", ClientIp);
                    return Request.CreateResponse(HttpStatusCode.OK, new { message = "Job Type deleted." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
