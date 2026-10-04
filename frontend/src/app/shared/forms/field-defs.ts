import type { CustomFieldEntity } from '../../core/api/models';
import type { LookupKind } from '../../core/config/config.store';

export type FieldType =
  | 'text' | 'textarea' | 'number' | 'bool' | 'color' | 'date' | 'time' | 'enum'
  | 'lookup' | 'lookupMulti' | 'ref' | 'refMulti' | 'tags' | 'days' | 'weekMask' | 'calendar' | 'json';

export interface FieldDef {
  key: string;
  /** Translation key, or `term:<key>` for institution terminology. */
  label: string;
  type: FieldType;
  lookup?: LookupKind;
  /** For lookups: bind the lookup's id (default) or its stable code. */
  valueBy?: 'id' | 'code';
  /** Reference list endpoint (e.g. '/rooms'), labelled by code + localized name. */
  ref?: string;
  /** Extra query for the reference list (e.g. { termId }). */
  refQuery?: Record<string, string>;
  options?: { value: string | number; label: string }[];
  /** Dynamic option source from the effective configuration. */
  optionsSource?: 'shifts' | 'periods';
  required?: boolean;
  min?: number;
  max?: number;
  step?: number;
  feature?: string;
  span?: 1 | 2;
  hint?: string;
  dir?: 'ltr' | 'rtl' | 'auto';
  default?: unknown;
}

export interface ColumnDef {
  key: string;
  label: string;
  kind?: 'text' | 'name' | 'lookup' | 'lookupCodes' | 'ref' | 'refMulti' | 'number' | 'tags' | 'bool' | 'color' | 'code' | 'enum';
  lookup?: LookupKind;
  ref?: string;
  enumPrefix?: string;
}

export interface EntitySchema {
  key: string;
  endpoint: string;
  title: string;
  singular: string;
  icon: string;
  managePermission: string;
  feature?: string;
  customFieldEntity?: CustomFieldEntity;
  fields: FieldDef[];
  columns: ColumnDef[];
  filters?: FieldDef[];
  defaultSort?: string;
  /** Related screens shown as tabs (e.g. Rooms · Buildings · Travel times). */
  links?: { path: string; label: string; feature?: string }[];
}
