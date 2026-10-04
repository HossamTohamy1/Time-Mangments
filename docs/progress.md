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

## Phase 4 — Constraint engine wiring, validator, impact analysis, Rule Builder
- `ScheduleProblemFactory` builds the engine problem from the database (time grid incl. shifts/overrides, sessions, groups with conflict sets, instructors/rooms availability, external busy time from other schedules, travel times).
- `ConstraintConfigurationProvider` turns institution constraint settings + rules + feature flags into the effective configuration (core always hard, `Off` excluded, prefer → soft).
- `IScheduleValidator`: `ValidateAssignment`, `ValidateSchedule`, `GetValidSlots` (green/amber/red per cell with reasons, best room/instructor), backed by a cached per-schedule state (`ScheduleStateStore`, version-keyed, incremental `Apply`).
- Impact analysis for configuration proposals (constraint setting, rule add/edit/remove, time structure, session type behaviour): new hard violations + affected entries, shown in a confirm dialog before saving.
- Rule preview endpoint (sessions in scope, current violations) — live preview in the Rule Builder dialog.
- Background revalidation queue + worker: configuration/data changes re-validate draft and published schedules and notify users when entries become invalid.
- Angular: Rule Builder (scope / condition / effect, no-code), impact dialog gating on constraints, time structure, session types and rules.
- Tests: 58 domain tests (every built-in constraint hard+soft, rules, configuration, evaluator, validator performance 6000 sessions < 100 ms), 24 API integration tests.

## Phase 5 — Timetable editor, conflicts, versions
- API: schedules list / create / clone / rename / delete / validate / publish (archives the previous published version, freezes a configuration snapshot, notifies linked instructor and student accounts); editor board (`/schedules/{id}/board`); entry commands: assign, move (row-version concurrency), unplace, pin/unpin, swap, auto-place remaining (greedy, never creates hard violations), per-user undo/redo backed by the change log, change history.
- Every edit is validated by the same engine as generation; hard violations are rejected with 422 `MOVE_CONFLICT` and the localized reasons, soft penalties are returned as warnings. Edits update the cached in-memory state after commit and are broadcast over SignalR (`ScheduleChanged` with the delta).
- Migration `ScheduleEntryRoomIndex`: the room/time index is no longer unique (shareable session types may share a room; the validator guards double booking, and swaps no longer trip a transient unique violation).
- Angular: timetable editor per the mockup — days × periods grid by group (incl. parent cohort and sub-groups) / instructor / room, week A/B, CDK drag-and-drop from the unplaced panel and between cells with live green / amber / red cells and reasons, drop preview, keyboard placement (Enter to pick, arrow keys mirrored in RTL, Enter to place, Esc), context menu (pin, swap, find alternative slot, move to unplaced), undo/redo, zoom, session-type filter, validate, publish, read-only banner and "edit a copy" for published schedules, conflict cards from the last validation, live updates from other users; unplaced panel with search, type chips and virtual scrolling; conflicts page grouped by constraint with deep links ("show in grid", "find alternative slot"); schedule versions page.
- Tests: 60 domain (greedy placer added), 29 API integration (assign/conflict reasons, move concurrency + pin, undo/redo/history, swap, auto-place + publish/clone/archive), 17 Angular unit tests (board view filtering).

## Phase 6 — Exports & imports
- Timetable export (`GET /schedules/{id}/export`): PDF (QuestPDF, A4 landscape, one page per group / instructor / room, right-to-left layout and shaped Arabic text with the embedded IBM Plex Sans Arabic font, session-type colours, break rows, page numbers), Excel (ClosedXML: a right-to-left sheet per resource with merged multi-period cells, frozen headers, print setup + a flat "all sessions" table) and CSV (UTF-8 with BOM). Language per export, optional week of the cycle, selected resources or all; terminology overrides are used for headings.
- Imports (`/imports`): buildings, rooms, instructors, courses, groups from .xlsx/.csv; references by code (lookups, buildings, org units, parent groups, rooms, shifts, qualified courses), `cf.<key>` columns for custom fields; upsert (match by code, absent columns keep stored values) or insert-only; dry run executes everything inside a rolled-back transaction and returns a per-row report (create / update / skip / error with localized messages, duplicates within the file); applying is all-or-nothing. Downloadable templates with a bilingual help sheet.
- API honours an explicit `X-Client-Language` header (sent by the SPA) before the saved profile language, so messages always match the UI language.
- Angular: Exports & imports page — schedule, view, resource picker with search, format cards, language, week; import kind/mode, columns, templates, drag-and-drop file, automatic dry run, report table, apply.
- Schedule scores / last-change time are now written with set-based updates (no row-version races between editing and background re-validation).
- Tests: 36 API integration (PDF font embedding, Excel RTL + sheets, CSV BOM/headers, import errors / all-or-nothing / dry run / upsert keeps absent columns / insert-only / templates / unknown columns / Arabic messages).

## Phase 7 — Generation, compare, substitutions
- `ISchedulerEngine` with two engines: OR-Tools CP-SAT (optional fixed-size intervals per occurrence — never infeasible, maximises placed occurrences first; NoOverlap per instructor, room and leaf-to-root group path for every week of the cycle; start / room / instructor domains computed with the shared evaluator on an empty timetable, so availability, external busy time, unary rules and rule-builder restrictions apply generically; soft start penalties, same-day spread tied to the `COURSE_ONCE_PER_DAY` configuration (hard → different days), symmetry breaking, hints from the base version) and a heuristic engine (greedy + local improvement). Every result passes the verifier: full evaluation + repair of anything a hard constraint rejects.
- Generation jobs: queue + background worker (one at a time, recovery after restart), modes "fresh" (keep pinned) / "complete" (keep everything, place the rest), per-run constraint overrides, time limit, cancellation, result written as a new locked draft that is unlocked when done, configuration snapshot, notification to the requester, live progress over SignalR (`/hubs/generation`).
- Readiness checks (no instructor / no feasible slot / no suitable room / overloaded groups and instructors), version compare (moved / added / removed + both scores), substitutions (absence → dated affected sessions → engine-validated, ranked substitutes → per-date exceptions, cancellations with notifications).
- Angular: generation wizard (scope → engine & limits incl. advanced per-run constraint tuning → readiness → live run with progress, score and placed count → open / compare / new run, recent runs), compare page, substitutions page, "compare with published" on versions.
- Measured on the demo university: CP-SAT placed 30/30 occurrences, proven optimal in 4.8 s; tests: 41 API integration (CP-SAT on the school, heuristic on the university, complete mode + compare, substitutions end to end).
