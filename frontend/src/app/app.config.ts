import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig, inject, isDevMode, provideAppInitializer, provideBrowserGlobalErrorListeners,
  provideZonelessChangeDetection,
} from '@angular/core';
import { provideRouter, withComponentInputBinding, withInMemoryScrolling } from '@angular/router';
import { provideTransloco, TRANSLOCO_TRANSPILER } from '@jsverse/transloco';
import { IcuTranspiler } from './core/i18n/icu-transpiler';
import { firstValueFrom } from 'rxjs';
import { TranslocoService } from '@jsverse/transloco';
import { routes } from './app.routes';
import { TranslocoHttpLoader } from './core/i18n/transloco-loader';
import { LanguageService } from './core/i18n/language.service';
import { ThemeService } from './core/theme/theme.service';
import { appInterceptors } from './core/http/interceptors';
import { appBootstrap } from './core/bootstrap';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    provideRouter(routes, withComponentInputBinding(), withInMemoryScrolling({ scrollPositionRestoration: 'top' })),
    provideHttpClient(withFetch(), withInterceptors(appInterceptors)),
    provideTransloco({
      config: {
        availableLangs: ['en', 'ar'],
        defaultLang: 'en',
        fallbackLang: 'en',
        reRenderOnLangChange: true,
        prodMode: !isDevMode(),
        missingHandler: { useFallbackTranslation: true, logMissingKey: isDevMode() },
      },
      loader: TranslocoHttpLoader,
    }),
    { provide: TRANSLOCO_TRANSPILER, useClass: IcuTranspiler },
    provideAppInitializer(async () => {
      const lang = inject(LanguageService);
      const theme = inject(ThemeService);
      const transloco = inject(TranslocoService);
      const restoreSession = appBootstrap();
      theme.init();
      lang.init();
      await firstValueFrom(transloco.load(lang.lang()));
      await restoreSession();
    }),
  ],
};
