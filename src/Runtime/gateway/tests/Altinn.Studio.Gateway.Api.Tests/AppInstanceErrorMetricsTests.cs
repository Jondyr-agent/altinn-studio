using Altinn.Studio.Gateway.Api.Application;
using Altinn.Studio.Gateway.Api.Clients.MetricsClient;
using Altinn.Studio.Gateway.Api.Clients.MetricsClient.Contracts.AzureMonitor;
using Altinn.Studio.Gateway.Api.Settings;
using Altinn.Studio.Gateway.Contracts.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AzureMonitorAppMetric = Altinn.Studio.Gateway.Api.Clients.MetricsClient.Contracts.AzureMonitor.AppMetric;

namespace Altinn.Studio.Gateway.Api.Tests;

public sealed class AppInstanceErrorMetricsTests
{
    [Fact]
    public async Task GetAppInstanceErrorMetrics_MapsFailedRequestsAndScopesLogsUrlToInstance()
    {
        var instanceA = "f7f8a9c0-1111-2222-3333-444455556666";
        var instanceB = "abcdef01-2222-3333-4444-555566667777";
        var metricsClient = new FakeMetricsClient([
            new InstanceFailedRequest
            {
                Name = "failed_process_next_requests",
                InstanceId = instanceA,
                Count = 5,
            },
            new InstanceFailedRequest
            {
                Name = "failed_process_next_requests",
                InstanceId = instanceB,
                Count = 2,
            },
        ]);
        using var serviceProvider = BuildServiceProvider(metricsClient);

        var result = await HandleMetrics.GetAppInstanceErrorMetrics(
            BuildGatewayContext(),
            serviceProvider,
            BuildMetricsClientSettings(),
            app: "my-app",
            range: 60,
            TestContext.Current.CancellationToken
        );

        var metrics = AssertOkValue(result).ToArray();

        Assert.Equal(2, metrics.Length);

        var first = metrics[0];
        Assert.Equal("failed_process_next_requests", first.Name);
        Assert.Equal(instanceA, first.InstanceId);
        Assert.Equal(5, first.Count);
        // The per-instance logs link must be scoped to the instance (passed as the search phrase).
        Assert.Contains(instanceA, first.LogsUrl.ToString(), StringComparison.Ordinal);

        Assert.Equal(instanceB, metrics[1].InstanceId);
        Assert.Contains(instanceB, metrics[1].LogsUrl.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAppInstanceErrorMetrics_WhenNoInstanceErrors_ReturnsEmpty()
    {
        var metricsClient = new FakeMetricsClient([]);
        using var serviceProvider = BuildServiceProvider(metricsClient);

        var result = await HandleMetrics.GetAppInstanceErrorMetrics(
            BuildGatewayContext(),
            serviceProvider,
            BuildMetricsClientSettings(),
            app: "my-app",
            range: 60,
            TestContext.Current.CancellationToken
        );

        Assert.Empty(AssertOkValue(result));
    }

    private static IEnumerable<InstanceErrorMetric> AssertOkValue(IResult result)
    {
        var valueResult = Assert.IsAssignableFrom<IValueHttpResult>(result);
        return Assert.IsAssignableFrom<IEnumerable<InstanceErrorMetric>>(valueResult.Value);
    }

    private static IOptionsMonitor<GatewayContext> BuildGatewayContext() =>
        new StaticOptionsMonitor<GatewayContext>(
            new GatewayContext
            {
                AzureSubscriptionId = "sub",
                ServiceOwner = "ttd",
                Environment = "at22",
            }
        );

    private static IOptionsMonitor<MetricsClientSettings> BuildMetricsClientSettings() =>
        new StaticOptionsMonitor<MetricsClientSettings>(
            new MetricsClientSettings { Provider = MetricsClientSettings.MetricsClientProvider.AzureMonitor }
        );

    private static ServiceProvider BuildServiceProvider(IMetricsClient metricsClient)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton<IMetricsClient>(
            MetricsClientSettings.MetricsClientProvider.AzureMonitor,
            metricsClient
        );
        return services.BuildServiceProvider();
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;

        public T Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FakeMetricsClient(IEnumerable<InstanceFailedRequest> instanceFailedRequests) : IMetricsClient
    {
        public Task<IEnumerable<FailedRequest>> GetFailedRequests(int range, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ActiveAppsResult> GetActiveApps(int windowDays, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IEnumerable<AppFailedRequest>> GetAppFailedRequests(
            string app,
            int range,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Task<IEnumerable<InstanceFailedRequest>> GetAppInstanceFailedRequests(
            string app,
            int range,
            CancellationToken cancellationToken
        ) => Task.FromResult(instanceFailedRequests);

        public Task<IEnumerable<AzureMonitorAppMetric>> GetAppMetrics(
            string app,
            int range,
            CancellationToken cancellationToken
        ) => throw new NotSupportedException();

        public Uri GetLogsUrl(
            string subscriptionId,
            string org,
            string env,
            IReadOnlyCollection<string> apps,
            string metricName,
            DateTimeOffset from,
            DateTimeOffset to,
            string? searchPhrase = null
        ) => new($"https://logs.example/{org}/{env}/{apps.First()}/{metricName}?instance={searchPhrase}");
    }
}
