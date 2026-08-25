using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using Dapper;
using SaviSchedular.Models;
using SaviSchedular.Services;

namespace SaviSchedular.Controllers
{
    [RoutePrefix("api/products")]
    public class ProductsController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        private string ClientIp
            => (Request.Properties.ContainsKey("MS_HttpContext")
                ? ((System.Web.HttpContextWrapper)Request.Properties["MS_HttpContext"]).Request.UserHostAddress
                : "unknown");

        // GET /api/products
        [HttpGet, Route("")]
        public HttpResponseMessage GetAll()
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var list = conn.Query<ProductModel>(
                        "SELECT ProductId, ProductCode, ProductName, BaseUrl, TokenType, TokenHeaderName, Description, IsActive, CreatedAt, CreatedBy FROM Products ORDER BY ProductName").AsList();
                    // Never expose ApiToken in list
                    return Request.CreateResponse(HttpStatusCode.OK, list);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // GET /api/products/{id}
        [HttpGet, Route("{id:int}")]
        public HttpResponseMessage GetById(int id)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    var item = conn.QueryFirstOrDefault<ProductModel>(
                        "SELECT * FROM Products WHERE ProductId = @Id", new { Id = id });
                    if (item == null)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Product not found." });
                    // Mask token
                    if (!string.IsNullOrEmpty(item.ApiToken))
                        item.ApiToken = "••••••••";
                    return Request.CreateResponse(HttpStatusCode.OK, item);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/products/save
        [HttpPost, Route("save")]
        public HttpResponseMessage Save([FromBody] SaveProductRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.ProductCode) || string.IsNullOrWhiteSpace(req.BaseUrl))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "ProductCode and BaseUrl are required." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    if (req.ProductId == 0)
                    {
                        // INSERT
                        var newId = conn.ExecuteScalar<int>(@"
                            INSERT INTO Products (ProductCode, ProductName, BaseUrl, ApiToken, TokenType, TokenHeaderName, Description, IsActive, CreatedAt, CreatedBy)
                            VALUES (@ProductCode, @ProductName, @BaseUrl, @ApiToken, @TokenType, @TokenHeaderName, @Description, @IsActive, @Now, @By);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new {
                                req.ProductCode, req.ProductName, req.BaseUrl, req.ApiToken,
                                TokenType = req.TokenType ?? "Bearer",
                                TokenHeaderName = req.TokenHeaderName ?? "Authorization",
                                req.Description, req.IsActive, Now = DateTime.Now, By = "Admin"
                            });
                        LoggingService.SaveAuditLog("Products", newId.ToString(), "INSERT", null, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { productId = newId, message = "Product created." });
                    }
                    else
                    {
                        // UPDATE — only update token if a new non-masked value is provided
                        var old = conn.QueryFirstOrDefault<ProductModel>("SELECT * FROM Products WHERE ProductId=@Id", new { Id = req.ProductId });
                        string tokenToSave = (req.ApiToken == "••••••••" || string.IsNullOrEmpty(req.ApiToken))
                            ? old?.ApiToken
                            : req.ApiToken;

                        conn.Execute(@"
                            UPDATE Products SET
                                ProductCode     = @ProductCode,
                                ProductName     = @ProductName,
                                BaseUrl         = @BaseUrl,
                                ApiToken        = @ApiToken,
                                TokenType       = @TokenType,
                                TokenHeaderName = @TokenHeaderName,
                                Description     = @Description,
                                IsActive        = @IsActive
                            WHERE ProductId = @ProductId",
                            new {
                                req.ProductCode, req.ProductName, req.BaseUrl,
                                ApiToken = tokenToSave,
                                TokenType = req.TokenType ?? "Bearer",
                                TokenHeaderName = req.TokenHeaderName ?? "Authorization",
                                req.Description, req.IsActive, req.ProductId
                            });
                        LoggingService.SaveAuditLog("Products", req.ProductId.ToString(), "UPDATE", old, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { message = "Product updated." });
                    }
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // DELETE /api/products/{id}
        [HttpDelete, Route("{id:int}")]
        public HttpResponseMessage Delete(int id)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    // Check if any clients exist
                    int clientCount = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM ProductClients WHERE ProductId=@Id", new { Id = id });
                    if (clientCount > 0)
                        return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = $"Cannot delete: {clientCount} client(s) exist under this product." });

                    conn.Execute("DELETE FROM Products WHERE ProductId=@Id", new { Id = id });
                    LoggingService.SaveAuditLog("Products", id.ToString(), "DELETE", null, null, "Admin", ClientIp);
                    return Request.CreateResponse(HttpStatusCode.OK, new { message = "Product deleted." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
