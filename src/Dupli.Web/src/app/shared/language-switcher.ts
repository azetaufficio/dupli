import { Component, inject } from '@angular/core';
import { TranslocoModule } from '@jsverse/transloco';
import { AuthService } from '../core/auth.service';
import { Language, SUPPORTED_LANGUAGES } from '../core/models';

const LANGUAGE_NAMES: Record<Language, string> = { en: 'English', it: 'Italiano' };

/** Two-button switch (EN/IT) in the user dropdown; saves via AuthService.setLanguage. */
@Component({
  selector: 'app-language-switcher',
  imports: [TranslocoModule],
  template: `
    <div class="language-switcher" role="group" [attr.aria-label]="'userMenu.language' | transloco">
      @for (lang of languages; track lang) {
        <button
          type="button"
          class="btn small"
          [class.active]="lang === auth.user()?.language"
          [attr.aria-pressed]="lang === auth.user()?.language"
          (click)="auth.setLanguage(lang)"
        >
          {{ names[lang] }}
        </button>
      }
    </div>
  `,
})
export class LanguageSwitcher {
  protected readonly auth = inject(AuthService);
  protected readonly languages = SUPPORTED_LANGUAGES;
  protected readonly names = LANGUAGE_NAMES;
}
