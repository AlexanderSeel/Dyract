using Microsoft.Extensions.Configuration;

namespace Dyract.Server.Services;

public static class ProductionSecretPolicy
{
    public const string PostgreSqlConnectionName = "Dyract";
    public const string RedisConnectionName = "Redis";

    public static string? GetConnectionString(
        IConfiguration configuration,
        string connectionName,
        string environmentName,
        Func<string, string?>? environmentVariableReader = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        if (!string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase))
        {
            return Normalize(configuration.GetConnectionString(connectionName));
        }

        var environmentVariableName = GetEnvironmentVariableName(connectionName);
        var reader = environmentVariableReader ?? Environment.GetEnvironmentVariable;
        var deploymentValue = Normalize(reader(environmentVariableName));
        if (deploymentValue is not null)
        {
            // Return the deployment value directly so a lower/higher-priority IConfiguration
            // provider cannot accidentally become the production credential source.
            return deploymentValue;
        }

        var configuredValue = Normalize(configuration.GetConnectionString(connectionName));
        if (configuredValue is null)
        {
            // Optional infrastructure remains optional. If it is not configured anywhere,
            // callers may continue with their documented development/single-instance fallback.
            return null;
        }

        throw new InvalidOperationException(
            $"Production connection '{connectionName}' must be supplied through deployment secret configuration using environment variable '{environmentVariableName}'. Checked-in/appsettings/command-line connection secrets are not accepted as the production source.");
    }

    public static string GetEnvironmentVariableName(string connectionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionName);
        return $"ConnectionStrings__{connectionName}";
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
