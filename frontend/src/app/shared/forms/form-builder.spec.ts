import { buildForm, formToInput, serverFieldErrors } from './form-builder';
import { FieldDef } from './field-defs';
import { ApiError } from '../../core/api/api';

const fields: FieldDef[] = [
  { key: 'code', label: 'fields.code', type: 'text', required: true },
  { key: 'capacity', label: 'fields.capacity', type: 'number', min: 0 },
  { key: 'tags', label: 'fields.tags', type: 'tags' },
  { key: 'notes', label: 'fields.notes', type: 'textarea' },
];

describe('dynamic form builder', () => {
  it('builds controls with validators and defaults', () => {
    const form = buildForm(fields, [], null);
    expect(form.get('code')!.invalid).toBe(true);
    form.get('code')!.setValue('R1');
    form.get('capacity')!.setValue(-1);
    expect(form.get('capacity')!.invalid).toBe(true);
  });

  it('maps form values to the API input shape', () => {
    const form = buildForm(fields, [], { code: ' R1 ', capacity: '30', tags: ['a'], notes: '  ' });
    const input = formToInput(fields, form);
    expect(input['code']).toBe('R1');
    expect(input['capacity']).toBe(30);
    expect(input['tags']).toEqual(['a']);
    expect(input['notes']).toBeNull();
    expect(input['customFields']).toEqual({});
  });

  it('extracts localized server field errors', () => {
    const err = new ApiError({ status: 400, code: 'VALIDATION_FAILED', message: 'x', errors: { capacity: [{ code: 'VALUE_OUT_OF_RANGE', message: 'Out of range' }] } });
    expect(serverFieldErrors(err)).toEqual({ capacity: 'Out of range' });
  });
});
