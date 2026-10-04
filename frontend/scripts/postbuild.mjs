// Angular's deleteOutputPath wipes wwwroot; restore the tracked placeholder.
import { writeFileSync } from 'node:fs';
writeFileSync(new URL('../../backend/src/Timetable.Api/wwwroot/.gitkeep', import.meta.url), '');
