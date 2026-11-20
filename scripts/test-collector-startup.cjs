'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const program = fs.readFileSync('src/SensorDash/Program.cs', 'utf8');
const startup = fs.readFileSync('src/SensorDash/CollectorStartupLog.cs', 'utf8');
const collection = fs.readFileSync('src/SensorDash/HardwareCollection.cs', 'utf8');
const wix = fs.readFileSync('installer/SensorDash.wxs', 'utf8');
const doctor = fs.readFileSync('scripts/Get-SensorDashDiagnostics.ps1', 'utf8');

// The earliest collector path must install fatal exception breadcrumbs BEFORE
// the host is constructed. A library-owned thread is outside SensorWorker's try.
assert.ok(program.indexOf('CollectorStartupLog.Install()') <
          program.indexOf('WebApplication.CreateSlimBuilder('));
assert.match(startup, /AppDomain\.CurrentDomain\.UnhandledException/);
assert.match(startup, /CommonApplicationData/);
assert.match(startup, /collector-startup\.log/);
assert.match(startup, /catch\s*\{/);
assert.match(program, /CollectorStartupLog\.Write\("info", "Hardware opened/);

// MSI should configure SCM recovery, not invoke a second copy of the collector.
assert.match(wix, /<util:ServiceConfig[^>]*FirstFailureActionType="restart"/);
assert.match(wix, /ThirdFailureActionType="restart"/);
assert.match(wix, /RestartServiceDelayInSeconds="60"/);
assert.match(wix, /ServiceControl[^>]*Stop="both"/);

// Storage remains enabled by default; an explicit opt-out can mitigate
// DiskInfoToolkit native callbacks without disabling motherboard PWM.
assert.match(collection, /SENSORDASH_DISABLE_STORAGE/);
assert.match(collection, /IsStorageEnabled = !StorageSafeMode && Enabled\(settings,"Storage"\)/);
assert.match(collection, /IsMotherboardEnabled = Enabled\(settings,"Motherboard"\)/);
assert.match(doctor, /Get-WinEvent/);
assert.doesNotMatch(doctor, /telemetry\.db|credentials\.json|token/i);
console.log('Collector startup/recovery contract checks passed.');
