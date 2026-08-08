using Dyract.Protocol;
using Dyract.Server.Services;

namespace Dyract.Server;

public sealed class GlobalRequestRateLimitMiddleware
{
    private readonly RequestDelegate _next;

    public GlobalRequestRateLimitMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IGlobalRequestLimiter limiter,
        TimeProvider timeProvider)
    {
        var category = GetCategory(context.Request.Path);
        if (category is null)
        {
            await _next(context);
            return;
        }

        var decision = await limiter.AcquireAsync(
            category.Value,
            GetClientPartitionKey(context),
            timeProvider.GetUtcNow(),
            context.RequestAborted);

        if (decision.IsAllowed)
        {
            await _next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.ContentType = "application/json";
        context.Response.Headers.RetryAfter = Math.Max(
            1,
            (int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(
            new ApiError("rate_limited", "Too many directory requests. Retry after the current rate-limit window."),
            cancellationToken: context.RequestAborted);
    }

    internal static DirectoryRateLimitCategory? GetCategory(PathString path)
    {
        if (path == new PathString("/api/v1/identity/challenge") ||
            path == new PathString("/api/v1/identity/register"))
        {
            return DirectoryRateLimitCategory.Registration;
        }

        return path.StartsWithSegments("/api/v1")
            ? DirectoryRateLimitCategory.PeerOperations
            : null;
    }

    private static string GetClientPartitionKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}
