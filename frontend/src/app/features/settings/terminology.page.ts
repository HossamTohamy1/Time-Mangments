import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type { TerminologyEntry } from '../../core/api/models';
import { ConfigStore } from '../../core/config/config.store';
import { ToastService } from '../../core/ui/toast.service';
import { Icon } from '../../shared/ui/icon';

/** Every UI term that institutions commonly rename; any other `term.*` key can be added too. */
export const KNOWN_TERMS = ['group', 'groups', 'instructor', 'instructors', 'course', 'courses', 'session', 'sessions', 'room', 'rooms', 'orgUnit', 'term'];

@Component({
  selector: 'app-terminology-page',
  imports: [TranslocoDirective, FormsModule, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head">
        <app-icon name="language" [size]="22" class="head-icon" /><h1>{{ t('settings.terminology.title') }}</h1>
        <div class="tt-spacer"></div>
        <button type="button" class="tt-btn primary" [disabled]="busy()" (click)="save()">{{ t('common.save') }}</button>
      </div>
      <p class="tt-muted">{{ t('settings.terminology.subtitle') }}</p>
      <div class="tt-card">
        <table class="tt-table">
          <thead><tr><th>{{ t('settings.terminology.key') }}</th><th>{{ t('settings.terminology.default') }}</th><th>English</th><th>العربية</th></tr></thead>
          <tbody>
            @for (row of rows(); track row.key) {
              <tr>
                <td class="code" dir="ltr">{{ row.key }}</td>
                <td class="tt-muted">{{ defaultFor(row.key) }}</td>
                <td><input class="tt-input" [(ngModel)]="row.en" dir="ltr" [attr.aria-label]="row.key + ' en'" /></td>
                <td><input class="tt-input" [(ngModel)]="row.ar" dir="rtl" [attr.aria-label]="row.key + ' ar'" /></td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    </div>
  `,
  styles: [`.head-icon { color: var(--tt-primary); } .code { font-family: ui-monospace, monospace; font-size: 12px; } .tt-input { inline-size: 100%; }`],
})
export class TerminologyPage {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly config = inject(ConfigStore);
  private readonly transloco = inject(TranslocoService);
  protected readonly rows = signal<{ key: string; en: string; ar: string }[]>([]);
  protected readonly busy = signal(false);

  constructor() { void this.load(); }

  protected defaultFor(key: string): string { return this.transloco.translate(key.replace(/^term\./, 'terms.')); }

  async load(): Promise<void> {
    const existing = await firstValueFrom(this.api.get<TerminologyEntry[]>('/config/terminology'));
    const keys = [...new Set([...KNOWN_TERMS.map((k) => `term.${k}`), ...existing.map((e) => e.key)])];
    this.rows.set(keys.map((k) => {
      const e = existing.find((x) => x.key === k);
      return { key: k, en: e?.en ?? '', ar: e?.ar ?? '' };
    }));
  }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.put('/config/terminology', this.rows().map((r) => ({ key: r.key, en: r.en || null, ar: r.ar || null }))));
      await this.config.load(true);
      this.toast.success('common.saved');
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : String(e));
    } finally {
      this.busy.set(false);
    }
  }
}
