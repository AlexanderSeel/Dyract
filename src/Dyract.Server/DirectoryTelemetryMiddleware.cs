using System.Diagnostics;
using System.Diagnostics.Metrics;
using Dyract.Protocol;
using Microsoft.Extensions.Hosting;

namespace Dyract.Server;

/// <summary>
/// Records coarse directory operation metrics without copying request bodies, identities,
/// network candidates, client addresses, nonces, capability IDs or other dynamic protocol data.
/// In production it also terminates ordinary unhandled request failures with a generic response
/// so exception messages containing infrastructure or protocol metadata do not fall through to
/// default hosting diagnostics.
/// </summary>
public sealed class DirectoryTelemetryMiddleware
{
    private static readonly Meter Meter = new("Dyract.Directory", "1.0.0");
    private static readonly Counter<long> RequestCounter = Meter.CreateCounter<long>(
        "dyract.directory.requests",
        unit: "requests",
        description: "Directory HTTP requests by bounded operation and status class.");
    private static readonly Histogram<double> DurationHistogram = Meter.CreateHistogram<double>(
        "dyract.directory.request.duration",
        unit: "ms",
        description: "Directory request duration by bounded operation and status class.");
    private static readonly Counter<long> FailureCounter = Meter.CreateCounter<long>(
        "dyract.directory.failures",
        unit: "failures",
        description: "Directory requests ending in 5xx or unhandled exceptions.");

    private readonly RequestDelegate _next;
    private readonly ILogger<DirectoryTelemetryMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public DirectoryTelemetryMiddleware(
        RequestDelegate next,
        ILogger<DirectoryTelemetryMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var operation = ClassifyOperation(context.Request.Path);
        var started = Stopwatch.GetTimestamp();
        var statusCode = StatusCodes.Status500InternalServerError;

        try
        {
            await _next(context);
            statusCode = context.Response.StatusCode;
        }
        catch (Exception exception) when (
            _environment.IsProduction() &&
            !context.Response.HasStarted &&
            !context.RequestAborted.IsCancellationRequested)
        {
            Record(operation, StatusCodes.Status500InternalServerError, started);
            _logger.LogError(
                DirectoryTelemetryLogEvents.RequestFailed,
                "Directory operation {Operation} failed with category {FailureCategory}.",
                operation,
                ClassifyFailure(exception));

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(
                new ApiError("internal_error", "The directory request could not be completed."),
                cancellationToken: context.RequestAborted);
            return;
        }
        catch
        {
            Record(operation, StatusCodes.Status500InternalServerError, started);
            throw;
        }

        Record(operation, statusCode, started);
    }

    public static string ClassifyOperation(PathString path)
    {
        if (path == new PathString("/health"))
        {
            return "health";
        }

        if (path == new PathString("/api/v1/identity/challenge"))
        {
            return "identity.challenge";
        }

        if (path == new PathString("/api/v1/identity/register"))
        {
            return "identity.register";
        }

        if (path == new PathString("/api/v1/peer/lookup"))
        {
            return "peer.lookup";
        }

        if (path == new PathString("/api/v1/presence"))
        {
            return "presence.publish";
        }

        if (path == new PathString("/api/v1/presence/remove"))
        {
            return "presence.remove";
        }

        if (path == new PathString("/api/v1/capability/revoke"))
        {
            return "capability.revoke";
        }

        if (path == new PathString("/api/v1/peer/resolve"))
        {
            return "peer.resolve";
        }

        if (path == new PathString("/api/v1/signal/send"))
        {
            return "signal.send";
        }

        if (path == new PathString("/api/v1/signal/fetch"))
        {
            return "signal.fetch";
        }

        if (path == new PathString("/api/v1/signal/ack"))
        {
            return "signal.ack";
        }

        return path.StartsWithSegments("/api/v1") ? "api.unknown" : "http.other";
    }

    public static string ClassifyFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            TimeoutException => "timeout",
            OperationCanceledException => "canceled",
            System.Security.Cryptography.CryptographicException => "cryptography",
            IOException => "io",
            _ => "internal"
        };
    }

    private void Record(string operation, int statusCode, long started)
    {
        var statusClass = $"{Math.Clamp(statusCode / 100, 0, 9)}xx";
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var tags = new TagList
        {
            { "operation", operation },
            { "status_class", statusClass }
        };

        RequestCounter.Add(1, tags);
        DurationHistogram.Record(elapsedMilliseconds, tags);

        if (statusCode >= StatusCodes.Status500InternalServerError)
        {
            FailureCounter.Add(1, tags);
        }

        _logger.LogInformation(
            DirectoryTelemetryLogEvents.RequestCompleted,
            "Directory operation {Operation} completed with status {StatusCode} in {ElapsedMilliseconds:F1} ms.",
            operation,
            statusCode,
            elapsedMilliseconds);
    }
}

internal static class DirectoryTelemetryLogEvents
{
    public static readonly EventId RequestCompleted = new(1000, nameof(RequestCompleted));
    public static readonly EventId RequestFailed = new(1001, nameof(RequestFailed));
}
