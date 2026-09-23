import { Agent } from '../core/models';
import { agentUpdateStatus } from './agent-update-status';

function agent(overrides: Partial<Agent> = {}): Parameters<typeof agentUpdateStatus>[0] {
  return {
    version: '0.2.0',
    desiredAgentVersion: '0.2.0',
    launcherManaged: true,
    lastUpdateOutcome: null,
    lastUpdateVersion: null,
    ...overrides,
  };
}

describe('agentUpdateStatus', () => {
  it('is "Up to date" when the running version matches the desired one', () => {
    expect(agentUpdateStatus(agent())).toBe('Up to date');
  });

  it('is "Up to date" when there is no desired version yet', () => {
    expect(agentUpdateStatus(agent({ version: '0.1.0', desiredAgentVersion: null }))).toBe(
      'Up to date',
    );
  });

  it('is "Update available" when it differs, launcher-managed, no rollback on the desired version', () => {
    expect(agentUpdateStatus(agent({ version: '0.1.0', desiredAgentVersion: '0.2.0' }))).toBe(
      'Update available',
    );
  });

  it('is "Update failed" when the desired version was rolled back', () => {
    expect(
      agentUpdateStatus(
        agent({
          version: '0.1.0',
          desiredAgentVersion: '0.2.0',
          lastUpdateOutcome: 'RolledBack',
          lastUpdateVersion: '0.2.0',
        }),
      ),
    ).toBe('Update failed');
  });

  it('is "Update available" when a rollback happened but not for the current desired version', () => {
    expect(
      agentUpdateStatus(
        agent({
          version: '0.1.0',
          desiredAgentVersion: '0.3.0',
          lastUpdateOutcome: 'RolledBack',
          lastUpdateVersion: '0.2.0',
        }),
      ),
    ).toBe('Update available');
  });

  it('is "No launcher" when the agent cannot self-update', () => {
    expect(
      agentUpdateStatus(
        agent({ version: '0.1.0', desiredAgentVersion: '0.2.0', launcherManaged: false }),
      ),
    ).toBe('No launcher');
  });

  it('prefers "Update failed" over "No launcher" when both conditions hold', () => {
    expect(
      agentUpdateStatus(
        agent({
          version: '0.1.0',
          desiredAgentVersion: '0.2.0',
          launcherManaged: false,
          lastUpdateOutcome: 'RolledBack',
          lastUpdateVersion: '0.2.0',
        }),
      ),
    ).toBe('Update failed');
  });
});
