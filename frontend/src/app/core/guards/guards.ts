import { inject } from '@angular/core';
import { CanActivateFn, CanDeactivateFn, Router } from '@angular/router';
import { TranslocoService } from '@jsverse/transloco';
import { AuthStore } from '../auth/auth.store';
import { ConfigStore } from '../config/config.store';

export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthStore);
  if (auth.isAuthenticated()) return true;
  return inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
};

/** Requires an institution (otherwise send the user to the setup wizard). */
export const institutionGuard: CanActivateFn = () => {
  const auth = inject(AuthStore);
  return auth.me()?.institutionId ? true : inject(Router).createUrlTree(['/setup']);
};

export function permissionGuard(permission: string): CanActivateFn {
  return () => inject(AuthStore).has(permission) || inject(Router).createUrlTree(['/forbidden']);
}

export function featureGuard(feature: string): CanActivateFn {
  return () => inject(ConfigStore).feature(feature) || inject(Router).createUrlTree(['/forbidden']);
}

export interface HasUnsavedChanges { hasUnsavedChanges(): boolean; }

/** Asks before leaving a page with unsaved edits. */
export const unsavedChangesGuard: CanDeactivateFn<HasUnsavedChanges> = (component) =>
  !component.hasUnsavedChanges() || confirm(inject(TranslocoService).translate('common.unsavedChanges'));
