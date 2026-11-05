# Development and delivery

## Local checks

Use Windows, the .NET 10 SDK, Node.js and PowerShell. From the repository root:

```powershell
Get-ChildItem src/SensorDash/wwwroot/*.js | ForEach-Object {
  node --check $_.FullName
  if ($LASTEXITCODE -ne 0) { throw "Syntax check failed: $($_.Name)" }
}
Get-ChildItem scripts/test-*.cjs | ForEach-Object {
  node $_.FullName
  if ($LASTEXITCODE -ne 0) { throw "Test failed: $($_.Name)" }
}
dotnet build src/SensorDash/SensorDash.csproj -c Release
git diff --check
```

Check each command's exit status. The Windows workflow is authoritative for the complete backend self-test commands, publish, MSI creation, install/uninstall checks and artifact validation. Local package/network limitations must be reported; a successful remote Windows run can supply that missing validation.

## Memory-first performance diagnostics

Settings → Performance diagnostics shows a one-minute sampling cadence for the
desktop/tray process and the elevated collector. Each process retains at most
1,440 in-memory samples spanning up to 24 hours; history is discarded on restart
and is never written into the telemetry SQLite database. The **Export memory
history CSV** button combines the two process streams without exporting sensor
values, saved settings, machine names or token material.

Use **private memory** to compare process-private commitment, **working set** to
compare resident pages (which can include shared memory), and **GC committed
memory** to assess how much is committed by the managed GC. Do not label the
difference as exact native memory; runtime and other components reserve memory
in different ways. Inspect memory after startup, one hour, and 24 hours before
changing collector sampling or fan-control hardware access. Collector/IPC
timing fields are secondary diagnostic context, not extra polling.

## Controlled .NET GC memory experiments

The default MSI runtime settings remain unchanged. Diagnostic CSV now records
`serverGc`, `gcHeapCount` and `gcConserveMemoryLevel` when the runtime
exposes those settings, as well as last-GC heap size, fragmentation, and the
cumulative fraction of runtime paused in GC. A null effective configuration
entry is **unknown**, not proof of a default setting. The last-GC values are
unavailable until the first GC. No collection is requested by diagnostics.

The `Windows MSI build` workflow offers a manual `gc_profile` choice:
`baseline` (normal installer and normal GC runtime policy) or `workstation`
(explicit Workstation GC for **both** processes). On `Production`, the workflow
always uses the baseline and rejects experimental selections. Every new
workflow run has a unique MSI version/ProductCode; the experimental MSI filename
ends with `-workstation-gc.msi` and is validated against the previous Production
MSI in the existing installer servicing test.

To start the experiment against the feature branch, **prefer GitHub CLI** while
the workflow input is not yet on `Production`: the web form may still show the
default-branch workflow's older inputs.

```powershell
gh workflow run windows-build.yml --repo CodyMathis123/SensorDash --ref feature/performance-diagnostics-and-footprint -f gc_profile=workstation
```

After the run, check Actions → Windows MSI build and confirm the run SHA,
`gc_profile=workstation` selection, the `-workstation-gc.msi` artifact name,
successful automated checks, and the smoke-test assertion that both running
processes report `serverGc=false`. This is a **manually requested run**;
ordinary feature commits and PR edits still trigger no CI.

**Supervised real-PC installation is a separate decision.** Before replacing an
installed automatic fan-control service, retain its working installer and
verify a viable downgrade/rollback strategy. The MSI intentionally rejects
downgrades and a previously built baseline MSI has a lower version than the
experimental MSI. Rolling back may therefore require a *newer versioned*
baseline MSI or a carefully planned uninstall/reinstall preserving user data.
Do not assume running an older baseline MSI will revert the GC experiment.
Avoid installing any unverified or failed artifact on the cooling PC.

Compare the same configuration, workload, and runtime (including installed
driver state) for startup, 1-hour, and 24-hour readings. Export both process
histories for every trial and use private bytes as the primary footprint metric;
check GC pauses, collection-cycle latency, recovery logs, and fan-control parity.
Reset by reinstalling the last validated baseline MSI, not by manipulating
the live service or GC environment variables. Changing GC mode has no promised
memory benefit. Never apply a GC heap hard limit during these first tests.

Microsoft reference: https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector

## Required PR workflow

CI prepares release identity before compilation. The major.minor series comes from Directory.Build.props; the third MSI field is GITHUB_RUN_NUMBER. ProductCode is a deterministic UUID for the platform/version, while UpgradeCode is permanent. Thus a Production run is newer than its PR run. Retries of the same run retain its version/source identity; start a new workflow run to produce a new distributable version. Local source version values are development defaults and must not be distributed. Preserve the workflow's counter; if replacing/resetting the workflow, advance the version series first. The allocator fails at the MSI field limits instead of wrapping.

Routine servicing validation downloads the MSI artifact from the latest successful Production push, installs it, and upgrades with the desktop running. It also checks exactly one registration, expected executable version, service restart, repeated install/repair, downgrade rejection, desktop closure on uninstall and user-data preservation. Missing or expired baseline artifacts fail validation rather than silently skipping it. The duplicate-0.8.2 case was validated once and is no longer rebuilt on every run. Installer logs are uploaded even on failure and each operation has a timeout.

1. Inspect relevant implementation and tests; make a focused change on a branch from `Production`.
2. Run appropriate local checks, review the diff, update documentation and add an ADR only for meaningful architectural decisions.
3. Push and create/update a PR targeting `Production`. Its description states the problem, resulting behavior, validation and known limits.
4. The **Windows MSI build** workflow never runs automatically for feature branches, fix branches, or pull requests. When validation is explicitly requested or required before merge, use Actions → Windows MSI build → Run workflow → select the feature branch. The same workflow runs automatically on every push to `Production` (including merges).
5. For a manually requested run, inspect every job, failing step/log and expected MSI and installer-diagnostic artifacts. Confirm it corresponds to the latest branch commit; if a subsequent change requires revalidation, deliberately dispatch a new run. Do not mistake an earlier successful run for validation of newer changes.
6. Before merging, review the final diff and required checks. Where the delivery requires Windows CI, manually validate the final head and merge only after the required pipeline succeeds. Resolve reviews and merge using an expected-head check when authorized; never bypass branch protection. This workflow deliberately does not generate a PR check unless manually dispatched.
7. Verify the PR merged and monitor the automatically triggered `Production` pipeline. Report the PR, validation result and any production-build failure. Investigate failures without representing the delivery as fully validated.

The workflow uploads build artifacts; merging is not authorization to install on the owner's machine or perform physical fan commissioning. Live hardware validation is separate from hardware-free CI.

The single MSI lifecycle test validates previous Production installation, upgrade, repeat install, repair, downgrade rejection, shortcut/autostart entries and uninstall with desktop closure. Do not add a redundant install/uninstall cycle for the same assertions.

**Unattended Windows runner:** The MSI test starts an actual `SensorDash.exe --desktop --no-browser` fixture and leaves it running for each upgrade/maintenance/uninstall operation. The test passes `MSIRESTARTMANAGERCONTROL=Disable` and `REBOOT=ReallySuppress` only to its own disposable-runner `msiexec` calls, preventing Windows Restart Manager from taking over with a files-in-use prompt; the MSI's WiX `CloseApplication` must still close the *running desktop* and the existing ServiceControl must still stop/restart the collector. Do not pre-kill the test desktop or replace its post-MSI `Assert-Closed` checks. Installer CloseApplication sends graceful messages before its deferred termination fallback, while the collector remains service-managed. The real desktop's MSI upgrade is separate: close the tray UI before updating on production hardware; do not forcibly kill an active fan-control service.

Publish only the raw MSI (upload-artifact v7, archive false) and installer diagnostics. download-artifact v8 handles both new raw MSI artifacts and older zipped baselines; no explicit extraction step is needed. The published application folder is build input, not a separate portable download.
