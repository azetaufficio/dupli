import { Component } from '@angular/core';
import { TranslocoModule } from '@jsverse/transloco';
import { OperatorUsers } from '../shared/operator-users';

@Component({
  selector: 'app-users',
  imports: [OperatorUsers, TranslocoModule],
  template: `
    <div class="page-header">
      <div>
        <h1>{{ 'users.title' | transloco }}</h1>
        <p class="muted" [innerHTML]="'users.subtitle' | transloco"></p>
      </div>
    </div>
    <app-operator-users />
  `,
})
export class UsersPage {}
