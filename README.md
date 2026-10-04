# TimeTable — configuration-driven school & university timetabling

A bilingual (Arabic RTL / English LTR), multi-institution timetabling system for universities, schools and any other
institution type you can describe in data: session types, instructor types, room types, group kinds, hierarchy levels,
time structure, terminology, rules and feature flags are all configuration — not code.

- **Backend**: ASP.NET Core (.NET 10), Clean Architecture (Domain / Application / Infrastructure / Api), EF Core + SQL Server,
  MediatR, FluentValidation, JWT + rotating refresh cookie, SignalR, OR-Tools CP-SAT, QuestPDF, ClosedXML.
- **Frontend**: Angular 22 (standalone, signals, zoneless, OnPush), Angular CDK (drag & drop, menus, overlays, virtual scroll),
  Transloco with an eval-free ICU transpiler, design tokens with Light / Dark / System themes.
- **One deployable**: the Angular production build is emitted into `backend/src/Timetable.Api/wwwroot` and served by the API
  (no Docker, no separate web server).

## Features

| Area | What you get |
| --- | --- |
| Setup | Institution setup wizard from templates (University, Primary, Preparatory, Secondary, Blank), save-as-template, config export/import with preview |
| Configuration | Lookups with colours and merge/deactivate, terminology overrides per language, time structure (periods, breaks, shifts, day overrides, A/B week cycles), feature flags, custom fields, dynamic roles & permissions, audit log |
| Constraint engine | 29 built-in constraints (hard / soft / off, weights, parameters), no-code **Rule Builder** with live preview, impact analysis before saving any risky change, background re-validation |
| Manual editing | Timetable grid per group / instructor / room, drag & drop and keyboard placement with live green / amber / red feasibility and reasons, pin, swap, alternative slot, undo / redo, conflicts page, versions, compare, publish |
| Generation | OR-Tools CP-SAT engine with heuristic fallback, readiness checks, per-run constraint overrides, live progress over SignalR, safety-net verification of every result |
| Operations | Substitutions (absence → affected dated sessions → ranked substitutes), notifications, dashboard KPIs |
| Self-service | "My timetable" for instructors and students (mobile-first), personal PDF, availability self-service |
| Data exchange | PDF (Arabic RTL with embedded font) / Excel / CSV exports; Excel / CSV imports with dry-run report |

## Quick start (development)

Prerequisites: .NET SDK 10, Node.js ≥ 22.22.3 (or 24), SQL Server (LocalDB, Developer or Express).

```bash
# 1. API (applies migrations and seeds the demo institutions in Development)
cd backend/src/Timetable.Api
dotnet run                     # http://localhost:5080  (Swagger: /api/swagger)

# 2. Angular dev server with proxy to the API (/api and /hubs, WebSockets included)
cd frontend
npm ci
npm start                      # http://localhost:4200
```

`appsettings.Development.json` points to `(localdb)\MSSQLLocalDB`; override with `ConnectionStrings__Default`.

**Demo accounts** (password `Demo#12345`): `admin@demo.local` (administrator of both demo institutions),
`scheduler@demo.local`, `dr.ahmed@demo.local` (instructor, self-service), `student@demo.local` (student, self-service).
Demo institutions: *Cairo Tech University* (UNI) and *Al-Nour Secondary School* (SEC).

## Build & deploy

```bash
./build.sh            # Linux/macOS  (./build.ps1 on Windows)
```

Installs, lints, tests and builds the client into `wwwroot`, builds and tests the server, and publishes a single app to
`artifacts/publish`. `dotnet publish` alone also builds the client (MSBuild target; skip with `-p:SkipClientBuild=true`).

Run the published app:

```bash
cd artifacts/publish
export ConnectionStrings__Default="Server=...;Database=Timetable;User Id=...;Password=...;TrustServerCertificate=True"
export Jwt__SigningKey="<at least 32 random characters>"
export Database__ApplyMigrationsOnStartup=true      # or apply migrations in your release pipeline
dotnet Timetable.Api.dll --urls http://0.0.0.0:8080
```

Host behind IIS, a systemd service + reverse proxy (nginx / Caddy), or Azure App Service. The app serves the SPA with
long-lived caching for hashed assets, `no-cache` for `index.html`, deep links fall back to the SPA, and unknown `/api` or
`/hubs` routes return JSON 404s.

### Configuration

| Key (env var form) | Purpose |
| --- | --- |
| `ConnectionStrings__Default` | SQL Server connection string (required) |
| `Jwt__SigningKey` | HMAC key for access tokens, ≥ 32 chars (required outside Development) |
| `Jwt__AccessTokenMinutes`, `Jwt__RefreshTokenDays` | Token lifetimes (30 min / 14 days) |
| `Database__ApplyMigrationsOnStartup` | Apply EF Core migrations at start-up |
| `Database__SeedDemoData` | Seed the two demo institutions and demo users |
| `Solver__DefaultWorkers` | CP-SAT threads (0 = all processors) |
| `Solver__HeuristicThresholdSessions` | In "auto" mode, use the heuristic above this many occurrences (0 = never) |

Migrations: `dotnet ef database update -p src/Timetable.Infrastructure -s src/Timetable.Api` (tool manifest in `backend/`).

## Tests

```bash
cd backend && dotnet test                       # domain, architecture and API integration tests
cd frontend && npm run lint && npm test         # lint, i18n key parity / hard-coded string check, unit tests
cd frontend && npm run build:prod && npm run e2e   # Playwright: en/ar × light/dark against the real API
```

Integration tests use a disposable SQL Server database when `TIMETABLE_TEST_SQLSERVER` is set (CI does this),
otherwise a temporary SQLite file. The Playwright suite starts a test-only host (`backend/tests/Timetable.E2EHost`) with a
fresh demo database.

`AcceptanceLanguageCenterTests` is the end-to-end proof that nothing is hard-coded: an unseen institution type (a language
training centre) is configured purely through the API and generated, validated, exported in Arabic and imported into.
See [docs/dynamic-proof.md](docs/dynamic-proof.md).

## Repository layout

```
backend/
  src/Timetable.Domain           entities, constraint engine (pure, no I/O)
  src/Timetable.Application      use cases (MediatR), validation, CRUD framework, scheduling services, engines' contract
  src/Timetable.Infrastructure   EF Core, Identity, SignalR, CP-SAT, QuestPDF/ClosedXML, templates, fonts, localization
  src/Timetable.Api              controllers, auth, hosting (SPA, security headers, rate limiting, ProblemDetails)
  tests/                         Domain.Tests, Application.Tests (architecture), Api.IntegrationTests, E2EHost
frontend/
  src/app/core                   API client, auth, config store, realtime, i18n, theme
  src/app/features               timetable editor, conflicts, generation, substitutions, settings, self-service, ...
  scripts/i18n                   source of truth for translations (en/ar key parity enforced)
  e2e/                           Playwright specs
docs/                            decisions, progress, design tokens, dynamic proof
```

## Documentation

- [docs/decisions.md](docs/decisions.md) — architecture and design decisions, including fixes found during verification
- [docs/progress.md](docs/progress.md) — what each phase delivered
- [docs/design-tokens.md](docs/design-tokens.md) — colour, typography, spacing and component tokens (light / dark)
- [docs/dynamic-proof.md](docs/dynamic-proof.md) — how every business concept is data, with the acceptance test walkthrough

## Licences

Application code: see repository licence. Bundled font IBM Plex Sans Arabic: SIL Open Font License 1.1
(`backend/src/Timetable.Infrastructure/Fonts/OFL-IBMPlexSansArabic.txt`). QuestPDF is used under its Community licence.
