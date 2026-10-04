# Progress log

## Phase 1 — Scaffold
- Backend solution (Domain/Application/Infrastructure/Api + 3 test projects), central package management, warnings as errors.
- Angular 22 workspace, production build emitted to `backend/src/Timetable.Api/wwwroot`, dev proxy for `/api` and `/hubs` (WebSockets).
- SPA hosting: default/static files with cache headers, fallback to `index.html` that never swallows `/api` or `/hubs`.
- i18n (Transloco, en/ar, RTL/LTR switch at runtime) and theming (tokens, Light/Dark/System, no-flash inline script) infrastructure.
- `build.sh` / `build.ps1`, GitHub Actions CI, i18n parity + hard-coded string check, build verification script.
- Verified: `ng build` → wwwroot → served at `/`, deep link served, `/api/v1/unknown` → 404 JSON.

## Phase 2 — Domain, persistence, templates, auth
- Generic domain model (no business enums), full configuration model (lookups, templates, terminology, constraint settings, rules, custom fields, feature flags, dynamic roles/permissions, substitutions, notifications).
- Constraint engine core in the domain (model, occupancy index, 29 built-in constraints + generic Rule Builder constraint, evaluator with valid-slot search).
- EF Core mappings, named tenant + soft-delete filters, audit + config audit, SQL Server migration `InitialCreate`.
- Five built-in bilingual template bundles (University, Primary, Preparatory, Secondary, Blank), idempotent template applier + config exporter.
- Curriculum → sessions generator (idempotent, previewable).
- Demo seed: Cairo Tech University (UNI) and Al-Nour Secondary School (SEC) + demo users (password `Demo#12345`).
- Auth: Identity + JWT + rotating refresh cookie, permission policies, institution resolution, localized ProblemDetails, rate limiting.
- Tests: architecture tests, auth/hosting integration tests.

## Phase 3 — CRUD, availability, Settings
- API: definition-driven CRUD for buildings, travel times, rooms, instructors, org units, groups, courses, terms (calendar), sessions, curriculum rules, offerings, custom fields, rules, roles; lookups with usage-blocked delete, deactivate and merge; effective config (ETag); terminology, feature flags, time structure, constraint settings; institutions (setup wizard), templates, config export/import with preview, save-as-template; audit log; users; availability (incl. self-service).
- Contract: OpenAPI exported by an integration test into `backend/openapi/v1.json`; `npm run generate:api` generates typed models (89).
- Angular: auth (in-memory access token + refresh cookie, single-flight refresh), institution switching, ConfigStore (ETag, live ConfigChanged), SignalR service, `term`/`localName` pipes, `*hasPermission`/`*ifFeature`, guards, toasts, dialogs; shell (sidebar/header per mockups, RTL mirrored, Light/Dark/System); generic schema-driven entity list + dynamic form (custom fields, server error mapping, usage dialog); availability matrix (drag painting, keyboard); Settings: setup wizard, org tree, lookups manager (color tints + AA warning, merge/deactivate), time structure/shifts/overrides editor, curriculum with preview/apply, constraint tuning, terminology, custom fields, roles matrix, users, feature flags, config transfer, audit; design-review page.
- Test-only E2E host (`backend/tests/Timetable.E2EHost`) runs the real API with a disposable DB for Playwright.
- Tests: 16 API integration tests, 4 architecture tests, 12 Angular unit tests. i18n parity/hard-coded string check passes.
- Deferred to later phases: Rule Builder editor/preview/impact (Phase 4), dashboards KPIs (Phase 8).
