import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TranslocoService } from '@jsverse/transloco';
import { ConfigStore } from './config.store';
import { LanguageService } from '../i18n/language.service';
import type { EffectiveConfigDto } from '../api/models';

const config = {
  institution: { id: 'i', code: 'UNI', defaultLanguage: 'ar', timeZone: 'Africa/Cairo' },
  version: 'v1',
  lookups: { 'session-types': [{ id: 's1', code: 'LECTURE', nameEn: 'Lecture', nameAr: 'محاضرة', isActive: true, isSystem: true, sortOrder: 1 },
    { id: 's2', code: 'OLD', nameEn: 'Old', isActive: false, isSystem: false, sortOrder: 2 }] },
  terminology: { en: { 'term.group': 'Section' }, ar: {} },
  features: { 'week-cycles': true, shifts: false },
  time: { workingDays: [4, 0, 1, 2, 3], weekStartDay: 0, weekCycleLength: 2, weekCycleLabels: [], periods: [], shifts: [], dayOverrides: [] },
  customFields: [], constraints: [], permissions: [],
} as unknown as EffectiveConfigDto;

describe('ConfigStore', () => {
  let store: ConfigStore;
  let http: HttpTestingController;
  let lang: LanguageService;

  beforeEach(async () => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(),
        { provide: TranslocoService, useValue: { setActiveLang: () => undefined, translate: (k: string) => `T(${k})` } }],
    });
    store = TestBed.inject(ConfigStore);
    http = TestBed.inject(HttpTestingController);
    lang = TestBed.inject(LanguageService);
    const p = store.load();
    http.expectOne('/api/v1/config/effective').flush(config, { headers: { ETag: '"v1"' } });
    await p;
  });

  it('resolves terminology overrides before translation keys', () => {
    lang.set('en', false);
    expect(store.term('group')).toBe('Section');
    lang.set('ar', false);
    expect(store.term('group')).toBe('T(terms.group)');
  });

  it('filters inactive lookups and orders working days from the week start', () => {
    expect(store.lookups('session-types').map((l) => l.code)).toEqual(['LECTURE']);
    expect(store.lookups('session-types', false).length).toBe(2);
    expect(store.workingDays()).toEqual([0, 1, 2, 3, 4]);
    expect(store.weekLabels()).toEqual(['1', '2']);
    expect(store.feature('week-cycles')).toBe(true);
    expect(store.feature('shifts')).toBe(false);
  });

  it('sends If-None-Match on reload', async () => {
    const p = store.load();
    const req = http.expectOne('/api/v1/config/effective');
    expect(req.request.headers.get('If-None-Match')).toBe('"v1"');
    req.flush(null, { status: 304, statusText: 'Not Modified' });
    await p;
    expect(store.config()?.version).toBe('v1');
  });
});
