using System.Globalization;
using System.Text.Json;
using Altinn.Studio.Gateway.Api.Clients.MetricsClient.Contracts.AzureMonitor;
using Altinn.Studio.Gateway.Api.Settings;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Monitor.Query.Logs;
using Azure.Monitor.Query.Logs.Models;
using Azure.ResourceManager.OperationalInsights;
using Microsoft.Extensions.Options;

namespace Altinn.Studio.Gateway.Api.Clients.MetricsClient;

internal sealed class AzureMonitorClient(
    IOptionsMonitor<GatewayContext> _gatewayContext,
    LogsQueryClient _logsQueryClient,
    ILogger<AzureMonitorClient> _logger
) : IMetricsClient
{
    private const int MaxRange = 10080;
    private const int MaxActivityWindowDays = 30;
    private const int MaxInstanceRows = 100;

    // Matches the instance guid in request URLs like '.../instances/{instanceOwnerPartyId}/{instanceGuid}/...'.
    // The party id is consumed by '\d+' and the guid is the captured group, matching the admin UI's instance id.
    private const string InstanceIdUrlPattern = @"/instances/\d+/([^/]+)";

    private sealed record OperationCategory(string[] OperationNames, bool IsInstanceScoped);

    private static readonly IReadOnlyDictionary<string, OperationCategory> _operationNames = new Dictionary<
        string,
        OperationCategory
    >
    {
        {
            "failed_process_next_requests",
            new OperationCategory(
                [
                    "PUT Process/NextElement [app/instanceGuid/instanceOwnerPartyId/org]",
                    "PUT {org}/{app}/instances/{instanceOwnerPartyId:int}/{instanceGuid:guid}/process/next",
                ],
                IsInstanceScoped: true
            )
        },
        {
            "failed_instance_creation_requests",
            new OperationCategory(
                [
                    "POST Instances/Post [app/org]",
                    "POST Instances/PostSimplified [app/org]",
                    "POST {org}/{app}/instances",
                    "POST {org}/{app}/instances/create",
                ],
                IsInstanceScoped: false
            )
        },
    };

    internal static IReadOnlyCollection<string> OperationNameKeys { get; } = _operationNames.Keys.ToArray();

    internal static int GetBucketSize(int range)
    {
        var maxPoints = 12;
        return (int)Math.Max(1, Math.Ceiling((double)range / maxPoints));
    }

    private static string GetInterval(int range)
    {
        var bucketSize = GetBucketSize(range);
        return $"{bucketSize}m";
    }

    private ResourceIdentifier GetApplicationLogAnalyticsWorkspaceId()
    {
        var gatewayContext = _gatewayContext.CurrentValue;
        return OperationalInsightsWorkspaceResource.CreateResourceIdentifier(
            gatewayContext.AzureSubscriptionId,
            $"monitor-{gatewayContext.ServiceOwner}-{gatewayContext.Environment}-rg",
            $"application-{gatewayContext.ServiceOwner}-{gatewayContext.Environment}-law"
        );
    }

    public async Task<IEnumerable<FailedRequest>> GetFailedRequests(int range, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(range, MaxRange);

        var logAnalyticsWorkspaceId = GetApplicationLogAnalyticsWorkspaceId();

        var query =
            $@"
                AppRequests
                | where Success == false
                | where ClientType != 'Browser'
                | where toint(ResultCode) >= 500
                | where OperationName in ('{string.Join("','", _operationNames.Values.SelectMany(value => value.OperationNames))}')
                | summarize Count = count() by AppRoleName, OperationName;";

        Response<LogsQueryResult> response = await _logsQueryClient.QueryResourceAsync(
            logAnalyticsWorkspaceId,
            query,
            new LogsQueryTimeRange(TimeSpan.FromMinutes(range)),
            cancellationToken: cancellationToken
        );

        return response
            .Value.Table.Rows.Select(row =>
            {
                return new
                {
                    AppName = row.GetString("AppRoleName"),
                    Name = _operationNames
                        .First(n => n.Value.OperationNames.Contains(row.GetString("OperationName")))
                        .Key,
                    Count = row.GetDouble("Count") ?? 0,
                };
            })
            .GroupBy(row => new { row.AppName, row.Name })
            .Select(row =>
            {
                return new FailedRequest
                {
                    Name = row.Key.Name,
                    AppName = row.Key.AppName,
                    Count = row.Sum(value => value.Count),
                };
            });
    }

    public async Task<ActiveAppsResult> GetActiveApps(int windowDays, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowDays);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(windowDays, MaxActivityWindowDays);

        try
        {
            var logAnalyticsWorkspaceId = GetApplicationLogAnalyticsWorkspaceId();
            var query =
                $@"
                AppRequests
                | where TimeGenerated > ago({windowDays}d)
                | where Name != 'GET /health'
                | summarize Count = count() by AppRoleName";

            Response<LogsQueryResult> response = await _logsQueryClient.QueryResourceAsync(
                logAnalyticsWorkspaceId,
                query,
                new LogsQueryTimeRange(TimeSpan.FromDays(windowDays)),
                cancellationToken: cancellationToken
            );

            var activeAppRows = response
                .Value.Table.Rows.Select(row => new
                {
                    AppName = row.GetString("AppRoleName") ?? string.Empty,
                    Count = row.GetDouble("Count") ?? 0,
                })
                .Where(static row => row.AppName.Length > 0)
                .GroupBy(static row => row.AppName, StringComparer.Ordinal)
                .Select(group => new { AppName = group.Key, Count = group.Sum(row => row.Count) })
                .ToArray();

            var activeAppRequestCounts = activeAppRows.ToDictionary(
                static row => row.AppName,
                static row => row.Count,
                StringComparer.Ordinal
            );

            return new ActiveAppsResult { Status = ActivityStatus.Ok, ActiveAppRequestCounts = activeAppRequestCounts };
        }
        catch (CredentialUnavailableException ex)
        {
            _logger.LogWarning(ex, "Azure Monitor credentials are unavailable while getting active apps.");
            return new ActiveAppsResult
            {
                Status = ActivityStatus.Unavailable,
                ActiveAppRequestCounts = new Dictionary<string, double>(),
            };
        }
        catch (AuthenticationFailedException ex)
        {
            _logger.LogWarning(ex, "Azure Monitor authentication failed while getting active apps.");
            return new ActiveAppsResult
            {
                Status = ActivityStatus.Unavailable,
                ActiveAppRequestCounts = new Dictionary<string, double>(),
            };
        }
        catch (RequestFailedException ex)
        {
            _logger.LogWarning(ex, "Azure Monitor request failed while getting active apps.");
            return new ActiveAppsResult
            {
                Status = ActivityStatus.Unavailable,
                ActiveAppRequestCounts = new Dictionary<string, double>(),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while getting active apps from Azure Monitor.");
            return new ActiveAppsResult
            {
                Status = ActivityStatus.Error,
                ActiveAppRequestCounts = new Dictionary<string, double>(),
            };
        }
    }

    public async Task<IEnumerable<AppFailedRequest>> GetAppFailedRequests(
        string app,
        int range,
        CancellationToken cancellationToken
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(range, MaxRange);

        var logAnalyticsWorkspaceId = GetApplicationLogAnalyticsWorkspaceId();

        var interval = GetInterval(range);

        var query =
            $@"
                AppRequests
                | where Success == false
                | where ClientType != 'Browser'
                | where toint(ResultCode) >= 500
                | where AppRoleName == '{app.Replace("'", "''")}'
                | where OperationName in ('{string.Join("','", _operationNames.Values.SelectMany(value => value.OperationNames))}')
                | summarize Count = count() by OperationName, DateTimeOffset = bin(TimeGenerated, {interval})
                | order by DateTimeOffset desc";

        Response<LogsQueryResult> response = await _logsQueryClient.QueryResourceAsync(
            logAnalyticsWorkspaceId,
            query,
            new LogsQueryTimeRange(TimeSpan.FromMinutes(range)),
            cancellationToken: cancellationToken
        );

        var metrics = response
            .Value.Table.Rows.GroupBy(row =>
                _operationNames.First(n => n.Value.OperationNames.Contains(row.GetString("OperationName"))).Key
            )
            .Select(group => new AppFailedRequest
            {
                Name = group.Key,
                Timestamps = group.Select(row =>
                    row.GetDateTimeOffset("DateTimeOffset")?.ToUnixTimeMilliseconds() ?? 0
                ),
                Counts = group.Select(row => row.GetDouble("Count") ?? 0),
            });

        return _operationNames.Select(name =>
            metrics.FirstOrDefault(metric => metric.Name == name.Key)
            ?? new AppFailedRequest
            {
                Name = name.Key,
                Timestamps = [],
                Counts = [],
            }
        );
    }

    public async Task<IEnumerable<InstanceFailedRequest>> GetAppInstanceFailedRequests(
        string app,
        int range,
        CancellationToken cancellationToken
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(range, MaxRange);

        var logAnalyticsWorkspaceId = GetApplicationLogAnalyticsWorkspaceId();

        var instanceScopedOperationNames = _operationNames
            .Where(category => category.Value.IsInstanceScoped)
            .SelectMany(category => category.Value.OperationNames);

        var query =
            $@"
                AppRequests
                | where Success == false
                | where ClientType != 'Browser'
                | where toint(ResultCode) >= 500
                | where AppRoleName == '{app.Replace("'", "''")}'
                | where OperationName in ('{string.Join("','", instanceScopedOperationNames)}')
                | extend InstanceId = extract(@'{InstanceIdUrlPattern}', 1, Url)
                | where isnotempty(InstanceId)
                | summarize Count = count() by OperationName, InstanceId
                | top {MaxInstanceRows} by Count";

        Response<LogsQueryResult> response = await _logsQueryClient.QueryResourceAsync(
            logAnalyticsWorkspaceId,
            query,
            new LogsQueryTimeRange(TimeSpan.FromMinutes(range)),
            cancellationToken: cancellationToken
        );

        return response.Value.Table.Rows.Select(row => new InstanceFailedRequest
        {
            Name = _operationNames.First(n => n.Value.OperationNames.Contains(row.GetString("OperationName"))).Key,
            InstanceId = row.GetString("InstanceId") ?? string.Empty,
            Count = row.GetDouble("Count") ?? 0,
        });
    }

    public async Task<IEnumerable<AppMetric>> GetAppMetrics(string app, int range, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(range);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(range, MaxRange);

        List<string> names = ["altinn_app_lib_processes_started", "altinn_app_lib_processes_ended"];

        var logAnalyticsWorkspaceId = GetApplicationLogAnalyticsWorkspaceId();

        var interval = GetInterval(range);

        var query =
            $@"
                AppMetrics
                | where AppRoleName == '{app.Replace("'", "''")}'
                | where Name in ('{string.Join("','", names)}')
                | summarize Count = sum(Sum) by Name, DateTimeOffset = bin(TimeGenerated, {interval})
                | order by DateTimeOffset desc";

        Response<LogsQueryResult> response = await _logsQueryClient.QueryResourceAsync(
            logAnalyticsWorkspaceId,
            query,
            new LogsQueryTimeRange(TimeSpan.FromMinutes(range)),
            cancellationToken: cancellationToken
        );

        var metrics = response
            .Value.Table.Rows.GroupBy(row => row.GetString("Name"))
            .Select(group => new AppMetric
            {
                Name = group.Key,
                Timestamps = group.Select(row =>
                    row.GetDateTimeOffset("DateTimeOffset")?.ToUnixTimeMilliseconds() ?? 0
                ),
                Counts = group.Select(row => row.GetDouble("Count") ?? 0),
            });

        return names.Select(name =>
            metrics.FirstOrDefault(metric => metric.Name == name)
            ?? new AppMetric
            {
                Name = name,
                Timestamps = [],
                Counts = [],
            }
        );
    }

    public Uri GetLogsUrl(
        string subscriptionId,
        string org,
        string env,
        IReadOnlyCollection<string> apps,
        string metricName,
        DateTimeOffset from,
        DateTimeOffset to,
        string? searchPhrase = null
    )
    {
        if (!_operationNames.TryGetValue(metricName, out var operationCategory))
            throw new ArgumentException(
                $"Unknown metricName '{metricName}'. Valid values: {string.Join(", ", OperationNameKeys)}",
                nameof(metricName)
            );

        var operationNames = operationCategory.OperationNames;

        string jsonPath = Path.Combine(AppContext.BaseDirectory, "Clients", "MetricsClient", "logsQueryTemplate.json");
        var fromUtc = from.ToUniversalTime();
        var toUtc = to.ToUniversalTime();
        var durationMs = (long)(toUtc - fromUtc).TotalMilliseconds;
        string jsonTemplate = File.ReadAllText(jsonPath);
        string json = jsonTemplate
            .Replace("{from}", fromUtc.ToString("O", CultureInfo.InvariantCulture))
            .Replace("{to}", toUtc.ToString("O", CultureInfo.InvariantCulture))
            .Replace("{durationMs}", durationMs.ToString(CultureInfo.InvariantCulture))
            .Replace("{app_Names}", string.Join(", ", apps.Select(n => $"'{n.Replace("'", "''")}'")))
            .Replace("{operation_Names}", string.Join(", ", operationNames.Select(n => $"'{n}'")))
            .Replace("\"{appNames}\"", string.Join(", ", apps.Select(name => $"\"{JsonEncodedText.Encode(name)}\"")))
            .Replace("\"{operationNames}\"", string.Join(",", operationNames.Select(n => $"\"{n}\"")));
        var jsonNode = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (
            !string.IsNullOrEmpty(searchPhrase)
            && jsonNode?["originalParams"]?["searchPhrase"] is System.Text.Json.Nodes.JsonObject searchPhraseNode
        )
        {
            // Pre-fill the Application Insights search box so the logs view is scoped to a single instance.
            searchPhraseNode["originalPhrase"] = searchPhrase;
        }
        var minifiedJson = jsonNode?.ToJsonString() ?? string.Empty;

        string encodedLogsQuery = Uri.EscapeDataString(minifiedJson);

        string encodedApplicationInsightsId = Uri.EscapeDataString(
            $"/subscriptions/{subscriptionId}/resourceGroups/monitor-{org}-{env}-rg/providers/Microsoft.Insights/components/{org}-{env}-ai"
        );
        var url =
            $"https://portal.azure.com/#blade/AppInsightsExtension/BladeRedirect/BladeName/searchV1/ResourceId/{encodedApplicationInsightsId}/BladeInputs/{encodedLogsQuery}";

        return new Uri(url);
    }
}
