using StackExchange.Redis;

namespace Dyract.Server.Services;

/// <summary>
/// Verifies configured shared transient state before the directory starts serving requests.
/// Connection details are intentionally not logged.
/// </summary>
public sealed class RedisTransientStateInitializer : IHostedService
{
    private readonly IConnectionMultiplexer _connection;
    private readonly ILogger<RedisTransientStateInitializer> _logger;

    public RedisTransientStateInitializer(
        IConnectionMultiplexer connection,
        ILogger<RedisTransientStateInitializer> logger)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _connection.GetDatabase().PingAsync();
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation("Redis shared transient state is available.");
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}
