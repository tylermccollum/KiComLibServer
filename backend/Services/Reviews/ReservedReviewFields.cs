using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace KiComLibBackend.Services.Reviews;

/// <summary>Reject forged server fields before ordinary part/import deserialization.</summary>
public sealed class ReservedReviewFields : IAsyncResourceFilter, IExceptionFilter
{
    public static bool IsReserved(string name) => name.Equals("KiComLibUUID", StringComparison.OrdinalIgnoreCase)
        || name.Equals("KiComLibRevision", StringComparison.OrdinalIgnoreCase)
        || name.Equals("KiComLibReviewState", StringComparison.OrdinalIgnoreCase);

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        string path = request.Path.Value?.ToLowerInvariant() ?? "";
        if ((request.Method == "POST" || request.Method == "PUT" || request.Method == "PATCH")
            && (path == "/api/part" || path.StartsWith("/api/part/", StringComparison.Ordinal) || path == "/api/commit")
            && request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            request.EnableBuffering();
            try
            {
                using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: context.HttpContext.RequestAborted);
                if (Contains(document.RootElement))
                {
                    context.Result = new BadRequestObjectResult(new { error = "KiComLibUUID, KiComLibRevision and KiComLibReviewState are server-managed fields" });
                    return;
                }
            }
            catch (JsonException) { /* Let the normal model binder report malformed JSON. */ }
            finally { request.Body.Position = 0; }
        }
        await next();
    }

    private static bool Contains(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.Object => node.EnumerateObject().Any(p => IsReserved(p.Name) || Contains(p.Value)),
        JsonValueKind.Array => node.EnumerateArray().Any(Contains),
        _ => false
    };

    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not ReviewException exception) return;
        context.Result = new ObjectResult(new { error = exception.Message }) { StatusCode = exception.Status };
        context.ExceptionHandled = true;
    }
}
