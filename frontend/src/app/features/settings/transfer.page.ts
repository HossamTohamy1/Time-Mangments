import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError, saveBlob } from '../../core/api/api';
import type { ConfigChangePreview, InstitutionDto, TemplateSummaryDto } from '../../core/api/models';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';
import { ToastService } from '../../core/ui/toast.service';
import { LocalNamePipe } from '../../shared/pipes/pipes';
import { ConfirmService } from '../../shared/ui/confirm';
import { Icon } from '../../shared/ui/icon';

/** Institution details + configuration transfer: export, import with preview, apply template, save as template. */
@Component({
  selector: 'app-transfer-page',
  imports: [TranslocoDirective, FormsModule, Icon, LocalNamePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './transfer.page.html',
  styles: [`.head-icon { color: var(--tt-primary); } .pad { padding: 16px; display: flex; flex-direction: column; gap: 10px; } h2 { margin: 0; font-size: 16px; }
    .preview { max-block-size: 320px; overflow: auto; } .error { color: var(--tt-conflict); }`],
})
export class TransferPage {
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly auth = inject(AuthStore);
  protected readonly config = inject(ConfigStore);

  protected readonly institution = computed(() => this.config.config()?.institution ?? null);
  protected readonly details = signal({ nameEn: '', nameAr: '', defaultLanguage: 'en', timeZone: 'Africa/Cairo' });
  protected readonly templates = signal<TemplateSummaryDto[]>([]);
  protected readonly templateCode = signal('');
  protected readonly preview = signal<ConfigChangePreview[] | null>(null);
  protected readonly pendingImport = signal<unknown | null>(null);
  protected readonly pendingKind = signal<'import' | 'template' | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly saveAs = signal({ code: '', nameEn: '', nameAr: '' });

  protected readonly changes = computed(() => (this.preview() ?? []).filter((c) => c.action !== 'unchanged'));

  constructor() {
    const i = this.institution();
    if (i) this.details.set({ nameEn: i.nameEn ?? '', nameAr: i.nameAr ?? '', defaultLanguage: i.defaultLanguage, timeZone: i.timeZone });
    this.api.get<TemplateSummaryDto[]>('/templates').subscribe((t) => this.templates.set(t));
  }

  async saveDetails(): Promise<void> {
    try {
      await firstValueFrom(this.api.put<InstitutionDto>('/institutions/current', this.details()));
      await Promise.all([this.config.load(true), this.auth.loadMe(false)]);
      this.toast.success('common.saved');
    } catch (e) { this.toast.error(e instanceof ApiError ? e.message : String(e)); }
  }

  async exportConfig(): Promise<void> {
    const blob = await firstValueFrom(this.api.download('/config/export'));
    saveBlob(blob, `${this.institution()?.code ?? 'institution'}-config.json`);
  }

  async onFile(e: Event): Promise<void> {
    const file = (e.target as HTMLInputElement).files?.[0];
    if (!file) return;
    this.error.set(null);
    try {
      const bundle = JSON.parse(await file.text());
      this.preview.set(await firstValueFrom(this.api.post<ConfigChangePreview[]>('/config/import', { bundle, dryRun: true })));
      this.pendingImport.set(bundle);
      this.pendingKind.set('import');
    } catch (err) {
      this.error.set(err instanceof ApiError ? err.message : String(err));
    }
  }

  async previewTemplate(): Promise<void> {
    this.error.set(null);
    try {
      this.preview.set(await firstValueFrom(this.api.post<ConfigChangePreview[]>('/config/apply-template', { templateCode: this.templateCode(), dryRun: true })));
      this.pendingKind.set('template');
    } catch (err) { this.error.set(err instanceof ApiError ? err.message : String(err)); }
  }

  async apply(): Promise<void> {
    if (!(await this.confirm.ask({ titleKey: 'settings.transfer.applyTitle', messageKey: 'settings.transfer.applyMessage', params: { n: this.changes().length } }))) return;
    try {
      if (this.pendingKind() === 'import') await firstValueFrom(this.api.post('/config/import', { bundle: this.pendingImport(), dryRun: false }));
      else await firstValueFrom(this.api.post('/config/apply-template', { templateCode: this.templateCode(), dryRun: false }));
      this.preview.set(null);
      this.pendingKind.set(null);
      await this.config.load(true);
      this.toast.success('settings.transfer.applied');
    } catch (err) { this.error.set(err instanceof ApiError ? err.message : String(err)); }
  }

  async saveAsTemplate(): Promise<void> {
    try {
      await firstValueFrom(this.api.post('/config/save-as-template', this.saveAs()));
      this.api.get<TemplateSummaryDto[]>('/templates').subscribe((t) => this.templates.set(t));
      this.toast.success('settings.transfer.templateSaved');
    } catch (err) { this.toast.error(err instanceof ApiError ? err.message : String(err)); }
  }

  protected patchDetails(k: string, v: string): void { this.details.update((d) => ({ ...d, [k]: v })); }
  protected patchSaveAs(k: string, v: string): void { this.saveAs.update((d) => ({ ...d, [k]: v })); }
}
