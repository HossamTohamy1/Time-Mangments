#!/usr/bin/env bash
# Builds the Angular client into the API's wwwroot, tests both projects and publishes a single deployable app.
# Usage: ./build.sh [--skip-tests] [--e2e] [--output <dir>]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
OUT="$ROOT/artifacts/publish"
SKIP_TESTS=false
RUN_E2E=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-tests) SKIP_TESTS=true; shift ;;
    --e2e) RUN_E2E=true; shift ;;
    --output) OUT="$2"; shift 2 ;;
    *) echo "Unknown option $1"; exit 1 ;;
  esac
done

echo "==> Frontend: install + lint + test + production build (-> backend wwwroot)"
pushd "$ROOT/frontend" >/dev/null
npm ci
npm run check:i18n
if [[ "$SKIP_TESTS" != true ]]; then npm test; fi
npm run build:prod
popd >/dev/null

echo "==> Backend: restore + build + test"
pushd "$ROOT/backend" >/dev/null
dotnet restore Timetable.sln
dotnet build Timetable.sln -c Release --no-restore
if [[ "$SKIP_TESTS" != true ]]; then dotnet test Timetable.sln -c Release --no-build; fi

echo "==> Publish single deployable app to $OUT"
dotnet publish src/Timetable.Api/Timetable.Api.csproj -c Release -o "$OUT" -p:SkipClientBuild=true
popd >/dev/null

if [[ "$RUN_E2E" == true ]]; then
  echo "==> End-to-end tests (Playwright: en/ar x light/dark)"
  (cd "$ROOT/frontend" && npx playwright install chromium && npm run e2e)
fi

test -f "$OUT/wwwroot/index.html" || { echo "✗ wwwroot/index.html missing from publish output"; exit 1; }
echo "✓ Published to $OUT (run: cd $OUT && dotnet Timetable.Api.dll)"
