using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace VoiceAgent.Api.Settings;

/// <summary>
/// Rejects requests that don't come from this machine. The app has no login, so endpoints that
/// read or change API keys and prompts must not be reachable from the network. Requests through
/// the <c>ng serve</c> dev proxy arrive from loopback and are allowed.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class LocalOnlyAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var remote = context.HttpContext.Connection.RemoteIpAddress;
        if (remote is { IsIPv4MappedToIPv6: true })
        {
            remote = remote.MapToIPv4(); // ::ffff:127.0.0.1 is loopback too.
        }
        if (remote is null || !System.Net.IPAddress.IsLoopback(remote))
        {
            context.Result = new ObjectResult("Settings can only be changed from the machine running the API.")
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }
    }
}
