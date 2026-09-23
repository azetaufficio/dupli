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
  | 'RestoreTestFailed';

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

export interface ProblemDetails {
  title?: string;
  detail?: string;
  status?: number;
}
