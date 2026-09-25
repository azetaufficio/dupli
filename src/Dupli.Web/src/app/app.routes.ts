import { inject } from '@angular/core';
import { Routes } from '@angular/router';
import { AuthService } from './core/auth.service';

export const routes: Routes = [
  {
    path: '',
    title: 'Dashboard · Dupli',
    loadComponent: () => import('./pages/dashboard').then((m) => m.DashboardPage),
  },
  {
    path: 'agents',
    title: 'Agents · Dupli',
    loadComponent: () => import('./pages/agents').then((m) => m.AgentsPage),
  },
  {
    path: 'agents/:id',
    title: 'Agent · Dupli',
    loadComponent: () => import('./pages/agent-detail').then((m) => m.AgentDetailPage),
  },
  {
    path: 'agents/:agentId/policies/new',
    title: 'New policy · Dupli',
    loadComponent: () => import('./pages/policy-editor').then((m) => m.PolicyEditorPage),
  },
  {
    path: 'policies/:id',
    title: 'Policy · Dupli',
    loadComponent: () => import('./pages/policy-editor').then((m) => m.PolicyEditorPage),
  },
  {
    path: 'history',
    title: 'Backup history · Dupli',
    loadComponent: () => import('./pages/history').then((m) => m.HistoryPage),
  },
  {
    path: 'logs',
    title: 'Logs · Dupli',
    loadComponent: () => import('./pages/logs').then((m) => m.LogsPage),
  },
  {
    path: 'alerts',
    title: 'Alerts · Dupli',
    loadComponent: () => import('./pages/alerts').then((m) => m.AlertsPage),
  },
  {
    path: 'notifications',
    title: 'Notifications · Dupli',
    loadComponent: () => import('./pages/notifications').then((m) => m.NotificationsPage),
  },
  {
    path: 'settings/notifications',
    title: 'Notification settings · Dupli',
    loadComponent: () =>
      import('./pages/notification-settings').then((m) => m.NotificationSettingsPage),
  },
  {
    path: 'storage',
    title: 'Storage · Dupli',
    loadComponent: () => import('./pages/storage').then((m) => m.StoragePage),
  },
  {
    path: 'releases',
    title: 'Releases · Dupli',
    loadComponent: () => import('./pages/releases').then((m) => m.ReleasesPage),
  },
  {
    path: 'users',
    title: 'Users · Dupli',
    canMatch: [() => inject(AuthService).isOwner()],
    loadComponent: () => import('./pages/users').then((m) => m.UsersPage),
  },
  {
    path: 'admin',
    title: 'Break-glass · Dupli',
    loadComponent: () => import('./pages/admin').then((m) => m.AdminPage),
  },
  {
    path: 'access-denied',
    title: 'Access denied · Dupli',
    loadComponent: () => import('./pages/access-denied').then((m) => m.AccessDeniedPage),
  },
  { path: '**', redirectTo: '' },
];
