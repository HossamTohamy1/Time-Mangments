import { Pipe, PipeTransform, inject } from '@angular/core';
import { ConfigStore, LookupKind } from '../../core/config/config.store';
import { LanguageService } from '../../core/i18n/language.service';
import { TranslocoService } from '@jsverse/transloco';

/** Name in the active language with fallback to the other language. */
@Pipe({ name: 'localName', pure: false })
export class LocalNamePipe implements PipeTransform {
  private readonly lang = inject(LanguageService);
  transform(v: { nameAr?: string | null; nameEn?: string | null } | null | undefined): string {
    return v ? this.lang.pick(v.nameAr, v.nameEn) : '';
  }
}

/** Institution terminology: override first, then translation key `terms.<key>`. */
@Pipe({ name: 'term', pure: false })
export class TermPipe implements PipeTransform {
  private readonly config = inject(ConfigStore);
  private readonly lang = inject(LanguageService);
  transform(key: string): string {
    this.lang.lang(); // re-evaluate on language switch
    return this.config.term(key);
  }
}

/** Resolves a lookup id to its localized name. */
@Pipe({ name: 'lookupName', pure: false })
export class LookupNamePipe implements PipeTransform {
  private readonly config = inject(ConfigStore);
  private readonly lang = inject(LanguageService);
  transform(id: string | null | undefined, kind: LookupKind): string {
    this.lang.lang();
    return this.config.name(this.config.lookup(kind, id));
  }
}

/** Number in the active locale and digit style. */
@Pipe({ name: 'num', pure: false })
export class NumPipe implements PipeTransform {
  private readonly lang = inject(LanguageService);
  transform(v: number | null | undefined, maxFraction = 1): string {
    return v === null || v === undefined ? '' : this.lang.formatNumber(v, { maximumFractionDigits: maxFraction });
  }
}

/** Localized weekday name (System.DayOfWeek numbering, 0 = Sunday). */
@Pipe({ name: 'dayName', pure: false })
export class DayNamePipe implements PipeTransform {
  private readonly transloco = inject(TranslocoService);
  private readonly lang = inject(LanguageService);
  transform(day: number, short = false): string {
    this.lang.lang();
    return this.transloco.translate(`days.${short ? 'short' : 'long'}.${day}`);
  }
}

/** Localized date (ISO string or Date). */
@Pipe({ name: 'localDate', pure: false })
export class LocalDatePipe implements PipeTransform {
  private readonly lang = inject(LanguageService);
  transform(v: string | Date | null | undefined, style: 'date' | 'datetime' | 'time' = 'date'): string {
    if (!v) return '';
    const opts: Intl.DateTimeFormatOptions = style === 'date' ? { dateStyle: 'medium' } : style === 'time' ? { timeStyle: 'short' } : { dateStyle: 'medium', timeStyle: 'short' };
    return this.lang.formatDate(v, opts);
  }
}

export const SHARED_PIPES = [LocalNamePipe, TermPipe, LookupNamePipe, NumPipe, DayNamePipe, LocalDatePipe] as const;
