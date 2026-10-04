import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Translation, TranslocoLoader } from '@jsverse/transloco';

/** Loads `/i18n/{lang}.json` for the root and `/i18n/{scope}/{lang}.json` for lazy feature scopes. */
@Injectable({ providedIn: 'root' })
export class TranslocoHttpLoader implements TranslocoLoader {
  private readonly http = inject(HttpClient);

  getTranslation(langPath: string) {
    return this.http.get<Translation>(`/i18n/${langPath}.json`);
  }
}
