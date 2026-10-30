# Working in SensorDash

Read [project and source map](docs/project.md), [development workflow](docs/development.md), and the [ADR index](docs/adr/README.md) before changing code. These are the shared instructions for human and AI contributors; keep them current when architecture or workflow changes.

## Working rules
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
For shipped application or installer changes, advance the three-part version and assign a new release ProductCode in `Directory.Build.props` before building. Keep the UpgradeCode permanent and the ProductCode stable across builds of that release. Never distribute changed payloads under an already shipped version. Follow [MSI release identity](docs/adr/0002-msi-release-identity.md); the Windows upgrade regression and release-policy gate must pass before merge.

The owner authorizes the normal delivery loop: implement, test, push a PR, run the Windows pipeline, inspect the complete result, and merge when all required tests and the pipeline pass. No additional merge confirmation is needed for that loop.

Follow [the full workflow](docs/development.md). Never merge on a pending, failed, cancelled or skipped validation run. Validate the latest PR revision; new commits require a new run. Respect branch protection and review requirements. Report concrete blockers rather than treating local environment failures as successful validation. After merging, monitor the Production run and report its result.
