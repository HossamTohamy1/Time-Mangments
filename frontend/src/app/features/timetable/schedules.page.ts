import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import type { ScheduleSummaryDto } from '../../core/api/models';
import { AuthStore } from '../../core/auth/auth.store';
import { ScheduleContext } from '../../core/schedule/schedule-context';
import { ToastService } from '../../core/ui/toast.service';
import { LocalDatePipe, NumPipe } from '../../shared/pipes/pipes';
import { ConfirmService } from '../../shared/ui/confirm';
import { Icon } from '../../shared/ui/icon';

/** Schedule versions: open, copy, rename, publish and delete drafts. */
@Component({
  selector: 'app-schedules-page',
  imports: [TranslocoDirective, RouterLink, Icon, NumPipe, LocalDatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './schedules.page.html',
  styleUrl: './schedules.page.scss',
})
export class SchedulesPage {
  protected readonly context = inject(ScheduleContext);
  private readonly api = inject(Api);
  private readonly auth = inject(AuthStore);
  private readonly toast = inject(ToastService);
  private readonly confirm = inject(ConfirmService);
  private readonly router = inject(Router);

  protected readonly renaming = signal<string | null>(null);
  protected readonly busy = signal(false);
  protected readonly canEdit = computed(() => this.auth.has('timetable.edit'));
  protected readonly canPublish = computed(() => this.auth.has('schedule.publish'));

  constructor() {
    void this.context.refresh();
  }

  protected open(s: ScheduleSummaryDto): void {
    this.context.select(s.id);
    void this.router.navigateByUrl('/timetable');
  }

  protected async clone(s: ScheduleSummaryDto): Promise<void> {
    await this.run(async () => {
      const created = await firstValueFrom(this.api.post<ScheduleSummaryDto>(`/schedules/${s.id}/clone`, { name: null }));
      this.toast.success('timetable.editor.draftCreated', { name: created.name });
    });
  }

  protected async rename(s: ScheduleSummaryDto, name: string): Promise<void> {
    this.renaming.set(null);
    if (!name.trim() || name.trim() === s.name) return;
    await this.run(() => firstValueFrom(this.api.put(`/schedules/${s.id}/name`, { name: name.trim() })).then(() => this.toast.success('common.saved')));
  }

  protected async publish(s: ScheduleSummaryDto): Promise<void> {
    const ok = await this.confirm.ask({ titleKey: 'timetable.editor.publishTitle', messageKey: 'timetable.versions.publishMessage', params: { name: s.name }, confirmKey: 'timetable.editor.publish' });
    if (!ok) return;
    await this.run(() => firstValueFrom(this.api.post(`/schedules/${s.id}/publish`)).then(() => this.toast.success('timetable.editor.published', { name: s.name })));
  }

  protected async remove(s: ScheduleSummaryDto): Promise<void> {
    const ok = await this.confirm.ask({ titleKey: 'common.deleteTitle', messageKey: 'common.deleteMessage', params: { name: s.name }, danger: true, confirmKey: 'common.delete' });
    if (!ok) return;
    await this.run(() => firstValueFrom(this.api.delete(`/schedules/${s.id}`)).then(() => this.toast.success('common.deleted')));
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    try {
      await action();
      await this.context.refresh();
    } catch (e) {
      this.toast.error(e instanceof ApiError ? e.message : null);
    } finally {
      this.busy.set(false);
    }
  }
}
