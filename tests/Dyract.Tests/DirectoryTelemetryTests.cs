using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Dyract.Crypto.Identity;
using Dyract.Protocol;
using Dyract.Server;
using Dyract.Server.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Dyract.Tests;

public sealed class DirectoryTelemetryTests
{
    [Theory]
    [InlineData("/health", "health")]
    [InlineData("/api/v1/identity/challenge", "identity.challenge")]
    [InlineData("/api/v1/identity/register", "identity.register")]
    [InlineData("/api/v1/peer/lookup", "peer.lookup")]
    [InlineData("/api/v1/presence", "presence.publish")]
    [InlineData("/api/v1/presence/remove", "presence.remove")]
    [InlineData("/api/v1/capability/revoke", "capability.revoke")]
    [InlineData("/api/v1/peer/resolve", "peer.resolve")]
    [InlineData("/api/v1/signal/send", "signal.send")]
    [InlineData("/api/v1/signal/fetch", "signal.fetch")]
    [InlineData("/api/v1/signal/ack", "signal.ack")]
    [InlineData("/api/v1/future", "api.unknown")]
    [InlineData("/something-else", "http.other")]
    public void OperationClassifier_UsesOnlyBoundedNames(string path, string expected)
    {
        Assert.Equal(expected, DirectoryTelemetryMiddleware.ClassifyOperation(path));
    }

    [Theory]
    [InlineData(typeof(TimeoutException), "timeout")]
    [InlineData(typeof(OperationCanceledException), "canceled")]
    [InlineData(typeof(System.Security.Cryptography.CryptographicException), "cryptography")]
    [InlineData(typeof(IOException), "io")]
    [InlineData(typeof(InvalidOperationException), "internal")]
    public void FailureClassifier_UsesOnlyBoundedCategories(Type exceptionType, string expected)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType, "sensitive detail")!;

        Assert.Equal(expected, DirectoryTelemetryMiddleware.ClassifyFailure(exception));
    }

    [Fact]
    public async Task RequestLog_DoesNotCopyRequestPayloadOrDynamicPathData()
    {
        var provider = new CapturingTelemetryLoggerProvider();
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureLogging(logging => logging.AddProvider(provider));
            });
        using var client = factory.CreateClient();
        const string sensitiveSentinel = "PRIVATE-SENTINEL-DO-NOT-LOG-9e87f1";

        using var response = await client.PostAsJsonAsync(
            "/api/v1/identity/challenge",
            new RegistrationChallengeRequest(sensitiveSentinel));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = Assert.Single(provider.Messages);
        Assert.Contains("identity.challenge", message, StringComparison.Ordinal);
        Assert.Contains("status 400", message, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveSentinel, message, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/v1/identity/challenge", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionUnhandledFailure_ReturnsAndLogsOnlyBoundedFailureData()
    {
        const string sensitiveSentinel = "redis://secret-user:secret-password@203.0.113.9:6380/private";
        var provider = new CapturingAllLoggerProvider();
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Production);
                builder.ConfigureLogging(logging => logging.AddProvider(provider));
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IRegistrationChallengeStore>();
                    services.AddSingleton<IRegistrationChallengeStore>(
                        new ThrowingRegistrationChallengeStore(sensitiveSentinel));
                });
            });
        using var client = factory.CreateClient();
        using var identity = PeerIdentity.Generate();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/identity/challenge",
            new RegistrationChallengeRequest(Convert.ToBase64String(identity.ExportPublicKey())));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("internal_error", body, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveSentinel, body, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveSentinel, string.Join('\n', provider.Messages), StringComparison.Ordinal);
        Assert.Contains(
            provider.Messages,
            message => message.Contains("identity.challenge", StringComparison.Ordinal) &&
                       message.Contains("category internal", StringComparison.Ordinal));
    }

    private sealed class CapturingTelemetryLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(
                string.Equals(
                    categoryName,
                    typeof(DirectoryTelemetryMiddleware).FullName,
                    StringComparison.Ordinal),
                Messages);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingAllLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName)
            => new CapturingLogger(enabled: true, Messages);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(
        bool enabled,
        ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => enabled && logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                messages.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed class ThrowingRegistrationChallengeStore(string message) : IRegistrationChallengeStore
    {
        public ValueTask<RegistrationChallenge> CreateAsync(
            Dyract.Core.Identity.PeerId peerId,
            byte[] publicKey,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(message);

        public ValueTask<RegistrationChallenge?> GetAsync(
            string challengeId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<bool> TryConsumeAsync(
            string challengeId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
