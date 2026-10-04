import { Routes } from '@angular/router';
import { featureGuard, permissionGuard } from '../../core/guards/guards';
import { ENTITY_SCHEMAS } from '../entities/entity-schemas';

export const SETTINGS_ROUTES: Routes = [
  { path: '', loadComponent: () => import('./settings-home.page').then((m) => m.SettingsHomePage) },
  { path: 'lookups', canActivate: [permissionGuard('config.manage')], loadComponent: () => import('./lookups.page').then((m) => m.LookupsPage) },
  { path: 'org-structure', canActivate: [permissionGuard('config.manage')], loadComponent: () => import('./org-structure.page').then((m) => m.OrgStructurePage) },
  { path: 'time', canActivate: [permissionGuard('config.manage')], loadComponent: () => import('./time-structure.page').then((m) => m.TimeStructurePage) },
  { path: 'curriculum', canActivate: [permissionGuard('config.manage'), featureGuard('curriculum')], loadComponent: () => import('./curriculum.page').then((m) => m.CurriculumPage) },
  { path: 'constraints', canActivate: [permissionGuard('rules.manage')], loadComponent: () => import('./constraints.page').then((m) => m.ConstraintsPage) },
  { path: 'terminology', canActivate: [permissionGuard('config.manage')], loadComponent: () => import('./terminology.page').then((m) => m.TerminologyPage) },
  {
    path: 'custom-fields', canActivate: [permissionGuard('config.manage'), featureGuard('custom-fields')],
    loadComponent: () => import('../entities/entity-list.page').then((m) => m.EntityListPage), data: { schema: ENTITY_SCHEMAS['custom-fields'] },
  },
  { path: 'roles', canActivate: [permissionGuard('roles.manage')], loadComponent: () => import('./roles.page').then((m) => m.RolesPage) },
  { path: 'users', canActivate: [permissionGuard('users.manage')], loadComponent: () => import('./users.page').then((m) => m.UsersPage) },
  { path: 'features', canActivate: [permissionGuard('config.manage')], loadComponent: () => import('./features.page').then((m) => m.FeaturesPage) },
  { path: 'transfer', canActivate: [permissionGuard('config.manage')], loadComponent: () => import('./transfer.page').then((m) => m.TransferPage) },
  { path: 'audit', canActivate: [permissionGuard('audit.view')], loadComponent: () => import('./audit.page').then((m) => m.AuditPage) },
];
