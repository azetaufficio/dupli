import { Component } from '@angular/core';
import { OperatorUsers } from '../shared/operator-users';

@Component({
  selector: 'app-users',
  imports: [OperatorUsers],
  template: `
    <div class="page-header">
      <div>
        <h1>Users</h1>
        <p class="muted">
          Who may sign in. <strong>Viewer</strong> reads only; <strong>Operator</strong> runs
          backups, browses snapshots and restores; <strong>Owner</strong> also manages users,
          storage targets and releases.
        </p>
      </div>
    </div>
    <app-operator-users />
  `,
})
export class UsersPage {}
