import { alertKindLabel } from './notification-preferences';

describe('alertKindLabel', () => {
  it('splits PascalCase words and sentence-cases the result', () => {
    expect(alertKindLabel('AgentOffline')).toBe('Agent offline');
    expect(alertKindLabel('BackupTooOld')).toBe('Backup too old');
    expect(alertKindLabel('AgentUpdateFailed')).toBe('Agent update failed');
  });

  it('leaves a single word capitalized', () => {
    expect(alertKindLabel('AgentOutdated')).toBe('Agent outdated');
  });
});
