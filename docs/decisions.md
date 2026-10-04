# Decision log

Every non-obvious choice, every flow gap found and fixed, and every feature added because a flow needed it.
Format: **what** — why — which flow it affects.

## Platform & tooling

- **.NET 10 (LTS) + EF Core 10, Angular 22 (standalone, zoneless, signals)** — latest LTS / stable at build time.
- **Node 24 LTS required for the frontend** — Angular CLI 22 requires Node ≥ 22.22.3 / 24.15. `package.json` declares `engines`.
- **MediatR 12.5 (Apache-2.0)** — MediatR 13+ requires a commercial licence key; 12.5 has everything needed (pipeline behaviours).
- **Shouldly instead of FluentAssertions** — FluentAssertions 8 is commercially licensed.
- **QuestPDF Community licence** — free for organisations under the revenue threshold; switch `QuestPDF.Settings.License` if that does not apply.
- **No Hangfire** — generation jobs use a `BackgroundService` + `Channel<T>` queue (no extra storage, no extra licence; jobs are persisted in `GenerationJobs`).
- **Angular Material (M3) over PrimeNG** — CDK is required anyway for drag-drop/virtual scroll; Material 3 theming is driven by CSS custom properties, which lets us wire it to our own design tokens for light/dark with no duplicated palettes.
- **Transloco + messageformat plugin** — runtime language switch without reload, lazy per-feature scopes, ICU plurals (Arabic has six plural categories).
- **ng-openapi-gen** for typed API clients (`npm run generate:api`, input `backend/openapi/v1.json`, exported by the API in Development at `/api/swagger/v1/swagger.json`).
- **Swagger UI at `/api/swagger`** (Development only) — keeps every API URL under `/api`, so the SPA fallback rules stay trivial.

## Hosting / packaging

- **Angular `deleteOutputPath` wipes `wwwroot`** — `npm run build:prod` re-creates `wwwroot/.gitkeep` (scripts/postbuild.mjs). Uploads/exports are written to `App_Data/` (config `Storage:Root`), never to `wwwroot`.
- **Unknown `/api/*` and `/hubs/*` → RFC 7807 404 JSON**, mapped as explicit catch-all routes so `MapFallbackToFile("index.html")` can never swallow them.
- **Cache headers** — `index.html` no-cache; hashed bundles (`name-HASH.ext`) `public, max-age=31536000, immutable`; other static files 1h.
- **MSBuild `BuildAngularClient` target** runs `npm ci && npm run build:prod` before publish; skip with `-p:SkipClientBuild=true` (used by build.sh after it has already built and tested the client).

## Data & persistence

- **SQL Server is the only production database.** The sandbox used to build this repo cannot install SQL Server (package host blocked), so integration tests use a dedicated SQL Server database when `TIMETABLE_TEST_SQLSERVER` is set (created + dropped per run) and otherwise fall back to in-memory SQLite. Provider-specific bits are isolated in `AppDbContext.OnModelCreating` (row version: SQL Server `rowversion` vs app-generated token; DateTimeOffset stored as ticks on SQLite). Migrations are SQL Server migrations; `Database:ApplyMigrationsOnStartup` applies them in Development.
- **GUID v7 keys** — sortable, generated client-side, no round trip.
- **Primitive collections (JSON columns)** for tags, equipment codes, qualified course ids, session group ids, role permissions — small, read together with the owner, no join tables needed.
- **Named EF query filters (EF 10)**: `Tenant` (institution scoping) and `SoftDelete`, combined. System operations call `ITenantContext.Bypass()`.
- **Audit**: `AuditableEntity` fields are set in `SaveChangesAsync`; configuration entities additionally write `ConfigAuditEntries` (before/after JSON) regardless of entry point (UI, import, template apply).
- **Config change propagation**: saving any configuration row bumps the institution's `ConfigVersion` (cache keys, ETag), invalidates permission caches and broadcasts `ConfigChanged` over SignalR.
- **Week cycles are bit masks** (`0` = every week, bit *i* = week *i* of an N-week rotation), so overlap checks are a single AND.

## Domain / dynamic design

- **No business enums.** Session types, instructor types, room types, group kinds, org-unit levels, equipment are lookup rows with stable `Code`s. An architecture test fails if an enum outside the technical allow-list is added or if enum members look like business concepts.
- **Constraint severity is configuration, not code.** Constraints only report an "amount" of badness; the configured instance decides whether it is a hard violation or a weighted soft penalty. Any built-in can therefore be switched Hard ↔ Soft ↔ Off (except the `IsCore` ones).
- **Incremental evaluation** — aggregate constraints compute candidate deltas (with − without) over small buckets (group/day, instructor/week, …) using an in-memory occupancy index.
- **All slot numbers in configuration and UI are 1-based**; the engine uses 0-based slot indexes internally.
- **Session ordering default is Soft in the University template** (Admin can switch it to Hard); keeps generation feasible on partially entered data.

## Security

- **Permissions, not roles, are checked** (`[HasPermission]` policies + `AuthorizationBehavior` for MediatR requests). Roles are data per institution. `X-Institution-Id` selects the institution and is validated against the caller's memberships.
- **Refresh tokens**: random 512-bit, stored as SHA-256 hash, rotated on every refresh, HttpOnly + Secure + SameSite=Strict cookie scoped to `/api/v1/auth`; re-use of a rotated token revokes the user's whole token family.
- **Org-unit scoped role assignments** are stored and returned but not yet used to filter editable data (listed as deferred in progress.md).

## Phase 3 — CRUD & settings

- **Definition-driven generic CRUD** (`CrudDefinition<TEntity,TDto,TInput>` + closed generic MediatR handlers) — one consistent pipeline for paging, search across both names, sorting, filters, FluentValidation, reference checks inside the institution, custom-field validation and usage-blocked deletes for ~20 entity types.
- **Deletes never orphan data**: definitions list usages; a delete with usages returns 409 `IN_USE` (or `SYSTEM_LOOKUP_IN_USE`) with the usage list. Lookups offer *deactivate* (keep for history) or *merge into another value* (re-points id and code references, then removes the source).
- **Fix (flow: seeding / curriculum)**: the curriculum generator ran with tenant filters bypassed during seeding and picked up rules of other institutions. All its queries are now explicitly scoped by institution id (defence in depth, independent of query filters).
- **Session edits only adjust draft schedules** (occurrence count / duration); published schedules are re-validated and surface conflicts instead of being silently changed.
- **Effective config ETag** = hash of the cached institution configuration + hash of the caller's permissions; 304 on `If-None-Match`.
- **First-run institution creation**: an authenticated user with no memberships may create an institution (becomes its ADMIN); afterwards `institutions.manage` is required.
- **Fix (flow: E2E hosting)**: controllers are registered as an explicit application part, so the API works whatever the entry assembly is (test host, publish).
- **Fix (flow: app bootstrap)**: `inject()` after `await` in the app initializer threw NG0203; dependencies are now resolved synchronously first.
- **Test/E2E SQLite fallback uses a temporary file database** (one connection per DbContext) — a shared in-memory connection is not safe under concurrent requests.
- **Session hint flag** (`tt.session` in localStorage, no secret) avoids a refresh call (and a 400 in the console) for anonymous visitors.
- **Self-service menu items** (My timetable / My availability) are shown only when the account is linked to an instructor or group.
- **Permission codes contain dots**; their translation keys replace dots with underscores to avoid nested-key collisions (`timetable.view` vs `timetable.view.own`).
- **Language endonyms** ("English", "العربية") are deliberately not translated in language pickers; the i18n checker allow-lists them.

## Phase 4 — Engine & validator

- **Severity is configuration, not code**: constraints only report violations with an amount; the configuration decides hard/soft and weight (soft penalty = weight × amount). Core constraints are always hard.
- **Candidate evaluation is incremental**: unary constraints evaluate only the candidate; resource conflicts use the occupancy index; aggregate constraints compute the delta (with − without), so a drag preview stays well under 100 ms for thousands of sessions.
- **Cached schedule state keyed by (config version, data version, schedule version)**; any configuration change bumps the version and the next request rebuilds.
- **Impact before save**: risky configuration changes are evaluated against existing schedules first (dry run), and the user confirms with the list of affected entries.
- **Fix (flow: conflicts)**: a group/parent-group clash was reported twice (once per direction); entity refs are now symmetric and de-duplicated.
- **Week patterns are bitmasks** (`0` = every week), so Week A/B and custom rotations need no special code.

## Phase 5 — Editor

- **Entry edits bypass the generic transaction behaviour**: each edit runs under the schedule's in-memory lock and saves entries + change log in a single `SaveChanges`; the cached index is updated only after the commit succeeds, so cache and database cannot diverge.
- **Undo/redo is per user and conflict-checked**: a change is reverted only if the entries it touched are still exactly as that change left them; otherwise `UNDO_CONFLICT` (someone else edited them). A new edit clears the user's redo stack.
- **Hard violations always block manual edits** (422 with reasons); soft penalties are allowed and reported. Published schedules are read-only; the way to change one is "edit a copy" (clone → draft → publish, which archives the previous version).
- **Auto-place is the greedy placer** (most-constrained first, cheapest valid cell, ties spread over days and balance group load). It doubles as the generation heuristic fallback.
- **Group view = group + ancestors + descendants**, so a section sees its cohort's shared lectures and its lab groups' sessions.
- **Fix (flow: swap)**: a unique room/time index made two-row swaps fail transiently; it was also wrong for shareable rooms. Replaced by a non-unique index (migration).
- **Fix (flow: editor layout)**: grid columns sized to `max-content` grew to the card text width; columns are now `minmax(col-min × factor, factor fr)` with a per-day factor for parallel sessions, and narrow cards switch to a compact layout via container queries.

## Phase 6 — Exports & imports

- **Renderer-agnostic document model**: the Application layer resolves language, terminology, day names, colours and merges overlapping cells into blocks; Infrastructure only lays out (QuestPDF / ClosedXML). The same model can later feed other formats.
- **Arabic font**: IBM Plex Sans Arabic (SIL OFL 1.1, licence file shipped next to the font) — the npm package only ships WOFF, so the TTFs were produced by unpacking the WOFF tables (lossless). Embedded as resources; system fonts are disabled so output is identical on every server.
- **QuestPDF Community licence** is configured in code; organisations above the Community revenue threshold must switch the licence setting.
- **Imports reuse the CRUD commands** (same validation, permission and custom-field rules as the UI). Dry run = real execution in a transaction that is rolled back, so the preview is exact (including rows that depend on earlier rows, e.g. a section whose parent cohort is in the same file).
- **Fix (flow: background re-validation)**: score updates on the schedule row collided with concurrent edits (row version). Derived fields now use `ExecuteUpdate`.
- **Fix (flow: language)**: messages followed the saved profile language even when the user switched the UI language; the SPA now sends `X-Client-Language`, which takes precedence.

## Phase 7 — Generation

- **Model never infeasible**: each occurrence has a presence literal; not placing it costs 100 000 × duration in the objective. The solver always returns the best partial timetable and the UI explains what is left instead of failing.
- **Constraint semantics stay in one place**: CP-SAT encodes only resource exclusivity and time/room/instructor domains; the domains are derived by evaluating every configured constraint (including rules) on an empty timetable. Aggregate constraints are enforced by the verifier + repair pass that runs after every engine, so engines can never publish an invalid placement.
- **Group conflicts as leaf-to-root paths**: one NoOverlap per path lets sibling sub-groups (lab groups) run in parallel while sharing their parents' lectures.
- **Auto engine** = CP-SAT when the native library loads, with the heuristic as fallback when CP-SAT finds nothing within the limit.
- **One job at a time per institution**; the result is a new draft locked with `LockedByJobId` until the job finishes (manual edits are refused meanwhile). Jobs interrupted by a restart are marked failed and their partial result removed.
- **Substitutions use the same evaluator**: a candidate is eligible when moving the entry to them breaks no hard rule (the session's instructor pool is ignored for one-off cover); ranking = qualified first, then soft penalty, then current load.

## Phase 8 — Hardening & verification

- **Strict CSP without `unsafe-eval`**: Transloco's messageformat plugin compiles ICU messages with `new Function`, which a strict policy blocks (the app rendered without text). It was replaced by a small eval-free ICU transpiler (`core/i18n/icu-transpiler.ts`: arguments, plural/selectordinal via `Intl.PluralRules` — all six Arabic categories — select, `#`, `=n`, offsets, escapes), covered by unit tests. Inline scripts in `index.html` are allowed by SHA-256 hashes computed from the deployed file at start-up.
- **Account language wins after sign-in** (it follows the user across devices); the header toggle updates it. Before sign-in the device preference applies.
- **Fix (flow: editor drag & drop)**, found by the Playwright suite: mouse drops never placed anything. CDK emits `ended` before `dropped` (the drop follows the return animation) and the pick was cleared on `ended`; in addition, the "placing…" banner pushed the grid down after CDK had cached the drop-list rectangles, so no cell was ever hit. Drop handlers now own the cleanup and the banner floats over the page.
- **Fix (flow: fresh institution)**, found by the acceptance test: saving the time structure of a new institution failed with a concurrency error. Entity ids are client-generated GUIDs but EF treated them as store-generated, so children added through navigations were UPDATEd instead of INSERTed. Ids are now `ValueGeneratedNever` model-wide (schema-neutral migration `ClientGeneratedKeys`).
- **Fix (flow: generation on small machines)**, found in CI: with CP-SAT's parallel search the verifier could fail to attribute a violation to a placement and returned a timetable with hard findings. It now falls back to any placement of the involved sessions and, as a last resort, unplaces offenders — an unplaced occurrence is always preferred to an invalid timetable. A wall-clock guard stops CP-SAT if the native time limit is not honoured.
- **CI uses real SQL Server** (apt install, Developer edition) so migrations and provider-specific behaviour are exercised; local runs keep the SQLite fallback for speed.
- **Demo terms are relative to the seeding date**, so self-service and dashboards always show a "current" week.
- **Fix (flow: consecutive generations)**, found by Playwright and CI: a job was signalled to the worker inside the request transaction, i.e. before commit; the worker could read the database before the job was visible and drop it, leaving it "Queued" forever (and every later start was refused as "already running"). The database is now the queue: the channel is only a wake-up signal and the worker re-reads queued jobs on every signal and every 3 seconds.
