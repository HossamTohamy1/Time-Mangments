import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TranslocoDirective } from '@jsverse/transloco';
import type { BoardSessionDto, ViolationDto } from '../../core/api/models';
import { ConfigStore } from '../../core/config/config.store';
import { ThemeService } from '../../core/theme/theme.service';
import { deriveColors } from '../../shared/ui/color';
import { Icon } from '../../shared/ui/icon';

/**
 * A session block in a timetable grid (editor, self-service, print). Colors come from the session type's
 * configured color, adapted to the active theme; hard conflicts and soft penalties change the frame.
 */
@Component({
  selector: 'app-session-card',
  imports: [TranslocoDirective, Icon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'tt-session-card',
    '[class.conflict]': 'hard().length > 0',
    '[class.soft]': 'hard().length === 0 && soft().length > 0',
    '[class.pinned]': 'pinned()',
    '[style.--c]': 'colors().accent',
    '[style.--cbg]': 'colors().background',
    '[style.--ctext]': 'colors().text',
    '[attr.title]': 'tooltip()',
  },
  template: `
    <ng-container *transloco="let t">
      @if (hard().length > 0) {
        <div class="conflict-head">
          <app-icon name="alertCircle" />
          <span class="title">{{ title() }}</span>
          <span class="count">{{ t('timetable.card.hard', { count: hard().length }) }}</span>
        </div>
        @for (v of hard().slice(0, 2); track $index) { <div class="issue">{{ v.message }}</div> }
      } @else {
        <div class="head">
          <h4 class="title">{{ title() }}@if (pinned()) {<app-icon class="lock" name="lock" [size]="13" />}</h4>
          <span class="badge">{{ typeName() }}</span>
        </div>
        @if (instructor()) { <div class="meta"><app-icon name="user" [size]="14" /><span>{{ instructor() }}</span></div> }
        @if (!compact()) {
          <div class="foot">
            <span class="room">@if (room()) {<app-icon name="door" [size]="13" />{{ room() }}}</span>
            @if (pinned()) { <span class="tag"><app-icon name="lock" [size]="11" />{{ t('timetable.card.pinned') }}</span> }
            @else if (group()) { <span class="tag">{{ group() }}</span> }
          </div>
        }
      }
    </ng-container>
  `,
  styleUrl: './session-card.scss',
})
export class SessionCard {
  private readonly config = inject(ConfigStore);
  private readonly theme = inject(ThemeService);

  readonly session = input.required<BoardSessionDto>();
  readonly instructor = input<string>('');
  readonly room = input<string>('');
  readonly group = input<string>('');
  readonly pinned = input(false);
  readonly compact = input(false);
  readonly violations = input<ViolationDto[]>([]);

  protected readonly hard = computed(() => this.violations().filter((v) => v.severity === 'Hard'));
  protected readonly soft = computed(() => this.violations().filter((v) => v.severity !== 'Hard' && v.penalty > 0));
  protected readonly type = computed(() => this.config.lookup('session-types', this.session().sessionTypeId));
  protected readonly typeName = computed(() => this.config.name(this.type()) || this.type()?.code || '');
  protected readonly colors = computed(() => deriveColors(this.type()?.color, this.theme.effective()));
  protected readonly title = computed(() => {
    const s = this.session();
    const name = this.config.name(s);
    return name ? `${s.courseCode} ${name}` : s.courseCode;
  });
  protected readonly tooltip = computed(() =>
    [this.title(), this.typeName(), this.instructor(), this.room(), this.group(), ...this.violations().map((v) => v.message)].filter(Boolean).join('\n'));
}
