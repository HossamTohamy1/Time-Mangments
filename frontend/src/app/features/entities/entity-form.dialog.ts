import { DialogRef, DIALOG_DATA } from '@angular/cdk/dialog';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormGroup } from '@angular/forms';
import { TranslocoDirective } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { Api, ApiError } from '../../core/api/api';
import { ConfigStore } from '../../core/config/config.store';
import { EntitySchema } from '../../shared/forms/field-defs';
import { DynamicForm } from '../../shared/forms/dynamic-form';
import { buildForm, formToInput, serverFieldErrors } from '../../shared/forms/form-builder';

export interface EntityFormData {
  schema: EntitySchema;
  value: Record<string, unknown> | null;
  defaults?: Record<string, unknown>;
  /** Optional gate before saving (e.g. impact analysis). Returning false cancels the save. */
  beforeSave?: (body: Record<string, unknown>, id: string | undefined) => Promise<boolean>;
}

@Component({
  selector: 'app-entity-form-dialog',
  imports: [TranslocoDirective, DynamicForm],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <form class="tt-dialog form" (ngSubmit)="save()" *transloco="let t" novalidate>
      <h2>
        {{ data.value ? t('common.editX', { x: singular() }) : t('common.newX', { x: singular() }) }}
      </h2>
      <app-dynamic-form [form]="form" [fields]="data.schema.fields" [customFields]="customFields()" [serverErrors]="fieldErrors()" />
      @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
      <div class="tt-dialog-actions">
        <button type="button" class="tt-btn" (click)="ref.close()">{{ t('common.cancel') }}</button>
        <button type="submit" class="tt-btn primary" [disabled]="busy()" data-testid="entity-save">{{ busy() ? t('common.saving') : t('common.save') }}</button>
      </div>
    </form>
  `,
  styles: [`.form { inline-size: min(760px, calc(100vw - 32px)); } .error { color: var(--tt-conflict); }`],
})
export class EntityFormDialog {
  protected readonly data = inject<EntityFormData>(DIALOG_DATA);
  protected readonly ref = inject<DialogRef<Record<string, unknown>>>(DialogRef);
  private readonly api = inject(Api);
  private readonly config = inject(ConfigStore);

  protected readonly customFields = computed(() => this.data.schema.customFieldEntity ? this.config.customFields(this.data.schema.customFieldEntity) : []);
  protected readonly form: FormGroup = buildForm(this.data.schema.fields, this.customFields(), this.data.value ?? this.data.defaults ?? null);
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly fieldErrors = signal<Record<string, string>>({});

  protected singular(): string {
    const s = this.data.schema.singular;
    return s.startsWith('term:') ? this.config.term(s.slice(5)) : s;
  }

  protected async save(): Promise<void> {
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    this.busy.set(true);
    this.error.set(null);
    this.fieldErrors.set({});
    const body = formToInput(this.data.schema.fields, this.form);
    try {
      const id = this.data.value?.['id'] as string | undefined;
      if (this.data.beforeSave && !(await this.data.beforeSave(body, id))) return;
      const saved = id
        ? await firstValueFrom(this.api.put<Record<string, unknown>>(`${this.data.schema.endpoint}/${id}`, body))
        : await firstValueFrom(this.api.post<Record<string, unknown>>(this.data.schema.endpoint, body));
      this.ref.close(saved);
    } catch (e) {
      this.fieldErrors.set(serverFieldErrors(e));
      this.error.set(e instanceof ApiError ? e.message : String(e));
    } finally {
      this.busy.set(false);
    }
  }
}
