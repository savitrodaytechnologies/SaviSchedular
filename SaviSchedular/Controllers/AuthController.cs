using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Web.Http;
using Dapper;
using SaviSchedular.Models;
using SaviSchedular.Services;

namespace SaviSchedular.Controllers
{
    [RoutePrefix("api/auth")]
    public class AuthController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        private string ClientIp
            => (Request.Properties.ContainsKey("MS_HttpContext")
                ? ((System.Web.HttpContextWrapper)Request.Properties["MS_HttpContext"]).Request.UserHostAddress
                : "unknown");

        public static void EnsureAdminUsersSchema(SqlConnection conn)
        {
            try
            {
                string sql = @"
                    IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'AdminUsers' AND type = 'U')
                    BEGIN
                        CREATE TABLE [dbo].[AdminUsers] (
                            [UserId]       INT            IDENTITY(1,1) NOT NULL PRIMARY KEY,
                            [Username]     NVARCHAR(100)  NOT NULL UNIQUE,
                            [PasswordHash] NVARCHAR(500)  NOT NULL,
                            [Salt]         NVARCHAR(100)  NOT NULL,
                            [FullName]     NVARCHAR(200)  NOT NULL,
                            [Email]        NVARCHAR(200)  NULL,
                            [Role]         NVARCHAR(50)   NOT NULL DEFAULT 'SuperAdmin',
                            [IsActive]     BIT            NOT NULL DEFAULT 1,
                            [CreatedAt]    DATETIME       NOT NULL DEFAULT GETDATE(),
                            [LastLoginAt]  DATETIME       NULL
                        );
                    END";
                conn.Execute(sql);

                // Seed default super-admin if table is empty
                int count = conn.ExecuteScalar<int>("SELECT COUNT(1) FROM AdminUsers");
                if (count == 0)
                {
                    string salt = Guid.NewGuid().ToString("N");
                    string hash = HashPassword("Admin@123", salt);
                    conn.Execute(@"
                        INSERT INTO AdminUsers (Username, PasswordHash, Salt, FullName, Email, Role, IsActive, CreatedAt)
                        VALUES ('admin', @Hash, @Salt, 'System Administrator', 'admin@savischools.com', 'SuperAdmin', 1, GETDATE())",
                        new { Hash = hash, Salt = salt });
                }
            }
            catch { }
        }

        public static string HashPassword(string password, string salt)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password + salt + "SaviSchedularSecret2026"));
                return Convert.ToBase64String(bytes);
            }
        }

        // POST /api/auth/login
        [HttpPost, Route("login")]
        public HttpResponseMessage Login([FromBody] LoginRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "Username and Password are required." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    EnsureAdminUsersSchema(conn);

                    var user = conn.QueryFirstOrDefault<AdminUserModel>(
                        "SELECT * FROM AdminUsers WHERE Username = @Username", new { Username = req.Username.Trim() });

                    if (user == null)
                        return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Invalid Username or Password." });

                    if (!user.IsActive)
                        return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Account is inactive. Contact Administrator." });

                    string computedHash = HashPassword(req.Password.Trim(), user.Salt);
                    if (!string.Equals(user.PasswordHash, computedHash, StringComparison.Ordinal))
                    {
                        LoggingService.SaveAuditLog("AdminUsers", user.UserId.ToString(), "LOGIN_FAILED", null, new { Username = req.Username }, "System", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Invalid Username or Password." });
                    }

                    // Record LastLoginAt
                    conn.Execute("UPDATE AdminUsers SET LastLoginAt = GETDATE() WHERE UserId = @UserId", new { user.UserId });

                    // Generate Session Token
                    string sessionToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user.UserId}:{user.Username}:{Guid.NewGuid():N}:{DateTime.UtcNow.Ticks}"));

                    LoggingService.SaveAuditLog("AdminUsers", user.UserId.ToString(), "LOGIN_SUCCESS", null, new { user.Username, user.Role }, user.Username, ClientIp);

                    return Request.CreateResponse(HttpStatusCode.OK, new LoginResponse
                    {
                        Token = sessionToken,
                        UserId = user.UserId,
                        Username = user.Username,
                        FullName = user.FullName,
                        Role = user.Role
                    });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // GET /api/auth/me
        [HttpGet, Route("me")]
        public HttpResponseMessage GetCurrentUser()
        {
            try
            {
                var authHeader = Request.Headers.Authorization;
                if (authHeader == null || string.IsNullOrWhiteSpace(authHeader.Parameter))
                    return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Unauthenticated." });

                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authHeader.Parameter));
                string[] parts = decoded.Split(':');
                if (parts.Length < 2 || !int.TryParse(parts[0], out int userId))
                    return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Invalid session token." });

                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    EnsureAdminUsersSchema(conn);
                    var user = conn.QueryFirstOrDefault<AdminUserModel>(
                        "SELECT UserId, Username, FullName, Email, Role, IsActive, CreatedAt, LastLoginAt FROM AdminUsers WHERE UserId = @UserId",
                        new { UserId = userId });

                    if (user == null || !user.IsActive)
                        return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "User not found or inactive." });

                    return Request.CreateResponse(HttpStatusCode.OK, user);
                }
            }
            catch
            {
                return Request.CreateResponse(HttpStatusCode.Unauthorized, new { error = "Invalid session." });
            }
        }

        // POST /api/auth/logout
        [HttpPost, Route("logout")]
        public HttpResponseMessage Logout()
        {
            return Request.CreateResponse(HttpStatusCode.OK, new { message = "Logged out successfully." });
        }
    }
}
