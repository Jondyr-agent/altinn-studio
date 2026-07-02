namespace Altinn.Studio.Gateway.Contracts.Metrics;

public class InstanceErrorMetric
{
    public required string Name { get; set; }
    public required string InstanceId { get; set; }
    public required double Count { get; set; }
    public required Uri LogsUrl { get; set; }
}
