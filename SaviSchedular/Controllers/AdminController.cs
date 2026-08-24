using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Web;
using System.Web.Http;

namespace SaviSchedular.Controllers
{
    /// <summary>
    /// /admin URL par HTML Admin Interface serve karta hai
    /// </summary>
    [RoutePrefix("admin")]
    public class AdminController : ApiController
    {
        [HttpGet, Route("")]
        public HttpResponseMessage GetAdminUI()
        {
            try
            {
                string htmlPath = HttpContext.Current.Server.MapPath("~/admin.html");
                string html = File.ReadAllText(htmlPath, Encoding.UTF8);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html")
                };
                return response;
            }
            catch (Exception ex)
            {
                return Request.CreateResponse(HttpStatusCode.InternalServerError,
                    new { error = $"Admin UI load error: {ex.Message}" });
            }
        }
    }
}
