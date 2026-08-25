using System.Web;
using System.Web.Http;
using Swashbuckle.Application;

namespace SaviSchedular
{
    public class Global : HttpApplication
    {
        protected void Application_Start()
        {
            GlobalConfiguration.Configure(WebApiConfig.Register);
        }

        protected void Application_BeginRequest(object sender, System.EventArgs e)
        {
            // Root URL par aao to Admin UI pe redirect karo
            if (Request.AppRelativeCurrentExecutionFilePath == "~/")
            {
                Response.Redirect("~/admin");
            }
        }
    }

    public static class WebApiConfig
    {
        public static void Register(HttpConfiguration config)
        {
            // Configure Swagger
            config.EnableSwagger(c =>
                {
                    c.SingleApiVersion("v1", "SaviSchedular APIs");
                })
                .EnableSwaggerUi(c =>
                {
                    c.DocExpansion(DocExpansion.List);
                });

            config.Formatters.JsonFormatter.SerializerSettings.ContractResolver =
                new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver();

            config.MapHttpAttributeRoutes();

            config.Routes.MapHttpRoute(
                name: "DefaultApi",
                routeTemplate: "api/{controller}/{id}",
                defaults: new { id = RouteParameter.Optional }
            );
        }
    }
}
