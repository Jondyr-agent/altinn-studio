using System;

namespace Altinn.Studio.Designer.Models.Metrics;

public class InstanceErrorMetric
{
    public required string Name { get; set; }
    public required string InstanceId { get; set; }
    public required double Count { get; set; }
    public required Uri LogsUrl { get; set; }
}
