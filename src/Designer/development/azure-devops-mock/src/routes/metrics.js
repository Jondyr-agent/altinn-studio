import { getDeployedApps } from './builds.js';
import { sampleInstanceIds } from './storage.js';

const ErrorMetricName = {
  ProcessNext: 'failed_process_next_requests',
  InstanceCreation: 'failed_instance_creation_requests',
};

const InstanceErrorSampleSize = 8;
const MaxChartPoints = 12;

function mockLogsUrl(app, instanceId) {
  const base = `https://portal.azure.com/#blade/AppInsightsExtension/mock/${encodeURIComponent(app)}`;
  return instanceId ? `${base}/instance/${instanceId}` : base;
}

function randomCount(max) {
  return Math.floor(Math.random() * max) + 1;
}

function getBucketSize(range) {
  return Math.max(1, Math.ceil(range / MaxChartPoints));
}

function buildTimeSeries(range) {
  const bucketSize = getBucketSize(range);
  const pointCount = Math.max(1, Math.min(MaxChartPoints, Math.ceil(range / bucketSize)));
  const bucketSizeMs = bucketSize * 60 * 1000;
  const now = Date.now();

  const timestamps = [];
  const counts = [];
  for (let point = pointCount - 1; point >= 0; point--) {
    timestamps.push(now - point * bucketSizeMs);
    counts.push(Math.floor(Math.random() * 6));
  }

  return { bucketSize, timestamps, counts };
}

// Org-level error counts per app. Mirrors the gateway ErrorMetric contract.
export const metricsErrorsRoute = (req, res) => {
  const { org, env } = req.params;

  const metrics = getDeployedApps(org, env).flatMap((app) => [
    {
      name: ErrorMetricName.ProcessNext,
      appName: app,
      count: randomCount(20),
      logsUrl: mockLogsUrl(app),
    },
    {
      name: ErrorMetricName.InstanceCreation,
      appName: app,
      count: randomCount(5),
      logsUrl: mockLogsUrl(app),
    },
  ]);

  res.json(metrics);
};

// App-level error time series. Mirrors the gateway AppErrorMetric contract.
export const metricsAppErrorsRoute = (req, res) => {
  const app = req.query?.['app'] ?? '';
  const range = parseInt(req.query?.['range']) || 60;

  const metrics = [ErrorMetricName.ProcessNext, ErrorMetricName.InstanceCreation].map((name) => ({
    name,
    ...buildTimeSeries(range),
    logsUrl: mockLogsUrl(app),
  }));

  res.json(metrics);
};

// App-level error counts per instance. Only the instance-scoped category produces rows.
// Instance ids are drawn from the storage mock so the deep-links resolve to a real instance.
export const metricsAppInstanceErrorsRoute = (req, res) => {
  const app = req.query?.['app'] ?? '';

  const metrics = sampleInstanceIds(InstanceErrorSampleSize).map((instanceId) => ({
    name: ErrorMetricName.ProcessNext,
    instanceId,
    count: randomCount(15),
    logsUrl: mockLogsUrl(app, instanceId),
  }));

  res.json(metrics);
};
