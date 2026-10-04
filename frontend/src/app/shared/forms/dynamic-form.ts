import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { FormArray, FormControl, FormGroup, ReactiveFormsModule } from '@angular/forms';
import { TranslocoDirective } from '@jsverse/transloco';
import type { CustomFieldDefinitionDto } from '../../core/api/models';
import { ConfigStore } from '../../core/config/config.store';
import { LanguageService } from '../../core/i18n/language.service';
import { DayNamePipe, LocalNamePipe, TermPipe } from '../pipes/pipes';
import { FieldDef } from './field-defs';
import { RefCache } from './ref-cache';

/** Renders a form from field definitions + the institution's custom field definitions (bilingual, RTL-safe). */
@Component({
  selector: 'app-dynamic-form',
  imports: [ReactiveFormsModule, TranslocoDirective, LocalNamePipe, TermPipe, DayNamePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dynamic-form.html',
  styleUrl: './dynamic-form.scss',
})
export class DynamicForm {
  readonly form = input.required<FormGroup>();
  readonly fields = input.required<FieldDef[]>();
  readonly customFields = input<CustomFieldDefinitionDto[]>([]);
  readonly serverErrors = input<Record<string, string>>({});

  protected readonly config = inject(ConfigStore);
  protected readonly refs = inject(RefCache);
  protected readonly lang = inject(LanguageService);

  protected readonly visibleFields = computed(() => this.fields().filter((f) => !f.feature || this.config.feature(f.feature)));
  protected readonly allDays = [0, 1, 2, 3, 4, 5, 6];

  protected control(key: string): FormControl { return this.form().get(key) as FormControl; }
  protected cfGroup(): FormGroup { return this.form().get('customFields') as FormGroup; }

  protected isTerm(label: string): boolean { return label.startsWith('term:'); }

  protected options(f: FieldDef): { value: string; label: string }[] {
    if (f.lookup) return this.config.lookups(f.lookup).map((l) => ({ value: f.valueBy === 'code' ? l.code : l.id, label: this.config.name(l) || l.code }));
    if (f.ref) return this.refs.items(f.ref, f.refQuery).map((i) => ({ value: i.id, label: this.refs.itemLabel(i) }));
    if (f.optionsSource === 'periods') return this.config.periods().filter((p) => !p.isBreak).map((p) => ({ value: String(p.index), label: this.lang.pick(p.nameAr, p.nameEn) || `${p.index + 1}` }));
    if (f.optionsSource === 'shifts') return (this.config.time()?.shifts ?? []).map((x) => ({ value: x.id, label: this.lang.pick(x.nameAr, x.nameEn) || x.code }));
    return (f.options ?? []).map((o) => ({ value: String(o.value), label: o.label }));
  }

  protected toggleMulti(key: string, value: string | number): void {
    const c = this.control(key);
    const current: (string | number)[] = [...(c.value ?? [])];
    const i = current.indexOf(value);
    if (i >= 0) current.splice(i, 1); else current.push(value);
    c.setValue(current);
    c.markAsDirty();
  }

  protected has(key: string, value: string | number): boolean { return (this.control(key).value ?? []).includes(value); }

  protected tagsText(key: string): string { return (this.control(key).value ?? []).join(', '); }
  protected setTags(key: string, text: string): void {
    this.control(key).setValue(text.split(/[,،]/).map((t) => t.trim()).filter((t) => t.length > 0));
    this.control(key).markAsDirty();
  }

  protected weekBit(key: string, week: number): boolean {
    const mask = this.control(key).value ?? 0;
    return mask === 0 || (mask & (1 << week)) !== 0;
  }
  protected toggleWeek(key: string, week: number): void {
    const n = this.config.weekLabels().length;
    const all = (1 << n) - 1;
    let mask: number = this.control(key).value || all;
    mask ^= 1 << week;
    if (mask === 0) return; // at least one week
    this.control(key).setValue(mask === all ? 0 : mask);
    this.control(key).markAsDirty();
  }

  protected calendar(key: string): FormArray { return this.form().get(key) as FormArray; }
  protected addCalendarDay(key: string): void {
    this.calendar(key).push(new FormGroup({ date: new FormControl(''), kind: new FormControl('Holiday'), nameAr: new FormControl(''), nameEn: new FormControl('') }));
  }

  protected errorFor(key: string): string | null {
    const server = this.serverErrors()[key];
    if (server) return server;
    const c = this.form().get(key);
    if (!c || !c.invalid || !(c.touched || c.dirty)) return null;
    if (c.errors?.['required']) return 'validation.required';
    if (c.errors?.['min'] || c.errors?.['max']) return 'validation.range';
    if (c.errors?.['pattern']) return 'validation.pattern';
    return 'validation.invalid';
  }

  protected cfOptions(f: CustomFieldDefinitionDto): { value: string; labelAr?: string; labelEn?: string }[] {
    return (f.options as unknown as { value: string; labelAr?: string; labelEn?: string }[] | null) ?? [];
  }
  protected cfHas(key: string, v: string): boolean { return (this.cfGroup().get(key)?.value ?? []).includes(v); }
  protected cfToggle(key: string, v: string): void {
    const c = this.cfGroup().get(key)!;
    const arr: string[] = [...(c.value ?? [])];
    const i = arr.indexOf(v);
    if (i >= 0) arr.splice(i, 1); else arr.push(v);
    c.setValue(arr);
    c.markAsDirty();
  }
}
