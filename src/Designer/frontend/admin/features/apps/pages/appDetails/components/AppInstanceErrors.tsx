import { useTranslation } from 'react-i18next';
import { Link } from 'react-router-dom';
import {
  StudioDetails,
  StudioTable,
  StudioSpinner,
  StudioError,
  StudioParagraph,
} from '@studio/components';
import { useAppInstanceErrorMetricsQuery } from 'admin/features/apps/hooks/queries/useAppInstanceErrorMetricsQuery';
import { useRequiredRoutePathsParams } from 'admin/hooks/useRequiredRoutePathsParams';
import classes from './AppInstanceErrors.module.css';

type AppInstanceErrorsProps = {
  name: string;
  range: number;
};

export const AppInstanceErrors = ({ name, range }: AppInstanceErrorsProps) => {
  const { t } = useTranslation();
  const {
    owner: org,
    environment,
    app,
  } = useRequiredRoutePathsParams(['owner', 'environment', 'app']);

  const { data, isPending, isError } = useAppInstanceErrorMetricsQuery(org, environment, app, range, {
    hideDefaultError: true,
  });

  const instanceErrors = (data ?? []).filter((metric) => metric.name === name);

  if (!isPending && !isError && instanceErrors.length === 0) {
    return null;
  }

  return (
    <StudioDetails className={classes.details}>
      <StudioDetails.Summary>
        {t('admin.metrics.app.errors.instances.show', { count: instanceErrors.length })}
      </StudioDetails.Summary>
      <StudioDetails.Content>
        {isPending && (
          <StudioSpinner aria-label={t('admin.metrics.app.errors.instances.loading')} />
        )}
        {isError && <StudioError>{t('admin.metrics.app.errors.instances.error')}</StudioError>}
        {!isPending && !isError && (
          <StudioTable className={classes.table}>
            <StudioTable.Head>
              <StudioTable.Row>
                <StudioTable.Cell>
                  {t('admin.metrics.app.errors.instances.instance')}
                </StudioTable.Cell>
                <StudioTable.Cell>
                  {t('admin.metrics.app.errors.instances.count')}
                </StudioTable.Cell>
              </StudioTable.Row>
            </StudioTable.Head>
            <StudioTable.Body>
              {instanceErrors.map((metric) => (
                <StudioTable.Row key={metric.instanceId}>
                  <StudioTable.Cell>
                    <Link to={`instances/${metric.instanceId}`}>{metric.instanceId}</Link>
                  </StudioTable.Cell>
                  <StudioTable.Cell>{metric.count}</StudioTable.Cell>
                </StudioTable.Row>
              ))}
            </StudioTable.Body>
          </StudioTable>
        )}
        <StudioParagraph data-size='xs' className={classes.hint}>
          {t('admin.metrics.app.errors.instances.hint')}
        </StudioParagraph>
      </StudioDetails.Content>
    </StudioDetails>
  );
};
