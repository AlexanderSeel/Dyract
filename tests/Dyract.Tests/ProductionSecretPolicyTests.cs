using Dyract.Server.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Dyract.Tests;

public sealed class ProductionSecretPolicyTests
{
    [Fact]
    public void Development_AllowsNormalConfigurationProviders()
    {
        var configuration = CreateConfiguration(
            ("ConnectionStrings:Dyract", "Host=localhost;Database=dyract_dev"));

        var value = ProductionSecretPolicy.GetConnectionString(
            configuration,
            ProductionSecretPolicy.PostgreSqlConnectionName,
            "Development",
            _ => throw new InvalidOperationException("Development must not consult the deployment-only reader."));

        Assert.Equal("Host=localhost;Database=dyract_dev", value);
    }

    [Fact]
    public void Production_UnconfiguredOptionalInfrastructure_RemainsAbsent()
    {
        var configuration = CreateConfiguration();

        var value = ProductionSecretPolicy.GetConnectionString(
            configuration,
            ProductionSecretPolicy.RedisConnectionName,
            "Production",
            _ => null);

        Assert.Null(value);
    }

    [Theory]
    [InlineData("Dyract")]
    [InlineData("Redis")]
    public void Production_ConfiguredOutsideDeploymentSecretSource_FailsWithoutLeakingValue(string connectionName)
    {
        const string secretSentinel = "PRIVATE-CONNECTION-SECRET-SENTINEL-8f31";
        var configuration = CreateConfiguration(
            ($"ConnectionStrings:{connectionName}", $"Host=hidden;Password={secretSentinel}"));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionSecretPolicy.GetConnectionString(
                configuration,
                connectionName,
                "Production",
                _ => null));

        Assert.Contains($"ConnectionStrings__{connectionName}", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretSentinel, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=hidden", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Dyract")]
    [InlineData("Redis")]
    public void Production_UsesExactDeploymentEnvironmentValueInsteadOfOtherConfiguration(string connectionName)
    {
        const string configuredSentinel = "SHOULD-NOT-BE-USED";
        const string deploymentValue = "Endpoint=private;Password=deployment-secret";
        var configuration = CreateConfiguration(
            ($"ConnectionStrings:{connectionName}", configuredSentinel));
        string? requestedVariable = null;

        var value = ProductionSecretPolicy.GetConnectionString(
            configuration,
            connectionName,
            "Production",
            variableName =>
            {
                requestedVariable = variableName;
                return deploymentValue;
            });

        Assert.Equal($"ConnectionStrings__{connectionName}", requestedVariable);
        Assert.Equal(deploymentValue, value);
        Assert.NotEqual(configuredSentinel, value);
    }

    [Fact]
    public void Production_WhitespaceDeploymentValue_DoesNotOverrideUnsafeConfiguredValue()
    {
        var configuration = CreateConfiguration(
            ("ConnectionStrings:Dyract", "Host=not-an-approved-production-source"));

        Assert.Throws<InvalidOperationException>(() =>
            ProductionSecretPolicy.GetConnectionString(
                configuration,
                ProductionSecretPolicy.PostgreSqlConnectionName,
                "Production",
                _ => "   "));
    }

    private static IConfiguration CreateConfiguration(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value))
            .Build();
}
