import { Component } from '@angular/core';
import { NotificationPreferences } from '../shared/notification-preferences';

@Component({
  selector: 'app-notification-settings',
  imports: [NotificationPreferences],
  template: `
    <div class="page-header">
      <div>
        <h1>Notification settings</h1>
        <p class="muted">
          Choose e-mail and/or in-app for each alert type. Unset types default to in-app for
          everyone, and mail for Owner and Operator.
        </p>
      </div>
    </div>
    <section class="card flush">
      <app-notification-preferences />
    </section>
  `,
})
export class NotificationSettingsPage {}
