/** Navigation is data: items are shown only when the user has the permission and the feature flag is on. */
export interface NavItem {
  path: string;
  icon: string;
  /** Translation key, or `term:<key>` to render through institution terminology. */
  label: string;
  permission?: string;
  feature?: string;
  badge?: 'conflicts';
  /** Self-service items need the account to be linked to an instructor or a student group. */
  requiresLink?: 'instructor' | 'instructorOrGroup';
}

export interface NavSection { label: string; items: NavItem[]; }

export const NAV: NavSection[] = [
  {
    label: 'nav.sections.operations',
    items: [
      { path: '/dashboard', icon: 'dashboard', label: 'nav.dashboard', permission: 'dashboard.view' },
      { path: '/timetable', icon: 'table', label: 'nav.timetable', permission: 'timetable.view|timetable.edit' },
      { path: '/conflicts', icon: 'alert', label: 'nav.conflicts', permission: 'timetable.view|timetable.edit', badge: 'conflicts' },
      { path: '/generation', icon: 'spark', label: 'nav.generation', permission: 'schedule.generate', feature: 'auto-generation' },
      { path: '/my-timetable', icon: 'calendar', label: 'nav.myTimetable', permission: 'timetable.view.own', requiresLink: 'instructorOrGroup' },
      { path: '/my-availability', icon: 'matrix', label: 'nav.myAvailability', permission: 'availability.manage.own', requiresLink: 'instructor' },
    ],
  },
  {
    label: 'nav.sections.resources',
    items: [
      { path: '/instructors', icon: 'users', label: 'term:instructors', permission: 'resources.view' },
      { path: '/availability', icon: 'matrix', label: 'nav.availability', permission: 'availability.manage' },
      { path: '/rooms', icon: 'door', label: 'nav.rooms', permission: 'resources.view' },
      { path: '/groups', icon: 'cap', label: 'term:groups', permission: 'resources.view' },
      { path: '/courses', icon: 'book', label: 'term:courses', permission: 'resources.view' },
      { path: '/sessions', icon: 'layers', label: 'nav.sessions', permission: 'resources.view' },
      { path: '/substitutions', icon: 'swap', label: 'nav.substitutions', permission: 'substitutions.manage', feature: 'substitutions' },
    ],
  },
  {
    label: 'nav.sections.admin',
    items: [
      { path: '/schedules', icon: 'history', label: 'nav.versions', permission: 'timetable.view|timetable.edit' },
      { path: '/exports', icon: 'arrows', label: 'nav.exports', permission: 'exports.run|imports.run', feature: 'import-export' },
      { path: '/settings', icon: 'gear', label: 'nav.settings', permission: 'config.manage|rules.manage|roles.manage|users.manage' },
    ],
  },
];
