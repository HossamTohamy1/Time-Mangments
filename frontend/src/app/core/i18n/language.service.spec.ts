import { TestBed } from '@angular/core/testing';
import { TranslocoService } from '@jsverse/transloco';
import { LanguageService } from './language.service';

describe('LanguageService', () => {
  let service: LanguageService;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [{ provide: TranslocoService, useValue: { setActiveLang: () => undefined } }] });
    service = TestBed.inject(LanguageService);
  });

  it('switches direction with the language', () => {
    service.set('ar', false);
    expect(document.documentElement.dir).toBe('rtl');
    expect(document.documentElement.lang).toBe('ar');
    service.set('en', false);
    expect(document.documentElement.dir).toBe('ltr');
  });

  it('picks the active-language name and falls back to the other one', () => {
    service.set('ar', false);
    expect(service.pick('قاعة', 'Room')).toBe('قاعة');
    expect(service.pick(null, 'Room')).toBe('Room');
    service.set('en', false);
    expect(service.pick('قاعة', '  ')).toBe('قاعة');
  });

  it('formats numbers with Arabic-Indic digits when chosen', () => {
    service.set('ar', false);
    service.setDigits('arabic-indic');
    expect(service.formatNumber(42)).toBe('٤٢');
    service.setDigits('western');
    expect(service.formatNumber(42)).toBe('42');
  });
});
