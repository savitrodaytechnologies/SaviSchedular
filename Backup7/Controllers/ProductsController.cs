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
using SaviSchedular.Services.Security;

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

        private static void EnsureProductsSchema(SqlConnection conn)
        {
            try
            {
                string sql = @"
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'AuthType')
                    BEGIN
                        ALTER TABLE [dbo].[Products] ADD 
                            [AuthType]     NVARCHAR(50)  NULL,
                            [TokenUrl]     NVARCHAR(500) NULL,
                            [ClientId]     NVARCHAR(200) NULL,
                            [ClientSecret] NVARCHAR(500) NULL;
                    END
                    IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Products') AND name = 'RsaPrivateKey')
                    BEGIN
                        ALTER TABLE [dbo].[Products] ADD 
                            [RsaPrivateKey] NVARCHAR(MAX) NULL,
                            [RsaPublicKey]  NVARCHAR(MAX) NULL,
                            [Audience]      NVARCHAR(200) NULL,
                            [Issuer]        NVARCHAR(200) NULL;
                    END";
                conn.Execute(sql);
            }
            catch { }
        }

        // GET /api/products/generate-rsa-keys
        [HttpGet, Route("generate-rsa-keys")]
        public HttpResponseMessage GenerateRsaKeys()
        {
            try
            {
                var keys = Rs256JwtService.GenerateKeyPair();
                return Request.CreateResponse(HttpStatusCode.OK, keys);
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // GET /api/products
        [HttpGet, Route("")]
        public HttpResponseMessage GetAll()
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    EnsureProductsSchema(conn);
                    var list = conn.Query<ProductModel>(
                        "SELECT ProductId, ProductCode, ProductName, BaseUrl, TokenType, TokenHeaderName, AuthType, TokenUrl, ClientId, Audience, Issuer, Description, IsActive, CreatedAt, CreatedBy FROM Products ORDER BY ProductName").AsList();
                    // Never expose ApiToken, ClientSecret, or RsaPrivateKey in list
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
                    EnsureProductsSchema(conn);
                    var item = conn.QueryFirstOrDefault<ProductModel>(
                        "SELECT * FROM Products WHERE ProductId = @Id", new { Id = id });
                    if (item == null)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "Product not found." });
                    // Mask tokens and secrets
                    if (!string.IsNullOrEmpty(item.ApiToken)) item.ApiToken = "••••••••";
                    if (!string.IsNullOrEmpty(item.ClientSecret)) item.ClientSecret = "••••••••";
                    if (!string.IsNullOrEmpty(item.RsaPrivateKey)) item.RsaPrivateKey = "••••••••";
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
                    EnsureProductsSchema(conn);
                    if (req.ProductId == 0)
                    {
                        // INSERT
                        string secretToSave = !string.IsNullOrEmpty(req.ClientSecret) && req.ClientSecret != "••••••••"
                            ? EncryptionHelper.Encrypt(req.ClientSecret)
                            : null;

                        string rsaPrivateToSave = !string.IsNullOrEmpty(req.RsaPrivateKey) && req.RsaPrivateKey != "••••••••"
                            ? EncryptionHelper.Encrypt(req.RsaPrivateKey)
                            : null;

                        var newId = conn.ExecuteScalar<int>(@"
                            INSERT INTO Products (ProductCode, ProductName, BaseUrl, ApiToken, TokenType, TokenHeaderName, AuthType, TokenUrl, ClientId, ClientSecret, RsaPrivateKey, RsaPublicKey, Audience, Issuer, Description, IsActive, CreatedAt, CreatedBy)
                            VALUES (@ProductCode, @ProductName, @BaseUrl, @ApiToken, @TokenType, @TokenHeaderName, @AuthType, @TokenUrl, @ClientId, @ClientSecret, @RsaPrivateKey, @RsaPublicKey, @Audience, @Issuer, @Description, @IsActive, @Now, @By);
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new {
                                req.ProductCode, req.ProductName, req.BaseUrl, req.ApiToken,
                                TokenType = req.TokenType ?? "Bearer",
                                TokenHeaderName = req.TokenHeaderName ?? "Authorization",
                                AuthType = req.AuthType ?? "RS256",
                                req.TokenUrl, req.ClientId, ClientSecret = secretToSave,
                                RsaPrivateKey = rsaPrivateToSave, req.RsaPublicKey,
                                Audience = string.IsNullOrWhiteSpace(req.Audience) ? req.ProductCode : req.Audience,
                                Issuer = string.IsNullOrWhiteSpace(req.Issuer) ? "SaviScheduler" : req.Issuer,
                                req.Description, req.IsActive, Now = DateTime.Now, By = "Admin"
                            });
                        LoggingService.SaveAuditLog("Products", newId.ToString(), "INSERT", null, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { productId = newId, message = "Product created." });
                    }
                    else
                    {
                        // UPDATE — only update token and secret if new non-masked values are provided
                        var old = conn.QueryFirstOrDefault<ProductModel>("SELECT * FROM Products WHERE ProductId=@Id", new { Id = req.ProductId });
                        string tokenToSave = (req.ApiToken == "••••••••" || string.IsNullOrEmpty(req.ApiToken))
                            ? old?.ApiToken
                            : req.ApiToken;

                        string secretToSave = (req.ClientSecret == "••••••••" || string.IsNullOrEmpty(req.ClientSecret))
                            ? old?.ClientSecret
                            : EncryptionHelper.Encrypt(req.ClientSecret);

                        string rsaPrivateToSave = (req.RsaPrivateKey == "••••••••" || string.IsNullOrEmpty(req.RsaPrivateKey))
                            ? old?.RsaPrivateKey
                            : EncryptionHelper.Encrypt(req.RsaPrivateKey);

                        string rsaPublicToSave = string.IsNullOrEmpty(req.RsaPublicKey)
                            ? old?.RsaPublicKey
                            : req.RsaPublicKey;

                        conn.Execute(@"
                            UPDATE Products SET
                                ProductCode     = @ProductCode,
                                ProductName     = @ProductName,
                                BaseUrl         = @BaseUrl,
                                ApiToken        = @ApiToken,
                                TokenType       = @TokenType,
                                TokenHeaderName = @TokenHeaderName,
                                AuthType        = @AuthType,
                                TokenUrl        = @TokenUrl,
                                ClientId        = @ClientId,
                                ClientSecret    = @ClientSecret,
                                RsaPrivateKey   = @RsaPrivateKey,
                                RsaPublicKey    = @RsaPublicKey,
                                Audience        = @Audience,
                                Issuer          = @Issuer,
                                Description     = @Description,
                                IsActive        = @IsActive
                            WHERE ProductId = @ProductId",
                            new {
                                req.ProductCode, req.ProductName, req.BaseUrl,
                                ApiToken = tokenToSave,
                                TokenType = req.TokenType ?? "Bearer",
                                TokenHeaderName = req.TokenHeaderName ?? "Authorization",
                                AuthType = req.AuthType ?? "RS256",
                                req.TokenUrl, req.ClientId, ClientSecret = secretToSave,
                                RsaPrivateKey = rsaPrivateToSave, RsaPublicKey = rsaPublicToSave,
                                Audience = string.IsNullOrWhiteSpace(req.Audience) ? req.ProductCode : req.Audience,
                                Issuer = string.IsNullOrWhiteSpace(req.Issuer) ? "SaviScheduler" : req.Issuer,
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
