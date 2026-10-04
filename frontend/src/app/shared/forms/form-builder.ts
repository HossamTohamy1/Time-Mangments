import { FormArray, FormControl, FormGroup, ValidatorFn, Validators } from '@angular/forms';
import type { CustomFieldDefinitionDto } from '../../core/api/models';
import { ApiError } from '../../core/api/api';
import { FieldDef } from './field-defs';

const EMPTY: Record<string, unknown> = { text: '', textarea: '', number: null, bool: false, color: null, date: '', time: '', enum: null,
  lookup: null, lookupMulti: [], ref: null, refMulti: [], tags: [], days: [], weekMask: 0, calendar: [], json: '{}' };

/** Builds a FormGroup for field definitions + custom fields, seeded with an existing value. */
export function buildForm(fields: FieldDef[], customFields: CustomFieldDefinitionDto[], value: Record<string, unknown> | null): FormGroup {
  const group: Record<string, FormControl | FormGroup | FormArray> = {};
  for (const f of fields) {
    const validators: ValidatorFn[] = [];
    if (f.required && !['bool', 'lookupMulti', 'tags', 'days', 'weekMask', 'calendar'].includes(f.type))
      validators.push(f.type === 'refMulti' ? Validators.minLength(1) : Validators.required);
    if (f.min !== undefined) validators.push(Validators.min(f.min));
    if (f.max !== undefined) validators.push(Validators.max(f.max));
    const raw = value?.[f.key] ?? f.default ?? EMPTY[f.type];
    if (f.type === 'calendar') {
      group[f.key] = new FormArray(((raw as Record<string, unknown>[]) ?? []).map((d) => new FormGroup({
        date: new FormControl(d['date'] ?? ''), kind: new FormControl(d['kind'] ?? 'Holiday'),
        nameAr: new FormControl(d['nameAr'] ?? ''), nameEn: new FormControl(d['nameEn'] ?? ''),
      })));
      continue;
    }
    const v = f.type === 'json' && typeof raw !== 'string' ? JSON.stringify(raw ?? {}, null, 2) : raw;
    group[f.key] = new FormControl(Array.isArray(v) ? [...v] : v, validators);
  }
  const cf: Record<string, FormControl> = {};
  const cfValues = (value?.['customFields'] as Record<string, unknown> | undefined) ?? {};
  for (const d of customFields) {
    const empty = d.dataType === 'Boolean' ? false : d.dataType === 'MultiSelect' ? [] : null;
    cf[d.key] = new FormControl(cfValues[d.key] ?? empty, d.required && d.dataType !== 'Boolean' ? [Validators.required] : []);
  }
  group['customFields'] = new FormGroup(cf);
  return new FormGroup(group);
}

/** Converts the form value to the API input shape (numbers, empty strings → null, JSON fields parsed). */
export function formToInput(fields: FieldDef[], form: FormGroup): Record<string, unknown> {
  const raw = form.getRawValue() as Record<string, unknown>;
  const out: Record<string, unknown> = {};
  for (const f of fields) {
    let v = raw[f.key];
    if (f.type === 'number') v = v === '' || v === null || v === undefined ? null : Number(v);
    if (['text', 'textarea', 'date', 'time', 'color'].includes(f.type) && typeof v === 'string') v = v.trim() === '' ? null : v.trim();
    if (f.type === 'json' && typeof v === 'string') { try { v = JSON.parse(v || '{}'); } catch { /* server validates */ } }
    if (f.type === 'calendar') v = (v as Record<string, unknown>[]).filter((d) => d['date']);
    if (f.type === 'enum' && typeof v === 'string' && /^\d+$/.test(v)) v = Number(v);
    out[f.key] = v;
  }
  const cf = raw['customFields'] as Record<string, unknown>;
  out['customFields'] = Object.fromEntries(Object.entries(cf ?? {}).filter(([, v]) => v !== null && v !== '' && !(Array.isArray(v) && v.length === 0)));
  return out;
}

/** Maps API validation errors (field → localized message) for display next to the controls. */
export function serverFieldErrors(err: unknown): Record<string, string> {
  if (!(err instanceof ApiError) || !err.problem.errors) return {};
  return Object.fromEntries(Object.entries(err.problem.errors).map(([k, v]) => [k, v[0]?.message ?? v[0]?.code ?? '']));
}
