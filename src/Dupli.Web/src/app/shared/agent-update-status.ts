import { Agent } from '../core/models';

export type AgentUpdateStatus = 'Up to date' | 'Update available' | 'Update failed' | 'No launcher';

type StatusInput = Pick<
  Agent,
  'version' | 'desiredAgentVersion' | 'launcherManaged' | 'lastUpdateOutcome' | 'lastUpdateVersion'
>;

/**
 * Derives the agent-version badge status shown in the agents list and the dashboard.
 * - Up to date: running version matches the desired one, or there is no desired version yet.
 * - Update failed: the last attempt to reach the desired version was rolled back.
 * - No launcher: the agent is not launcher-managed, so it cannot self-update even though it differs.
 * - Update available: differs, launcher-managed, no rollback recorded for the desired version.
 */
export function agentUpdateStatus(agent: StatusInput): AgentUpdateStatus {
  const { version, desiredAgentVersion, launcherManaged, lastUpdateOutcome, lastUpdateVersion } =
    agent;

  if (!desiredAgentVersion || version === desiredAgentVersion) return 'Up to date';
  if (lastUpdateOutcome === 'RolledBack' && lastUpdateVersion === desiredAgentVersion)
    return 'Update failed';
  if (!launcherManaged) return 'No launcher';
  return 'Update available';
}
