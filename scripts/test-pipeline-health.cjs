const assert = require("node:assert/strict");
const fs = require("node:fs");

const program = fs.readFileSync("src/SensorDash/Program.cs", "utf8");
const pipeline = fs.readFileSync("src/SensorDash/PipelineHealthMetrics.cs", "utf8");
const exporter = fs.readFileSync("src/SensorDash/TelemetryExport.cs", "utf8");
const test = fs.readFileSync("src/SensorDash/PipelineHealthSelfTest.cs", "utf8");
const wix = fs.readFileSync("installer/SensorDash.wxs", "utf8");
const profiles = fs.readFileSync("src/SensorDash/PersistentFanProfiles.cs", "utf8");

assert.match(program, /windowsLifetime\.AutoLog = false/);
assert.match(program, /AddWindowsService\(options => options\.ServiceName = "SensorDashCollector"\)/);
assert.match(wix, /EventSource Name="SensorDash\.Collector" Log="SensorDash"/);
assert.match(program, /AddSingleton<PipelineHealthMetrics>\(\)/);
assert.match(program, /MapGet\("\/api\/pipeline-health"/);
assert.match(program, /pipeline = metrics\.Snapshot\(/);
assert.match(program, /metrics\.ObserveScan\(scanDuration\)/);
assert.match(program, /metrics\.ObserveControl\(/);
assert.match(program, /metrics\.ObserveCoolingInputs\(/);
assert.match(program, /metrics\.ObserveInterval\(/);
assert.match(program, /metrics\.ObserveFamily\(/);
assert.match(program, /PipelineHealthSelfTest\.Run\(\)/);
assert.match(profiles, /CoolingInputRequirements\(\)/);
assert.match(pipeline, /Interlocked\.Increment\(ref _buckets/);
assert.match(pipeline, /P95Ms, double\? P99Ms, double\? MaxMs/);
assert.match(pipeline, /SampleExportCursorLagSeconds/);
assert.match(pipeline, /sensordash_reading/);
assert.match(exporter, /PipelineHealthMetrics\.SerializeForPi/);
assert.match(exporter, /metrics\.SetExportCursor\(cursor\.TimestampMs\)/);
assert.match(test, /Pi allowlisted line protocol/);

const critical = program.slice(program.indexOf("public sealed class SensorWorker("));
assert.doesNotMatch(critical, /SqliteConnection|db\.Record\(|JsonSerializer\.Serialize\(/);
assert.match(critical, /controls\.Tick\(_computer, frame, cfg, now\)/);
assert.match(critical, /SleepAwareStageStamp\.Start\(\)/);
console.log("PASS: bounded pipeline instrumentation, stable service source and Pi-compatible gauges");
