// Mirrors of the server admin DTOs (src/Dupli.Server/Api/AdminDtos.cs and Dupli.Contracts).
// JSON uses camelCase property names and string enums.

export type AgentStatus = 'Pending' | 'Active' | 'Disabled';
export type JobType =
  | 'Backup'
  | 'Restore'
  | 'RestoreTest'
  | 'RepositoryCheck'
  | 'Retention'
  | 'RestartAgent'
  | 'AgentUpdate'
  | 'ResticUpdate';
export type SystemJobType = 'RestoreTest' | 'RestartAgent' | 'Retention' | 'RepositoryCheck';
export type JobTrigger = 'Schedule' | 'Manual' | 'System';
export type JobState =
  'Pending' | 'Assigned' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled' | 'TimedOut' | 'Missed';
export type JobOutcome =
  'Succeeded' | 'SucceededWithWarnings' | 'Failed' | 'Cancelled' | 'Interrupted';
export type AlertKind =
  | 'AgentOffline'
  | 'BackupFailed'
  | 'BackupMissed'
  | 'BackupTooOld'
  | 'RepositoryCheckFailed'
  | 'RestoreTestFailed'
  | 'AgentUpdateFailed'
  | 'AgentOutdated';

export const ALERT_KINDS: readonly AlertKind[] = [
  'AgentOffline',
  'BackupFailed',
  'BackupMissed',
  'BackupTooOld',
  'RepositoryCheckFailed',
  'RestoreTestFailed',
  'AgentUpdateFailed',
  'AgentOutdated',
];

export type AgentPlatform = 'windows_amd64' | 'linux_amd64' | 'linux_arm64';
export type AgentChannel = 'dev' | 'beta' | 'stable';
export type UpdateOutcome = 'Succeeded' | 'RolledBack';
export type ReleaseProduct = 'agent' | 'restic';

export const AGENT_PLATFORMS: readonly AgentPlatform[] = [
  'windows_amd64',
  'linux_amd64',
  'linux_arm64',
];
export const AGENT_CHANNELS: readonly AgentChannel[] = ['dev', 'beta', 'stable'];

export const TERMINAL_STATES: readonly JobState[] = [
  'Succeeded',
  'Failed',
  'Cancelled',
  'TimedOut',
  'Missed',
];

/** Ordered: each role includes the permissions of the ones before it. */
export const OPERATOR_ROLES = ['Viewer', 'Operator', 'Owner'] as const;
export type OperatorRole = (typeof OPERATOR_ROLES)[number];

export interface UserInfo {
  authenticated: boolean;
  mode: 'None' | 'EntraId';
  name: string | null;
  email: string | null;
  role: OperatorRole | null;
}

export interface OperatorUser {
  id: string;
  email: string;
  role: OperatorRole;
  displayName: string | null;
  /** False while the invitation has not been used for a first sign-in. */
  bound: boolean;
  lastLoginAt: string | null;
  disabledAt: string | null;
  createdAt: string;
  createdBy: string;
  updatedAt: string;
  updatedBy: string;
}

export interface InviteOperatorRequest {
  email: string;
  role: OperatorRole;
}

export interface StorageTarget {
  id: string;
  name: string;
  endpoint: string;
  bucket: string;
  region: string | null;
}

export interface CreateStorageTargetRequest {
  name: string;
  endpoint: string;
  bucket: string;
  region: string | null;
}

export interface Agent {
  id: string;
  name: string;
  status: AgentStatus;
  online: boolean;
  hostname: string | null;
  osVersion: string | null;
  version: string | null;
  resticVersion: string | null;
  lastHeartbeatAt: string | null;
  lastBackupAt: string | null;
  freeDiskSpace: number | null;
  storageTargetId: string;
  storagePrefix: string;
  createdAt: string;
  enrolledAt: string | null;
  platform: AgentPlatform;
  channel: AgentChannel;
  pinnedAgentVersion: string | null;
  pinnedResticVersion: string | null;
  launcherManaged: boolean;
  desiredAgentVersion: string | null;
  desiredResticVersion: string | null;
  lastUpdateVersion: string | null;
  lastUpdateOutcome: UpdateOutcome | null;
  lastUpdateError: string | null;
  lastUpdateAt: string | null;
  resticUpdateError: string | null;
  s3AccessKeyId: string;
  s3CredentialsVersion: number;
  s3CredentialsAppliedVersion: number | null;
  s3CredentialsUpdatedAt: string | null;
}

export interface UpdateAgentSettingsRequest {
  channel: AgentChannel;
  pinnedAgentVersion: string | null;
  pinnedResticVersion: string | null;
}

export interface UpdateAgentStorageCredentialsRequest {
  accessKeyId: string;
  secretAccessKey: string;
  skipVerification: boolean;
}

export interface CreateAgentRequest {
  name: string;
  storageTargetId: string;
  storagePrefix: string;
  s3AccessKeyId: string;
  s3SecretAccessKey: string;
  repositoryPassword?: string | null;
}

export interface EnrollmentToken {
  token: string;
  expiresAt: string;
}

export interface DashboardCounters {
  online: number;
  offline: number;
  pending: number;
  backupFailed: number;
  backupRunning: number;
  openAlerts: number;
}

export interface DashboardAgent {
  agent: Agent;
  lastRunStatus: JobOutcome | null;
  runningJob: JobType | null;
  openAlerts: number;
}

export interface Dashboard {
  counters: DashboardCounters;
  agents: DashboardAgent[];
}

/** Keyset page: `next` is an opaque cursor for the next call's `before`, or null on the last page. */
export interface Paged<T> {
  items: T[];
  next: string | null;
}

export interface CronPreview {
  valid: boolean;
  error: string | null;
  next: string[];
}

export interface Retention {
  keepDaily: number;
  keepWeekly: number;
  keepMonthly: number;
}

export interface DirectorySource {
  type: 'directory';
  sourceId: string;
  paths: string[];
  excludes: string[];
}

/** AllExcept: every database except templates, postgres and excludeDatabases. Only: exactly includeDatabases. */
export type DatabaseSelection = 'AllExcept' | 'Only';

export interface PostgresSource {
  type: 'postgres';
  sourceId: string;
  connectionId: string;
  databaseSelection: DatabaseSelection;
  excludeDatabases: string[];
  includeDatabases: string[];
  includeGlobals: boolean;
}

export type BackupSource = DirectorySource | PostgresSource;

export interface PgConnection {
  id: string;
  agentId: string;
  name: string;
  host: string;
  port: number;
  username: string;
  passwordSecret: string;
  binDirectory: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface PgConnectionRequest {
  name: string;
  host: string;
  port: number;
  username: string;
  passwordSecret: string;
  binDirectory: string | null;
}

export interface PolicyRequest {
  name: string;
  cron: string;
  timeZone: string;
  enabled: boolean;
  retention: Retention;
  sources: BackupSource[];
}

export interface Policy extends PolicyRequest {
  id: string;
  agentId: string;
  nextRunAt: string | null;
  lastScheduledFor: string | null;
}

export interface JobItemResult {
  sourceId: string;
  item: string;
  outcome: JobOutcome;
  snapshotId: string | null;
  bytesProcessed: number;
  bytesAdded: number;
  warnings: string[];
  error: string | null;
  /** Restore: target directory or database. */
  location?: string | null;
}

export interface Job {
  id: string;
  agentId: string;
  agentName: string;
  policyId: string | null;
  policyName: string | null;
  type: JobType;
  trigger: JobTrigger;
  state: JobState;
  createdAt: string;
  scheduledAt: string;
  expiresAt: string;
  startedAt: string | null;
  completedAt: string | null;
  cancelRequested: boolean;
  error: string | null;
  items: JobItemResult[];
}

export interface Run {
  id: string;
  jobId: string;
  policyId: string;
  policyName: string | null;
  agentId: string;
  agentName: string;
  startedAt: string;
  completedAt: string;
  status: JobOutcome;
  bytesProcessed: number;
  bytesAdded: number;
  items: JobItemResult[];
  errorMessage: string | null;
}

export interface LogEntry {
  id: number;
  agentId: string;
  agentName: string;
  jobId: string | null;
  timestamp: string;
  level: string;
  message: string;
  exception: string | null;
}

export interface Alert {
  id: string;
  kind: AlertKind;
  subjectKey: string;
  agentId: string | null;
  agentName: string | null;
  policyId: string | null;
  policyName: string | null;
  message: string;
  openedAt: string;
  resolvedAt: string | null;
}

export interface Release {
  id: string;
  product: ReleaseProduct;
  version: string;
  platform: AgentPlatform;
  channel: AgentChannel | null;
  sourceUrl: string;
  sha256: string;
  isCurrent: boolean;
  createdAt: string;
}

export interface CreateReleaseRequest {
  version: string;
  platform: AgentPlatform;
  sourceUrl: string;
  sha256: string;
  makeCurrent: boolean;
}

export interface CreateAgentReleaseRequest {
  version: string;
  platform: AgentPlatform;
  channel: AgentChannel;
  sourceUrl: string;
  sha256: string;
  makeCurrent: boolean;
}

export interface ImportAgentReleaseRequest {
  version: string;
  channel: AgentChannel;
  makeCurrent: boolean;
}

export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
}

/** A restic snapshot of an agent's repository, Dupli tags parsed. */
export interface Snapshot {
  id: string;
  shortId: string;
  time: string;
  host: string;
  paths: string[];
  tags: string[];
  policyId: string | null;
  policyName: string | null;
  sourceId: string | null;
  /** 'dir' | 'pg' */
  type: string | null;
  /** PostgreSQL database ('_globals' = roles/tablespaces). */
  database: string | null;
  /** Connection of the policy source that produced this snapshot, if the policy still exists. UI default only. */
  connectionId: string | null;
}

export type SnapshotNodeType = 'File' | 'Directory' | 'Symlink' | 'Other';

export interface SnapshotNode {
  name: string;
  path: string;
  type: SnapshotNodeType;
  size: number;
  modifiedAt: string | null;
}

export interface CreateRestoreRequest {
  snapshotId: string;
  includes: string[];
  targetDirectory: string | null;
  newDatabase: string | null;
  connectionId: string | null;
}

export type NotificationEvent = 'Opened' | 'Resolved';

/** One row per (alert transition, recipient): the bell and /notifications feed. */
export interface OperatorNotification {
  id: string;
  kind: AlertKind;
  event: NotificationEvent;
  subject: string;
  body: string;
  agentId: string | null;
  policyId: string | null;
  createdAt: string;
  readAt: string | null;
}

export interface UnreadCount {
  count: number;
}

/** Per-kind opt-in to e-mail and/or in-app notifications. Absent from the server response never happens:
 * GET always returns all `ALERT_KINDS`, defaults included. */
export interface NotificationPreference {
  kind: AlertKind;
  email: boolean;
  inApp: boolean;
}
