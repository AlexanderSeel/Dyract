using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Dyract.Server.Services;

public static class RedisConnectionPolicy
{
    public const string NetworkIsolationConfirmationKey = "Dyract:Redis:NetworkIsolationConfirmed";
    public const string NetworkIsolationConfirmationEnvironmentVariable = "Dyract__Redis__NetworkIsolationConfirmed";

    public static void Validate(
        ConfigurationOptions options,
        string environmentName,
        bool? networkIsolationConfirmed = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        if (!string.Equals(environmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!options.Ssl)
        {
            throw new InvalidOperationException(
                "Production Redis must use TLS. Configure the Redis connection with ssl=true.");
        }

        if (string.IsNullOrWhiteSpace(options.Password))
        {
            throw new InvalidOperationException(
                "Production Redis must use authentication. Configure a Redis password/ACL secret through protected deployment configuration.");
        }

        if (options.AllowAdmin)
        {
            throw new InvalidOperationException(
                "Production Redis must not enable administrative commands for the Dyract directory connection.");
        }

        var isolationConfirmed = networkIsolationConfirmed ?? string.Equals(
            Environment.GetEnvironmentVariable(NetworkIsolationConfirmationEnvironmentVariable),
            bool.TrueString,
            StringComparison.OrdinalIgnoreCase);

        if (!isolationConfirmed)
        {
            throw new InvalidOperationException(
                $"Production Redis network isolation must be explicitly confirmed with {NetworkIsolationConfirmationKey}=true after private-network/firewall policy has been applied.");
        }
    }
}
