import { getBrowserLang } from '@jsverse/transloco';
import { Language, SUPPORTED_LANGUAGES } from './models';

const DEFAULT_LANGUAGE: Language = 'en';

/** The user's saved preference, else the browser's language if we support it, else English. */
export function resolveLanguage(saved: Language | null): Language {
  if (saved) return saved;
  const browser = getBrowserLang();
  return (SUPPORTED_LANGUAGES as readonly string[]).includes(browser ?? '')
    ? (browser as Language)
    : DEFAULT_LANGUAGE;
}
