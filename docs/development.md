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
4. The **Windows MSI and portable build** workflow runs automatically on PR creation and updates. Manual fallback: Actions → that workflow → Run workflow → select the PR branch.
5. Wait for completion and inspect every job, failing step/log and expected MSI/portable artifacts. Confirm the run corresponds to the current PR head (or GitHub's merge ref for that head). Fix failures and repeat; a run for an older revision is insufficient.
6. When tests and the full pipeline succeed, review the final diff, resolve required reviews, mark ready if needed, and merge using an expected-head check. The owner has authorized this step without repeated confirmation. Never bypass branch protection.
7. Verify the PR merged and monitor the ensuing `Production` pipeline. Report the PR, validation result and any production-build failure. Investigate failures without representing the delivery as fully validated.

The workflow uploads build artifacts; merging is not authorization to install on the owner's machine or perform physical fan commissioning. Live hardware validation is separate from hardware-free CI.

The single MSI lifecycle test validates previous Production installation, upgrade, repeat install, repair, downgrade rejection, shortcut/autostart entries and uninstall with desktop closure. Do not add a redundant install/uninstall cycle for the same assertions.
