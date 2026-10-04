import { Routes } from '@angular/router';
import { provideTranslocoScope } from '@jsverse/transloco';
import { authGuard, featureGuard, institutionGuard, permissionGuard } from './core/guards/guards';
import { ENTITY_SCHEMAS } from './features/entities/entity-schemas';

const entity = (key: string, permission = 'resources.view') => ({
  path: key,
  canActivate: [permissionGuard(permission), ...(ENTITY_SCHEMAS[key].feature ? [featureGuard(ENTITY_SCHEMAS[key].feature!)] : [])],
  loadComponent: () => import('./features/entities/entity-list.page').then((m) => m.EntityListPage),
  data: { schema: ENTITY_SCHEMAS[key] },
});

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./features/auth/login.page').then((m) => m.LoginPage) },
  {
    path: 'setup',
    canActivate: [authGuard],
    providers: [provideTranslocoScope('settings')],
    loadComponent: () => import('./features/settings/setup-wizard.page').then((m) => m.SetupWizardPage),
  },
  {
    path: '',
    canActivate: [authGuard, institutionGuard],
    loadComponent: () => import('./layout/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
      { path: 'dashboard', loadComponent: () => import('./features/dashboard/dashboard.page').then((m) => m.DashboardPage), providers: [provideTranslocoScope('dashboard')] },
      entity('rooms'), entity('buildings'), entity('instructors'), entity('courses'), entity('groups'), entity('sessions'),
      entity('terms'), entity('travel-times'), entity('offerings'),
      {
        path: 'availability', canActivate: [permissionGuard('availability.manage')],
        loadComponent: () => import('./features/availability/availability.page').then((m) => m.AvailabilityPage),
      },
      {
        path: 'my-availability', canActivate: [permissionGuard('availability.manage.own')],
        loadComponent: () => import('./features/availability/availability.page').then((m) => m.AvailabilityPage), data: { self: true },
      },
      {
        path: 'settings',
        providers: [provideTranslocoScope('settings')],
        loadChildren: () => import('./features/settings/settings.routes').then((m) => m.SETTINGS_ROUTES),
      },
      { path: 'profile', loadComponent: () => import('./features/misc/profile.page').then((m) => m.ProfilePage) },
      { path: 'design-review', loadComponent: () => import('./features/misc/design-review.page').then((m) => m.DesignReviewPage) },
      { path: 'forbidden', loadComponent: () => import('./features/misc/forbidden.page').then((m) => m.ForbiddenPage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
