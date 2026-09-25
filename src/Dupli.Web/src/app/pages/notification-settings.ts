import { Component } from '@angular/core';
import { TranslocoModule } from '@jsverse/transloco';
import { NotificationPreferences } from '../shared/notification-preferences';

@Component({
  selector: 'app-notification-settings',
  imports: [NotificationPreferences, TranslocoModule],
  template: `
    <div class="page-header">
      <div>
        <h1>{{ 'notificationSettings.title' | transloco }}</h1>
        <p class="muted">{{ 'notificationSettings.subtitle' | transloco }}</p>
      </div>
    </div>
    <section class="card flush">
      <app-notification-preferences />
    </section>
  `,
})
export class NotificationSettingsPage {}
