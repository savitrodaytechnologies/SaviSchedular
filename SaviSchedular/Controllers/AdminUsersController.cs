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
    [RoutePrefix("api/users")]
    public class AdminUsersController : ApiController
    {
        private static string ConnStr
            => ConfigurationManager.ConnectionStrings["SaviSchedularConnection"].ConnectionString;

        private string ClientIp
            => (Request.Properties.ContainsKey("MS_HttpContext")
                ? ((System.Web.HttpContextWrapper)Request.Properties["MS_HttpContext"]).Request.UserHostAddress
                : "unknown");

        // GET /api/users
        [HttpGet, Route("")]
        public HttpResponseMessage GetAll()
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    AuthController.EnsureAdminUsersSchema(conn);
                    var list = conn.Query<AdminUserModel>(
                        "SELECT UserId, Username, FullName, Email, Role, IsActive, CreatedAt, LastLoginAt FROM AdminUsers ORDER BY Username").AsList();
                    return Request.CreateResponse(HttpStatusCode.OK, list);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // GET /api/users/{id}
        [HttpGet, Route("{id:int}")]
        public HttpResponseMessage GetById(int id)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    AuthController.EnsureAdminUsersSchema(conn);
                    var user = conn.QueryFirstOrDefault<AdminUserModel>(
                        "SELECT UserId, Username, FullName, Email, Role, IsActive, CreatedAt, LastLoginAt FROM AdminUsers WHERE UserId = @Id", new { Id = id });

                    if (user == null)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "User not found." });

                    return Request.CreateResponse(HttpStatusCode.OK, user);
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/users/save
        [HttpPost, Route("save")]
        public HttpResponseMessage Save([FromBody] SaveAdminUserRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.FullName))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "Username and FullName are required." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    AuthController.EnsureAdminUsersSchema(conn);

                    if (req.UserId == 0)
                    {
                        // INSERT - Password required
                        if (string.IsNullOrWhiteSpace(req.Password))
                            return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "Password is required for new users." });

                        int existing = conn.ExecuteScalar<int>(
                            "SELECT COUNT(1) FROM AdminUsers WHERE Username = @Username", new { req.Username });
                        if (existing > 0)
                            return Request.CreateResponse(HttpStatusCode.Conflict, new { error = "Username already exists." });

                        string salt = Guid.NewGuid().ToString("N");
                        string hash = AuthController.HashPassword(req.Password.Trim(), salt);

                        var newId = conn.ExecuteScalar<int>(@"
                            INSERT INTO AdminUsers (Username, PasswordHash, Salt, FullName, Email, Role, IsActive, CreatedAt)
                            VALUES (@Username, @Hash, @Salt, @FullName, @Email, @Role, @IsActive, GETDATE());
                            SELECT CAST(SCOPE_IDENTITY() AS INT);",
                            new {
                                req.Username, Hash = hash, Salt = salt, req.FullName, req.Email,
                                Role = req.Role ?? "Admin", req.IsActive
                            });

                        LoggingService.SaveAuditLog("AdminUsers", newId.ToString(), "INSERT", null, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { userId = newId, message = "Admin User created." });
                    }
                    else
                    {
                        // UPDATE
                        var old = conn.QueryFirstOrDefault<AdminUserModel>("SELECT * FROM AdminUsers WHERE UserId = @Id", new { Id = req.UserId });
                        if (old == null)
                            return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "User not found." });

                        string hashToSave = old.PasswordHash;
                        string saltToSave = old.Salt;

                        if (!string.IsNullOrWhiteSpace(req.Password) && req.Password != "••••••••")
                        {
                            saltToSave = Guid.NewGuid().ToString("N");
                            hashToSave = AuthController.HashPassword(req.Password.Trim(), saltToSave);
                        }

                        conn.Execute(@"
                            UPDATE AdminUsers SET
                                Username     = @Username,
                                PasswordHash = @PasswordHash,
                                Salt         = @Salt,
                                FullName     = @FullName,
                                Email        = @Email,
                                Role         = @Role,
                                IsActive     = @IsActive
                            WHERE UserId = @UserId",
                            new {
                                req.Username, PasswordHash = hashToSave, Salt = saltToSave,
                                req.FullName, req.Email, Role = req.Role ?? "Admin",
                                req.IsActive, req.UserId
                            });

                        LoggingService.SaveAuditLog("AdminUsers", req.UserId.ToString(), "UPDATE", old, req, "Admin", ClientIp);
                        return Request.CreateResponse(HttpStatusCode.OK, new { message = "Admin User updated." });
                    }
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // POST /api/users/reset-password
        [HttpPost, Route("reset-password")]
        public HttpResponseMessage ResetPassword([FromBody] ResetPasswordRequest req)
        {
            if (req == null || req.UserId <= 0 || string.IsNullOrWhiteSpace(req.NewPassword))
                return Request.CreateResponse(HttpStatusCode.BadRequest, new { error = "UserId and NewPassword are required." });

            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    AuthController.EnsureAdminUsersSchema(conn);

                    string salt = Guid.NewGuid().ToString("N");
                    string hash = AuthController.HashPassword(req.NewPassword.Trim(), salt);

                    int rows = conn.Execute(@"
                        UPDATE AdminUsers SET PasswordHash = @Hash, Salt = @Salt WHERE UserId = @UserId",
                        new { Hash = hash, Salt = salt, req.UserId });

                    if (rows == 0)
                        return Request.CreateResponse(HttpStatusCode.NotFound, new { error = "User not found." });

                    LoggingService.SaveAuditLog("AdminUsers", req.UserId.ToString(), "RESET_PASSWORD", null, new { req.UserId }, "Admin", ClientIp);
                    return Request.CreateResponse(HttpStatusCode.OK, new { message = "Password reset successfully." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }

        // DELETE /api/users/{id}
        [HttpDelete, Route("{id:int}")]
        public HttpResponseMessage Delete(int id)
        {
            try
            {
                using (var conn = new SqlConnection(ConnStr))
                {
                    conn.Open();
                    AuthController.EnsureAdminUsersSchema(conn);
                    conn.Execute("UPDATE AdminUsers SET IsActive = 0 WHERE UserId = @Id", new { Id = id });
                    LoggingService.SaveAuditLog("AdminUsers", id.ToString(), "DELETE", null, null, "Admin", ClientIp);
                    return Request.CreateResponse(HttpStatusCode.OK, new { message = "User deactivated." });
                }
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError, new { error = ex.Message });
            }
        }
    }
}
