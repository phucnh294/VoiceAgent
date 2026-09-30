using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using VoiceAgent.Api.Settings;

namespace VoiceAgent.Api.Tests.Settings;

public class LocalOnlyAttributeTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void OnActionExecuting_Loopback_IsAllowed(string address)
    {
        var context = ContextFrom(address);

        new LocalOnlyAttribute().OnActionExecuting(context);

        Assert.Null(context.Result);
    }

    [Theory]
    [InlineData("192.168.1.20")]
    [InlineData("10.0.0.5")]
    public void OnActionExecuting_RemoteAddress_IsForbidden(string address)
    {
        var context = ContextFrom(address);

        new LocalOnlyAttribute().OnActionExecuting(context);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    private static ActionExecutingContext ContextFrom(string address)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        return new ActionExecutingContext(
            new ActionContext(http, new RouteData(), new ActionDescriptor()),
            [],
            new Dictionary<string, object?>(),
            controller: new object());
    }
}
