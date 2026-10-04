import { defineConfig } from '@playwright/test';

/**
 * End-to-end tests against the real API + built SPA, served by the test-only E2E host (fresh demo database per run).
 * Every spec runs in four combinations: English/Arabic × light/dark.
 * Prerequisite: `npm run build:prod` (the host serves backend/src/Timetable.Api/wwwroot).
 */
const port = Number(process.env['E2E_PORT'] ?? 5199);
const executablePath = process.env['E2E_CHROMIUM'] || undefined;

export default defineConfig({
  testDir: './e2e',
  timeout: 90_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env['CI'] ? 1 : 0,
  reporter: process.env['CI'] ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: `http://localhost:${port}`,
    viewport: { width: 1440, height: 900 },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    launchOptions: { executablePath },
  },
  projects: [
    { name: 'en-light', use: { colorScheme: 'light' }, metadata: { lang: 'en', theme: 'light' } },
    { name: 'en-dark', use: { colorScheme: 'dark' }, metadata: { lang: 'en', theme: 'dark' } },
    { name: 'ar-light', use: { colorScheme: 'light' }, metadata: { lang: 'ar', theme: 'light' } },
    { name: 'ar-dark', use: { colorScheme: 'dark' }, metadata: { lang: 'ar', theme: 'dark' } },
  ],
  webServer: {
    command: `dotnet run --project ../../tests/Timetable.E2EHost --no-launch-profile -- --urls http://localhost:${port}`,
    cwd: '../backend/src/Timetable.Api',
    url: `http://localhost:${port}/health`,
    reuseExistingServer: !process.env['CI'],
    timeout: 240_000,
    stdout: 'ignore',
    stderr: 'pipe',
  },
});
