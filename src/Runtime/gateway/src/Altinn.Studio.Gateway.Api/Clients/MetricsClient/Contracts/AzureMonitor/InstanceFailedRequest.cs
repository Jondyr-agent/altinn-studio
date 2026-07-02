namespace Altinn.Studio.Gateway.Api.Clients.MetricsClient.Contracts.AzureMonitor;

internal sealed class InstanceFailedRequest
{
    public required string Name { get; set; }
    public required string InstanceId { get; set; }
    public required double Count { get; set; }
}
