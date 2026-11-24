using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Surveillance.Web.Investigations;

namespace Surveillance.Web.Lab;

/// <summary>
/// Demo pages under /lab answer 404 unless the active solution (settings page) is the scripted
/// fake agent, so a demo never spends model calls or touches real investigations.
/// </summary>
public sealed class LabAvailableFilter(AgentSelection selection) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!await selection.IsLabAvailableAsync(context.HttpContext.RequestAborted))
        {
            context.Result = new NotFoundResult();
            return;
        }

        await next();
    }
}
