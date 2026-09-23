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
  | 'AgentUpdateFailed';

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

export interface UserInfo {
  authenticated: boolean;
  mode: 'None' | 'EntraId' | 'Development';
  name: string | null;
  email: string | null;
  authorized: boolean;
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
}

export interface UpdateAgentSettingsRequest {
  channel: AgentChannel;
  pinnedAgentVersion: string | null;
  pinnedResticVersion: string | null;
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

export interface PostgresSource {
  type: 'postgres';
  sourceId: string;
  host: string;
  port: number;
  username: string;
  passwordSecret: string;
  excludeDatabases: string[];
  includeGlobals: boolean;
  binDirectory: string | null;
}

export type BackupSource = DirectorySource | PostgresSource;

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
}

export interface Job {
  id: string;
  agentId: string;
  policyId: string | null;
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
  agentId: string;
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
  policyId: string | null;
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
