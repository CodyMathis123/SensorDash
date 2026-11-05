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
