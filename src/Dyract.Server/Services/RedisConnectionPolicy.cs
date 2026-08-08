using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Dyract.Server.Services;

public static class RedisConnectionPolicy
{
    public static void Validate(ConfigurationOptions options, string environmentName)
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
    }
}
