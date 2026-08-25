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
    /// <summary>
    /// Manages API Client keys for external projects that integrate with SaviSchedular
    /// </summary>
    [RoutePrefix("api/apiclients")]
    public class ApiClientsController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        private string ClientIp
            => (Request.Properties.ContainsKey("MS_HttpContext")
                ? ((System.Web.HttpContextWrapper)Request.Properties["MS_HttpContext"]).Request.UserHostAddress
                : "unknown");

        // GET /api/apiclients
        [HttpGet, Route("")]
        public HttpResponseMessage GetAll()
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var list = conn.Query<ApiClientModel>(@"
                        SELECT ApiClientId, ClientName,
                               LEFT(ApiKey,8)+'••••••••' AS ApiKey,
                               AllowedProductIds, IsActive, CreatedAt, CreatedBy, LastUsedAt
                        FROM ApiClients ORDER BY ClientName").AsList();
                    return Request.CreateResponse(HttpStatusCode.OK, list);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/apiclients/save
        [HttpPost, Route("save")]
        public HttpResponseMessage Save([FromBody] SaveApiClientRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ClientName))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "ClientName is required." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    if (req.ApiClientId == 0)
                    {
                        // Generate a secure UUID-based API key
                        string newKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                        var newId = conn.ExecuteScalar<int>(@"
                            INSERT INTO ApiClients (ClientName, ApiKey, AllowedProductIds, IsActive, CreatedAt, CreatedBy)
                            VALUES (@ClientName, @ApiKey, @AllowedProductIds, @IsActive, @Now, @By);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new { req.ClientName, ApiKey = newKey, req.AllowedProductIds, req.IsActive, Now = DateTime.Now, By = "Admin" });

                        LoggingService.SaveAuditLog("ApiClients", newId.ToString(), "INSERT", null, new { req.ClientName }, "Admin", ClientIp);
                        // Return full key ONCE on creation
                        return Request.CreateResponse(HttpStatusCode.OK, new { apiClientId = newId, apiKey = newKey, message = "API Client created. Copy the key now — it won't be shown again." });
                    }
                    else
                    {
                        conn.Execute(@"
                            UPDATE ApiClients SET
                                ClientName        = @ClientName,
                                AllowedProductIds = @AllowedProductIds,
                                IsActive          = @IsActive
                            WHERE ApiClientId = @ApiClientId",
                            new { req.ClientName, req.AllowedProductIds, req.IsActive, req.ApiClientId });
                        LoggingService.SaveAuditLog("ApiClients", req.ApiClientId.ToString(), "UPDATE", null, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { message = "API Client updated." });
                    }
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/apiclients/{id}/regenerate — Generate a new key
        [HttpPost, Route("{id:int}/regenerate")]
        public HttpResponseMessage RegenerateKey(int id)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    string newKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                    conn.Execute("UPDATE ApiClients SET ApiKey=@Key WHERE ApiClientId=@Id", new { Key = newKey, Id = id });
                    LoggingService.SaveAuditLog("ApiClients", id.ToString(), "REGENERATE_KEY", null, null, "Admin", ClientIp);
                    return Request.CreateResponse(HttpStatusCode.OK, new { apiKey = newKey, message = "Key regenerated. Copy it now." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // DELETE /api/apiclients/{id}
        [HttpDelete, Route("{id:int}")]
        public HttpResponseMessage Delete(int id)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    conn.Execute("DELETE FROM ApiClients WHERE ApiClientId=@Id", new { Id = id });
                    LoggingService.SaveAuditLog("ApiClients", id.ToString(), "DELETE", null, null, "Admin", ClientIp);
                    return Request.CreateResponse(HttpStatusCode.OK, new { message = "API Client deleted." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
