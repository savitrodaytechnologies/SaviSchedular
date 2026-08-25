using System.Net;
using System.Net.Http;
using System.Web.Http;

namespace SaviSchedular.Controllers
{
    /// <summary>
    /// Redirects /admin to /admin.html
    /// </summary>
    [RoutePrefix("admin")]
    public class AdminController : ApiController
    {
        [HttpGet, Route("")]
        public HttpResponseMessage Index()
        {
            var response = Request.CreateResponse(HttpStatusCode.Redirect);
            response.Headers.Location = new System.Uri(Request.RequestUri, "/admin.html");
            return response;
        }
    }
}
