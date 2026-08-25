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
    [RoutePrefix("api/clients")]
    public class ProductClientsController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        private string ClientIp
            => (Request.Properties.ContainsKey("MS_HttpContext")
                ? ((System.Web.HttpContextWrapper)Request.Properties["MS_HttpContext"]).Request.UserHostAddress
                : "unknown");

        // GET /api/clients?productId=1
        [HttpGet, Route("")]
        public HttpResponseMessage GetAll([FromUri] int? productId = null, [FromUri] string q = null)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    string sql = @"
                        SELECT pc.*, p.ProductName
                        FROM ProductClients pc
                        JOIN Products p ON p.ProductId = pc.ProductId
                        WHERE 1=1";
                    var p = new DynamicParameters();
                    if (productId.HasValue && productId.Value > 0)
                    {
                        sql += " AND pc.ProductId = @ProductId";
                        p.Add("ProductId", productId.Value);
                    }
                    if (!string.IsNullOrWhiteSpace(q))
                    {
                        sql += " AND (pc.ClientName LIKE @Q OR pc.ExternalId LIKE @Q)";
                        p.Add("Q", "%" + q.Trim() + "%");
                    }
                    sql += " ORDER BY p.ProductName, pc.ClientName";

                    var list = conn.Query<ProductClientModel>(sql, p).AsList();
                    return Request.CreateResponse(HttpStatusCode.OK, list);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/clients/save
        [HttpPost, Route("save")]
        public HttpResponseMessage Save([FromBody] SaveClientRequest req)
        {
            if (req == null || req.ProductId <= 0 || string.IsNullOrWhiteSpace(req.ExternalId))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "ProductId and ExternalId are required." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    if (req.ClientId == 0)
                    {
                        var newId = conn.ExecuteScalar<long>(@"
                            INSERT INTO ProductClients (ProductId, ClientName, ExternalId, CustomBaseUrl, IsActive, CreatedAt, CreatedBy)
                            VALUES (@ProductId, @ClientName, @ExternalId, @CustomBaseUrl, @IsActive, @Now, @By);
                            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                            new {
                                req.ProductId, req.ClientName, req.ExternalId,
                                req.CustomBaseUrl, req.IsActive, Now = DateTime.Now, By = "Admin"
                            });
                        LoggingService.SaveAuditLog("ProductClients", newId.ToString(), "INSERT", null, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { clientId = newId, message = "Client created." });
                    }
                    else
                    {
                        var old = conn.QueryFirstOrDefault<ProductClientModel>("SELECT * FROM ProductClients WHERE ClientId=@Id", new { Id = req.ClientId });
                        conn.Execute(@"
                            UPDATE ProductClients SET
                                ClientName    = @ClientName,
                                ExternalId    = @ExternalId,
                                CustomBaseUrl = @CustomBaseUrl,
                                IsActive      = @IsActive
                            WHERE ClientId = @ClientId",
                            new { req.ClientName, req.ExternalId, req.CustomBaseUrl, req.IsActive, req.ClientId });
                        LoggingService.SaveAuditLog("ProductClients", req.ClientId.ToString(), "UPDATE", old, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { message = "Client updated." });
                    }
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // DELETE /api/clients/{id}
        [HttpDelete, Route("{id:long}")]
        public HttpResponseMessage Delete(long id)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    int instCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM SchedulerJobInstances WHERE ClientId=@Id", new { Id = id });
                    if (instCount > 0)
                        return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = $"Cannot delete: {instCount} schedule(s) exist for this client." });

                    conn.Execute("DELETE FROM ProductClients WHERE ClientId=@Id", new { Id = id });
                    LoggingService.SaveAuditLog("ProductClients", id.ToString(), "DELETE", null, null, "Admin", ClientIp);
                    return Request.CreateResponse(HttpStatusCode.OK, new { message = "Client deleted." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
