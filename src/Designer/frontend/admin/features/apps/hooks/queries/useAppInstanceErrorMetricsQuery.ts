import type { QueryMeta, UseQueryResult } from '@tanstack/react-query';
import { useQuery } from '@tanstack/react-query';
import type { InstanceErrorMetric } from 'admin/features/apps/types/metrics/InstanceErrorMetric';
import { appInstanceErrorMetricsPath } from 'admin/features/apps/utils/apiPaths';
import { QueryKey } from 'app-shared/types/QueryKey';
import axios from 'axios';

export const useAppInstanceErrorMetricsQuery = (
  org: string,
  env: string,
  app: string,
  range: number,
  meta?: QueryMeta,
): UseQueryResult<InstanceErrorMetric[]> => {
  return useQuery<InstanceErrorMetric[]>({
    queryKey: [QueryKey.AppInstanceErrorMetrics, org, env, app, range],
    queryFn: async ({ signal }) =>
      (await axios.get<InstanceErrorMetric[]>(appInstanceErrorMetricsPath(org, env, app, range), { signal }))
        .data,
    meta,
  });
};
