using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace KSol.RDPGateway.Controllers;

/// <summary>
/// Renders styled error pages for both 4xx status codes (via
/// <c>UseStatusCodePagesWithReExecute("/error/{0}")</c>) and unhandled exceptions (via
/// <c>UseExceptionHandler("/error/500")</c>). Open to everyone — error pages must render for
/// anonymous users too.
/// </summary>
[Route("error")]
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class ErrorController : Controller
{
    [Route("{code:int?}")]
    public IActionResult Index(int? code)
    {
        var status = code ?? 500;

        // Honor the status code on the response itself so clients/proxies/log scrapers see the truth,
        // not a 200 wrapping an error page.
        Response.StatusCode = status;

        var vm = new ErrorPageViewModel
        {
            StatusCode = status,
            Title = TitleFor(status),
            Message = MessageFor(status),
            Icon = IconFor(status),
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
        };
        return View("Error", vm);
    }

    private static string TitleFor(int code) => code switch
    {
        400 => "Bad request",
        401 => "Sign-in required",
        403 => "Access denied",
        404 => "Page not found",
        408 => "Request timed out",
        429 => "Too many requests",
        >= 500 => "Something went wrong",
        _ => "Error",
    };

    private static string MessageFor(int code) => code switch
    {
        400 => "The request couldn't be understood. Please check the address and try again.",
        401 => "You need to sign in to view this page.",
        403 => "You don't have permission to access this resource.",
        404 => "The page you're looking for doesn't exist or has moved.",
        408 => "The server took too long to receive the request. Please try again.",
        429 => "You've made too many requests in a short time. Please wait a moment and try again.",
        >= 500 => "An unexpected error occurred while processing your request. The team has been notified.",
        _ => "An error occurred while processing your request.",
    };

    private static string IconFor(int code) => code switch
    {
        401 or 403 => "ri-lock-2-line",
        404 => "ri-compass-3-line",
        429 => "ri-speed-up-line",
        >= 500 => "ri-error-warning-line",
        _ => "ri-error-warning-line",
    };
}

public class ErrorPageViewModel
{
    public int StatusCode { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string Icon { get; set; } = "ri-error-warning-line";
    public string? RequestId { get; set; }
    public bool ShowRequestId => StatusCode >= 500 && !string.IsNullOrEmpty(RequestId);
}
