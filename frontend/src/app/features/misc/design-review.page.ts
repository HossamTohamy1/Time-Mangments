import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import { LanguageService } from '../../core/i18n/language.service';
import { ThemeService } from '../../core/theme/theme.service';
import { ToastService } from '../../core/ui/toast.service';
import { ConfirmService } from '../../shared/ui/confirm';
import { Icon, ICON_NAMES } from '../../shared/ui/icon';

/** Route-based design review: core components side by side, to inspect all four language/theme combinations quickly. */
@Component({
  selector: 'app-design-review-page',
  imports: [TranslocoDirective, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="tt-page" *transloco="let t">
      <div class="tt-page-head">
        <h1>{{ t('design.title') }}</h1>
        <div class="tt-spacer"></div>
        <div class="tt-seg">
          <button type="button" [attr.aria-pressed]="lang.lang() === 'en'" (click)="lang.set('en')">EN</button>
          <button type="button" [attr.aria-pressed]="lang.lang() === 'ar'" (click)="lang.set('ar')">AR</button>
        </div>
        <div class="tt-seg">
          <button type="button" [attr.aria-pressed]="theme.effective() === 'light'" (click)="theme.set('light')"><app-icon name="sun" /></button>
          <button type="button" [attr.aria-pressed]="theme.effective() === 'dark'" (click)="theme.set('dark')"><app-icon name="moon" /></button>
        </div>
      </div>
      <section class="tt-card pad"><h2>{{ t('design.buttons') }}</h2>
        <div class="tt-row wrap">
          <button class="tt-btn" type="button">{{ t('common.cancel') }}</button>
          <button class="tt-btn primary" type="button">{{ t('common.save') }}</button>
          <button class="tt-btn outline-primary" type="button"><app-icon name="checkList" /> {{ t('design.validate') }}</button>
          <button class="tt-btn soft" type="button"><app-icon name="wand" /> {{ t('design.autoPlace') }}</button>
          <button class="tt-btn danger" type="button"><app-icon name="alert" /> {{ t('design.conflicts') }}</button>
          <button class="tt-btn" type="button" disabled>{{ t('design.disabled') }}</button>
          <button class="tt-btn" type="button"><app-icon name="arrowRight" /> {{ t('common.next') }}</button>
        </div>
      </section>
      <section class="tt-card pad"><h2>{{ t('design.chips') }}</h2>
        <div class="tt-row wrap">
          <span class="tt-chip valid"><app-icon name="check" /> {{ t('timetable.status.valid') }}</span>
          <span class="tt-chip penalty"><app-icon name="alert" /> {{ t('timetable.status.penalty') }}</span>
          <span class="tt-chip conflict"><app-icon name="alertCircle" /> {{ t('timetable.status.conflict') }}</span>
          <span class="tt-chip primary"><app-icon name="lock" /> {{ t('timetable.pinned') }}</span>
          <span class="tt-chip" dir="auto">{{ sample.chip }}</span>
        </div>
      </section>
      <section class="tt-card pad"><h2>{{ t('design.inputs') }}</h2>
        <div class="tt-grid-2">
          <label class="tt-field">{{ t('fields.nameEn') }}<input class="tt-input" dir="ltr" [value]="sample.hallEn" /></label>
          <label class="tt-field">{{ t('fields.nameAr') }}<input class="tt-input" dir="rtl" [value]="sample.hallAr" /></label>
          <label class="tt-field">{{ t('fields.roomType') }}<select class="tt-input">@for (o of sample.types; track o) { <option>{{ o }}</option> }</select></label>
          <label class="tt-field">{{ t('fields.notes') }}<textarea class="tt-input" rows="2" dir="auto" [value]="sample.note"></textarea></label>
        </div>
      </section>
      <section class="tt-card pad"><h2>{{ t('design.table') }}</h2>
        <table class="tt-table"><thead><tr><th>{{ t('fields.code') }}</th><th>{{ t('fields.name') }}</th><th>{{ t('fields.capacity') }}</th></tr></thead>
          <tbody>@for (r of sample.rows; track r[0]) { <tr><td>{{ r[0] }}</td><td dir="auto">{{ r[1] }}</td><td>{{ r[2] }}</td></tr> }</tbody></table>
      </section>
      <section class="tt-card pad"><h2>{{ t('design.feedback') }}</h2>
        <div class="tt-row wrap">
          <button class="tt-btn" type="button" (click)="toast.success('common.saved')">{{ t('design.toastSuccess') }}</button>
          <button class="tt-btn" type="button" (click)="toast.error(null, 'errors.unexpected')">{{ t('design.toastError') }}</button>
          <button class="tt-btn" type="button" (click)="confirm.ask({ titleKey: 'common.deleteTitle', messageKey: 'common.deleteMessage', params: { name: 'H1' }, danger: true })">{{ t('design.dialog') }}</button>
        </div>
        <div class="skeletons"><div class="tt-skeleton"></div><div class="tt-skeleton"></div><div class="tt-skeleton short"></div></div>
      </section>
      <section class="tt-card pad"><h2>{{ t('design.gridCells') }}</h2>
        <div class="cells">
          <div class="cell valid"><app-icon name="plusCircle" /> {{ t('timetable.status.valid') }}</div>
          <div class="cell penalty"><app-icon name="alert" /> {{ t('timetable.status.penalty') }}</div>
          <div class="cell conflict"><app-icon name="alertCircle" /> {{ t('timetable.status.conflict') }}</div>
          <div class="cell pinned"><app-icon name="lock" /> {{ t('timetable.pinned') }}</div>
        </div>
      </section>
      <section class="tt-card pad"><h2>{{ t('design.icons') }}</h2>
        <div class="icons">@for (n of icons; track n) { <span class="ic" [title]="n"><app-icon [name]="n" [size]="20" /></span> }</div>
      </section>
    </div>
  `,
  styles: [`.pad { padding: 16px; display: flex; flex-direction: column; gap: 10px; } h2 { margin: 0; font-size: 15px; } .wrap { flex-wrap: wrap; }
    .skeletons { display: grid; gap: 8px; max-inline-size: 420px; .short { inline-size: 60%; } }
    .cells { display: grid; grid-template-columns: repeat(auto-fill, minmax(160px, 1fr)); gap: 10px; }
    .cell { padding: 16px; border-radius: 10px; border: 2px dashed; display: flex; gap: 8px; align-items: center; font-weight: 600;
      &.valid { color: var(--tt-valid); background: var(--tt-valid-bg); border-color: var(--tt-valid-border); }
      &.penalty { color: var(--tt-penalty); background: var(--tt-penalty-bg); border-color: var(--tt-penalty-border); }
      &.conflict { color: var(--tt-conflict); background: var(--tt-conflict-bg); border-color: var(--tt-conflict-border); }
      &.pinned { color: var(--tt-pinned); border-style: solid; border-color: var(--tt-pinned); } }
    .icons { display: flex; flex-wrap: wrap; gap: 8px; } .ic { inline-size: 36px; block-size: 36px; display: grid; place-items: center; border: 1px solid var(--tt-border); border-radius: 8px; }`],
})
export class DesignReviewPage {
  protected readonly lang = inject(LanguageService);
  protected readonly theme = inject(ThemeService);
  protected readonly toast = inject(ToastService);
  protected readonly confirm = inject(ConfirmService);
  protected readonly icons = ICON_NAMES;
  /** Sample user-generated content (mixed scripts) to check bidi rendering; not UI text. */
  protected readonly sample = {
    chip: 'CS201 · هياكل البيانات', hallEn: 'Lecture Hall 1', hallAr: 'مدرج ١ — CS201', types: ['HALL', 'LAB'], note: 'مقرر CS201 يبدأ الأحد',
    rows: [['H1', 'Lecture Hall 1', 250], ['LAB3', 'معمل 3', 45]] as [string, string, number][],
  };
}
