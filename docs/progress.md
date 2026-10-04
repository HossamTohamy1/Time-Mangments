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
