export type InstanceErrorMetric = {
  name: string;
  instanceId: string;
  count: number;
  logsUrl: string;
};

// Error categories that target a specific instance and can therefore be broken down per instance.
const instanceScopedErrorMetrics = ['failed_process_next_requests'];

export const isInstanceScopedErrorMetric = (name: string): boolean =>
  instanceScopedErrorMetrics.includes(name);
