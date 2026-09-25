import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Agent,
  Alert,
  CreateAgentReleaseRequest,
  CreateAgentRequest,
  CreateReleaseRequest,
  CreateRestoreRequest,
  CreateStorageTargetRequest,
  CronPreview,
  Dashboard,
  EnrollmentToken,
  ImportAgentReleaseRequest,
  InviteOperatorRequest,
  Job,
  JobState,
  JobType,
  LogEntry,
  NotificationPreference,
  OperatorNotification,
  OperatorRole,
  OperatorUser,
  Paged,
  PgConnection,
  PgConnectionRequest,
  Policy,
  PolicyRequest,
  Release,
  ReleaseProduct,
  Run,
  Snapshot,
  SnapshotNode,
  StorageTarget,
  SystemJobType,
  UnreadCount,
  UpdateAgentSettingsRequest,
  UpdateAgentStorageCredentialsRequest,
} from './models';

type Query = Record<string, string | number | boolean | null | undefined>;

/** Builds query params, dropping empty values (the server can't bind "" to Guid?/int?). */
export function params(query: Query): HttpParams {
  let p = new HttpParams();
  for (const [key, value] of Object.entries(query)) {
    if (value !== null && value !== undefined && value !== '') p = p.set(key, String(value));
  }
  return p;
}

/** Typed client for /api/admin. Session cookie + XSRF header are handled by HttpClient. */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);
  private readonly base = '/api/admin';

  dashboard(): Observable<Dashboard> {
    return this.http.get<Dashboard>(`${this.base}/dashboard`);
  }

  cronPreview(cron: string, timeZone: string, count = 5): Observable<CronPreview> {
    return this.http.get<CronPreview>(`${this.base}/cron/preview`, {
      params: params({ cron, timeZone, count }),
    });
  }

  storageTargets(): Observable<StorageTarget[]> {
    return this.http.get<StorageTarget[]>(`${this.base}/storage-targets`);
  }

  createStorageTarget(
    request: CreateStorageTargetRequest,
    context?: HttpContext,
  ): Observable<StorageTarget> {
    return this.http.post<StorageTarget>(`${this.base}/storage-targets`, request, { context });
  }

  agents(): Observable<Agent[]> {
    return this.http.get<Agent[]>(`${this.base}/agents`);
  }

  agent(id: string): Observable<Agent> {
    return this.http.get<Agent>(`${this.base}/agents/${id}`);
  }

  createAgent(request: CreateAgentRequest, context?: HttpContext): Observable<Agent> {
    return this.http.post<Agent>(`${this.base}/agents`, request, { context });
  }

  createEnrollmentToken(agentId: string): Observable<EnrollmentToken> {
    return this.http.post<EnrollmentToken>(
      `${this.base}/agents/${agentId}/enrollment-tokens`,
      null,
    );
  }

  disableAgent(agentId: string): Observable<void> {
    return this.http.post<void>(`${this.base}/agents/${agentId}/disable`, null);
  }

  enableAgent(agentId: string): Observable<void> {
    return this.http.post<void>(`${this.base}/agents/${agentId}/enable`, null);
  }

  deleteAgent(agentId: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/agents/${agentId}`);
  }

  /** Runs every enabled policy of the agent; already-pending ones are silently skipped. */
  runAllPolicies(agentId: string): Observable<Job[]> {
    return this.http.post<Job[]>(`${this.base}/agents/${agentId}/policies/run`, null);
  }

  snapshots(agentId: string, refresh = false, context?: HttpContext): Observable<Snapshot[]> {
    return this.http.get<Snapshot[]>(`${this.base}/agents/${agentId}/snapshots`, {
      params: params({ refresh: refresh || null }),
      context,
    });
  }

  snapshotTree(
    agentId: string,
    snapshotId: string,
    path: string,
    context?: HttpContext,
  ): Observable<SnapshotNode[]> {
    return this.http.get<SnapshotNode[]>(
      `${this.base}/agents/${agentId}/snapshots/${snapshotId}/tree`,
      { params: params({ path }), context },
    );
  }

  createRestore(agentId: string, request: CreateRestoreRequest): Observable<Job> {
    return this.http.post<Job>(`${this.base}/agents/${agentId}/restores`, request);
  }

  runSystemJob(agentId: string, type: SystemJobType): Observable<Job> {
    return this.http.post<Job>(`${this.base}/agents/${agentId}/jobs`, { type });
  }

  policies(): Observable<Policy[]> {
    return this.http.get<Policy[]>(`${this.base}/policies`);
  }

  agentPolicies(agentId: string): Observable<Policy[]> {
    return this.http.get<Policy[]>(`${this.base}/agents/${agentId}/policies`);
  }

  policy(id: string): Observable<Policy> {
    return this.http.get<Policy>(`${this.base}/policies/${id}`);
  }

  createPolicy(agentId: string, request: PolicyRequest, context?: HttpContext): Observable<Policy> {
    return this.http.post<Policy>(`${this.base}/agents/${agentId}/policies`, request, { context });
  }

  updatePolicy(id: string, request: PolicyRequest, context?: HttpContext): Observable<Policy> {
    return this.http.put<Policy>(`${this.base}/policies/${id}`, request, { context });
  }

  deletePolicy(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/policies/${id}`);
  }

  runPolicy(id: string): Observable<Job> {
    return this.http.post<Job>(`${this.base}/policies/${id}/run`, null);
  }

  jobs(
    query: {
      agentId?: string;
      policyId?: string;
      state?: JobState;
      type?: JobType;
      before?: string;
      limit?: number;
    } = {},
  ): Observable<Paged<Job>> {
    return this.http.get<Paged<Job>>(`${this.base}/jobs`, { params: params(query) });
  }

  job(id: string): Observable<Job> {
    return this.http.get<Job>(`${this.base}/jobs/${id}`);
  }

  cancelJob(id: string): Observable<Job> {
    return this.http.post<Job>(`${this.base}/jobs/${id}/cancel`, null);
  }

  runs(
    query: { agentId?: string; policyId?: string; before?: string; limit?: number } = {},
  ): Observable<Paged<Run>> {
    return this.http.get<Paged<Run>>(`${this.base}/runs`, { params: params(query) });
  }

  logs(
    query: { agentId?: string; jobId?: string; level?: string; before?: string; limit?: number } = {},
  ): Observable<Paged<LogEntry>> {
    return this.http.get<Paged<LogEntry>>(`${this.base}/logs`, { params: params(query) });
  }

  alerts(query: { open?: boolean; agentId?: string; limit?: number } = {}): Observable<Alert[]> {
    return this.http.get<Alert[]>(`${this.base}/alerts`, { params: params(query) });
  }

  updateAgentSettings(
    agentId: string,
    request: UpdateAgentSettingsRequest,
    context?: HttpContext,
  ): Observable<void> {
    return this.http.put<void>(`${this.base}/agents/${agentId}/update-settings`, request, {
      context,
    });
  }

  updateAgentStorageCredentials(
    agentId: string,
    request: UpdateAgentStorageCredentialsRequest,
    context?: HttpContext,
  ): Observable<void> {
    return this.http.put<void>(`${this.base}/agents/${agentId}/storage-credentials`, request, {
      context,
    });
  }

  releases(product: ReleaseProduct): Observable<Release[]> {
    return this.http.get<Release[]>(`${this.base}/releases`, { params: params({ product }) });
  }

  createAgentRelease(
    request: CreateAgentReleaseRequest,
    context?: HttpContext,
  ): Observable<Release> {
    return this.http.post<Release>(`${this.base}/releases/agent`, request, { context });
  }

  createResticRelease(request: CreateReleaseRequest, context?: HttpContext): Observable<Release> {
    return this.http.post<Release>(`${this.base}/releases/restic`, request, { context });
  }

  importAgentRelease(
    request: ImportAgentReleaseRequest,
    context?: HttpContext,
  ): Observable<Release[]> {
    return this.http.post<Release[]>(`${this.base}/releases/agent/import`, request, { context });
  }

  makeReleaseCurrent(id: string): Observable<void> {
    return this.http.post<void>(`${this.base}/releases/${id}/make-current`, null);
  }

  connections(agentId: string): Observable<PgConnection[]> {
    return this.http.get<PgConnection[]>(`${this.base}/agents/${agentId}/connections`);
  }

  createConnection(
    agentId: string,
    request: PgConnectionRequest,
    context?: HttpContext,
  ): Observable<PgConnection> {
    return this.http.post<PgConnection>(`${this.base}/agents/${agentId}/connections`, request, {
      context,
    });
  }

  updateConnection(
    id: string,
    request: PgConnectionRequest,
    context?: HttpContext,
  ): Observable<PgConnection> {
    return this.http.put<PgConnection>(`${this.base}/connections/${id}`, request, { context });
  }

  deleteConnection(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/connections/${id}`);
  }

  users(): Observable<OperatorUser[]> {
    return this.http.get<OperatorUser[]>(`${this.base}/users`);
  }

  inviteUser(request: InviteOperatorRequest, context?: HttpContext): Observable<OperatorUser> {
    return this.http.post<OperatorUser>(`${this.base}/users`, request, { context });
  }

  setUserRole(id: string, role: OperatorRole): Observable<OperatorUser> {
    return this.http.put<OperatorUser>(`${this.base}/users/${id}`, { role });
  }

  setUserDisabled(id: string, disabled: boolean): Observable<OperatorUser> {
    return this.http.post<OperatorUser>(
      `${this.base}/users/${id}/${disabled ? 'disable' : 'enable'}`,
      null,
    );
  }

  deleteUser(id: string): Observable<void> {
    return this.http.delete<void>(`${this.base}/users/${id}`);
  }

  userNotificationPreferences(userId: string): Observable<NotificationPreference[]> {
    return this.http.get<NotificationPreference[]>(
      `${this.base}/users/${userId}/notification-preferences`,
    );
  }

  setUserNotificationPreferences(
    userId: string,
    request: NotificationPreference[],
  ): Observable<NotificationPreference[]> {
    return this.http.put<NotificationPreference[]>(
      `${this.base}/users/${userId}/notification-preferences`,
      request,
    );
  }

  /** The signed-in operator's own feed and preferences: /api/me, not /api/admin (no user id in the URL). */
  myNotifications(
    query: { unreadOnly?: boolean; before?: string; limit?: number } = {},
  ): Observable<OperatorNotification[]> {
    return this.http.get<OperatorNotification[]>('/api/me/notifications', {
      params: params(query),
    });
  }

  myUnreadNotificationCount(): Observable<UnreadCount> {
    return this.http.get<UnreadCount>('/api/me/notifications/unread-count');
  }

  markNotificationRead(id: string): Observable<void> {
    return this.http.post<void>(`/api/me/notifications/${id}/read`, null);
  }

  markAllNotificationsRead(): Observable<void> {
    return this.http.post<void>('/api/me/notifications/read-all', null);
  }

  myNotificationPreferences(): Observable<NotificationPreference[]> {
    return this.http.get<NotificationPreference[]>('/api/me/notification-preferences');
  }

  setMyNotificationPreferences(
    request: NotificationPreference[],
  ): Observable<NotificationPreference[]> {
    return this.http.put<NotificationPreference[]>('/api/me/notification-preferences', request);
  }
}
