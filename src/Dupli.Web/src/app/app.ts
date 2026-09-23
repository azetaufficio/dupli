import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from './core/auth.service';
import { ConfirmDialog } from './shared/confirm';
import { Toasts } from './shared/toasts';

@Component({
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ConfirmDialog, Toasts],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  protected readonly auth = inject(AuthService);
}
