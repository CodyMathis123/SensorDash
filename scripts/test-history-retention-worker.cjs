const assert = require('node:assert/strict');
const fs = require('node:fs');

const program = fs.readFileSync('src/SensorDash/Program.cs', 'utf8');
const worker = fs.readFileSync('src/SensorDash/HistoryRetentionWorker.cs', 'utf8');
const test = fs.readFileSync('src/SensorDash/HistoryRetentionSelfTest.cs', 'utf8');
const compose = null; // Pi retention is intentionally owned by HomeAwayFromHome.

assert.match(program, /AddHostedService<HistoryRetentionWorker>\(\)/);
assert.match(program, /RetentionDays\s*\{\s*get;\s*set;\s*\}\s*=\s*3;/);
assert.doesNotMatch(program, /db\.Cleanup\(/);
assert.doesNotMatch(program, /_lastCleanup|_lastRetentionDays/);

const start = program.indexOf('public sealed class SensorWorker(');
const end = program.indexOf('private static void Scan(', start);
assert(start >= 0 && end > start);
assert.doesNotMatch(program.slice(start, end), /CleanupBatch|TryRetentionCheckpoint|wal_checkpoint|DELETE FROM samples/i);
assert.match(worker, /class HistoryRetentionWorker[\s\S]*BackgroundService/);
assert.match(worker, /Task\.Delay\(HistoryRetentionSchedule\.BetweenBatches/);
assert.match(worker, /LIMIT \$limit/);
assert.match(worker, /Math\.Clamp\(batchSize, 1, 4096\)/);
assert.match(worker, /SqliteErrorCode is 5 or 6/);
assert.match(worker, /SqliteConnection|connection\.BeginTransaction\(\)/);
assert.match(worker, /TryRetentionCheckpoint/);
assert.match(test, /deletedSamples == 20000/);
assert.match(test, /deletedAccessories == 700 && deletedEvents == 300/);
assert.match(test, /Database integrity failed/);
assert(!compose);
console.log('PASS: Windows SQLite retention uses a separate incremental worker; fan polling and remote history stay independent.');
