import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { TranslocoDirective, TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError, saveBlob } from '../../core/api/api';
import type { ImportKindDto, ImportReportDto, ImportRowDto } from '../../core/api/models';
import { AuthStore } from '../../core/auth/auth.store';
import { ConfigStore } from '../../core/config/config.store';
import { LanguageService } from '../../core/i18n/language.service';
import { ScheduleContext } from '../../core/schedule/schedule-context';
import { ToastService } from '../../core/ui/toast.service';
import { RefCache } from '../../shared/forms/ref-cache';
import { NumPipe, TermPipe } from '../../shared/pipes/pipes';
import { Icon } from '../../shared/ui/icon';

type View = 'group' | 'instructor' | 'room';
type Format = 'pdf' | 'xlsx' | 'csv';

const ENDPOINT: Record<View, string> = { group: '/groups', instructor: '/instructors', room: '/rooms' };

/** Timetable exports (PDF / Excel / CSV, per resource, Arabic or English) and master-data imports with a dry-run report. */
@Component({
  selector: 'app-exports-page',
  imports: [TranslocoDirective, Icon, NumPipe, TermPipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './exports.page.html',
  styleUrl: './exports.page.scss',
})
export class ExportsPage {
  protected readonly context = inject(ScheduleContext);
  protected readonly config = inject(ConfigStore);
  protected readonly refs = inject(RefCache);
  protected readonly auth = inject(AuthStore);
  private readonly api = inject(Api);
  private readonly toast = inject(ToastService);
  private readonly lang = inject(LanguageService);
  private readonly transloco = inject(TranslocoService);

  // ---- export ----
  protected readonly views: View[] = ['group', 'instructor', 'room'];
  protected readonly formats: { id: Format; icon: string }[] = [{ id: 'pdf', icon: 'file' }, { id: 'xlsx', icon: 'table' }, { id: 'csv', icon: 'layers' }];
  protected readonly view = signal<View>('group');
  protected readonly format = signal<Format>('pdf');
  protected readonly language = signal<'ar' | 'en'>(this.lang.lang());
  protected readonly week = signal<number | null>(null);
  protected readonly selected = signal<Set<string>>(new Set());
  protected readonly filter = signal('');
  protected readonly exporting = signal(false);
  protected readonly resources = computed(() => {
    const q = this.filter().trim().toLowerCase();
    return this.refs.items(ENDPOINT[this.view()]).filter((r) => !q || this.refs.itemLabel(r).toLowerCase().includes(q));
  });

  // ---- import ----
  protected readonly kinds = signal<ImportKindDto[]>([]);
  protected readonly kind = signal<string>('rooms');
  protected readonly mode = signal<'upsert' | 'insert'>('upsert');
  protected readonly file = signal<File | null>(null);
  protected readonly report = signal<ImportReportDto | null>(null);
  protected readonly onlyIssues = signal(true);
  protected readonly importing = signal(false);
  protected readonly dragOver = signal(false);
  protected readonly kindColumns = computed(() => this.kinds().find((k) => k.kind === this.kind())?.columns ?? []);
  protected readonly visibleRows = computed<ImportRowDto[]>(() =>
    (this.report()?.rows ?? []).filter((r) => !this.onlyIssues() || r.action === 'error' || r.issues.length > 0));
  protected readonly canApply = computed(() => {
    const r = this.report();
    return !!r && r.dryRun && r.failed === 0 && r.created + r.updated > 0 && !!this.file();
  });

  constructor() {
    void this.context.ensureLoaded();
    if (this.auth.has('imports.run')) {
      void firstValueFrom(this.api.get<ImportKindDto[]>('/imports')).then((k) => this.kinds.set(k)).catch(() => undefined);
    }
  }

  protected setView(v: View): void {
    this.view.set(v);
    this.selected.set(new Set());
    this.filter.set('');
  }

  protected toggle(id: string): void {
    this.selected.update((s) => { const n = new Set(s); if (n.has(id)) n.delete(id); else n.add(id); return n; });
  }

  protected label(r: { code?: string; nameAr?: string | null; nameEn?: string | null; id: string }): string { return this.refs.itemLabel(r); }

  protected async download(): Promise<void> {
    const schedule = this.context.current();
    if (!schedule) return;
    const q = new URLSearchParams({ format: this.format(), view: this.view(), lang: this.language() });
    if (this.week() !== null) q.set('week', String(this.week()));
    for (const id of this.selected()) q.append('ids', id);
    this.exporting.set(true);
    try {
      const blob = await firstValueFrom(this.api.download(`/schedules/${schedule.id}/export?${q.toString()}`));
      saveBlob(blob, `${schedule.name}-${this.view()}.${this.format()}`);
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : null);
    } finally {
      this.exporting.set(false);
    }
  }

  // ---- import actions ----
  protected async template(format: 'xlsx' | 'csv'): Promise<void> {
    try {
      const blob = await firstValueFrom(this.api.download(`/imports/${this.kind()}/template`, { format }));
      saveBlob(blob, `${this.kind()}-template.${format}`);
    } catch { this.toast.error(null); }
  }

  protected pickFile(input: HTMLInputElement): void {
    this.setFile(input.files?.[0] ?? null);
    input.value = '';
  }

  protected dropped(ev: DragEvent): void {
    ev.preventDefault();
    this.dragOver.set(false);
    this.setFile(ev.dataTransfer?.files?.[0] ?? null);
  }

  private setFile(f: File | null): void {
    this.file.set(f);
    this.report.set(null);
    if (f) void this.run(true);
  }

  protected setKind(k: string): void {
    this.kind.set(k);
    this.report.set(null);
    this.file.set(null);
  }

  protected async run(dryRun: boolean): Promise<void> {
    const f = this.file();
    if (!f) return;
    const form = new FormData();
    form.append('file', f, f.name);
    this.importing.set(true);
    try {
      const r = await firstValueFrom(this.api.upload<ImportReportDto>(`/imports/${this.kind()}`, form, { dryRun, mode: this.mode() }));
      this.report.set(r);
      if (!dryRun && r.applied) {
        this.toast.success('exports.import.applied', { created: r.created, updated: r.updated });
        this.refs.invalidate(`/${this.kind()}`);
        this.file.set(null);
      }
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : null);
    } finally {
      this.importing.set(false);
    }
  }

  protected kindLabel(kind: string): string {
    const term: Record<string, string> = { instructors: 'instructors', courses: 'courses', groups: 'groups', rooms: 'rooms' };
    return term[kind] ? this.config.term(term[kind]) : this.transloco.translate(`exports.kinds.${kind}`);
  }
}
