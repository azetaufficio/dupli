import { HttpClient, HttpContext, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  Agent,
  Alert,
  CreateAgentReleaseRequest,
  CreateAgentRequest,
  CreateReleaseRequest,
  CreateStorageTargetRequest,
  CronPreview,
  Dashboard,
  EnrollmentToken,
  ImportAgentReleaseRequest,
  Job,
  JobState,
  JobType,
  LogEntry,
  Policy,
  PolicyRequest,
  Release,
  ReleaseProduct,
  Run,
  StorageTarget,
  SystemJobType,
  UpdateAgentSettingsRequest,
} from './models';

type Query = Record<string, string | number | boolean | null | undefined>;

function params(query: Query): HttpParams {
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
      limit?: number;
    } = {},
  ): Observable<Job[]> {
    return this.http.get<Job[]>(`${this.base}/jobs`, { params: params(query) });
  }

  job(id: string): Observable<Job> {
    return this.http.get<Job>(`${this.base}/jobs/${id}`);
  }

  cancelJob(id: string): Observable<Job> {
    return this.http.post<Job>(`${this.base}/jobs/${id}/cancel`, null);
  }

  runs(query: { agentId?: string; policyId?: string; limit?: number } = {}): Observable<Run[]> {
    return this.http.get<Run[]>(`${this.base}/runs`, { params: params(query) });
  }

  logs(
    query: { agentId?: string; jobId?: string; level?: string; limit?: number } = {},
  ): Observable<LogEntry[]> {
    return this.http.get<LogEntry[]>(`${this.base}/logs`, { params: params(query) });
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
}
