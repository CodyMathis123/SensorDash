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

Release identity lives in `Directory.Build.props`: increment `SensorDashVersion` and generate a new `SensorDashProductCode` for shipped application/installer changes. Keep `SensorDashUpgradeCode` unchanged. Executable, MSI and artifact naming consume that single source. PR CI enforces advancement relative to the base revision. Rebuilds of the same release retain their ProductCode; changed distributed payloads require a new version. Use the exact tested MSI for distribution rather than silently replacing a released artifact.

The disposable Windows runner tests two legacy 0.8.2 registrations, replacement with the new version, one remaining registration, service restart, repeat installation, repair, downgrade blocking, uninstall and user-data preservation. `scripts/Test-MsiUpgrade.ps1` must never be run on a personal machine.

1. Inspect relevant implementation and tests; make a focused change on a branch from `Production`.
2. Run appropriate local checks, review the diff, update documentation and add an ADR only for meaningful architectural decisions.
3. Push and create/update a PR targeting `Production`. Its description states the problem, resulting behavior, validation and known limits.
4. The **Windows MSI and portable build** workflow runs automatically on PR creation and updates. Manual fallback: Actions → that workflow → Run workflow → select the PR branch.
5. Wait for completion and inspect every job, failing step/log and expected MSI/portable artifacts. Confirm the run corresponds to the current PR head (or GitHub's merge ref for that head). Fix failures and repeat; a run for an older revision is insufficient.
6. When tests and the full pipeline succeed, review the final diff, resolve required reviews, mark ready if needed, and merge using an expected-head check. The owner has authorized this step without repeated confirmation. Never bypass branch protection.
7. Verify the PR merged and monitor the ensuing `Production` pipeline. Report the PR, validation result and any production-build failure. Investigate failures without representing the delivery as fully validated.

The workflow uploads build artifacts; merging is not authorization to install on the owner's machine or perform physical fan commissioning. Live hardware validation is separate from hardware-free CI.
