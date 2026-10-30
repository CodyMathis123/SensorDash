# Working in SensorDash

Read [project and source map](docs/project.md), [development workflow](docs/development.md), and the [ADR index](docs/adr/README.md) before changing code. These are the shared instructions for human and AI contributors; keep them current when architecture or workflow changes.

## Working rules
- Configured sensor aliases are the primary display name in selectors, charts and readings. Sensor Explorer exposes and searches both alias and original name; preserve sensor IDs and raw names in stored telemetry. Installer servicing must close the desktop dashboard as well as stop the collector, with a running-desktop regression on the disposable Windows runner.
- Work on a focused branch targeting `Production`. Preserve unrelated user changes.
- Inspect the actual implementation before proposing a fix. Prefer small changes and existing modules over new frameworks or dependencies.
- Add behavioral regression coverage for bugs, persistence, concurrency and control policy. Avoid tests that merely duplicate implementation text.
- Persist configuration through the shared settings queue. Do not let responses overwrite newer local edits or sensor updates rebuild active forms.
- Use discovered sensor IDs; never guess physical headers. Distinguish measured, estimated and rated values. Missing or stale data must not appear current.
- Keep editable fan drafts separate from administrator-approved hardware control. Never activate PWM writes merely by editing settings, opening a page or installing an update.
- Retain collector fail-safe and firmware/default restoration behavior. Hardware-affecting changes need hardware-free policy tests and explicit disclosure of any remaining physical validation.
- Do not commit secrets, local data, build output or machine-specific paths. Keep source-map and ADR links relative.
- Record significant architectural decisions in an ADR; routine fixes need only clear code and a PR description. Do not add parallel agent/tool-specific rule files that duplicate this document.

## Completion policy
CI allocates each new workflow run a three-part MSI version using major.minor from `Directory.Build.props` and the monotonically increasing workflow run number as the build field. ProductCode is derived from that version; UpgradeCode remains permanent. PR and Production runs must have distinct increasing versions. Do not distribute local development builds or changed payloads under an existing version. Follow [CI MSI versions](docs/adr/0003-ci-msi-versions.md); previous-Production upgrade tests must pass.

The owner authorizes the normal delivery loop: implement, test, push a PR, run the Windows pipeline, inspect the complete result, and merge when all required tests and the pipeline pass. No additional merge confirmation is needed for that loop.

Follow [the full workflow](docs/development.md). Never merge on a pending, failed, cancelled or skipped validation run. Validate the latest PR revision; new commits require a new run. Respect branch protection and review requirements. Report concrete blockers rather than treating local environment failures as successful validation. After merging, monitor the Production run and report its result.

## UI changes
Follow [UI guidance](docs/ui-guidelines.md): concise user-facing copy, field limits in tooltips, compact editable rows and accessible information icons. Keep implementation notes in documentation or collapsed diagnostics. Preserve meaningful hardware consent and distinguish measurements from estimates.
