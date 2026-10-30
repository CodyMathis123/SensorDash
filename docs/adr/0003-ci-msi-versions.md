# 0003: CI versions and previous-Production upgrade validation

- Status: Accepted
- Date: 2026-10-30
- Supersedes: release-number allocation and routine duplicate-fixture testing in ADR 0002

## Context
Rebuilding a PR on Production under the same version obscures package identity. Duplicate 0.8.2 recovery was a one-time regression and unnecessarily expensive as a permanent fixture.

## Decision
Allocate MSI ProductVersion as major.minor.workflow-run-number. The series comes from Directory.Build.props, which CI updates before compilation. Derive a version-specific x64 ProductCode using UUID v5, retaining the permanent family UpgradeCode. Application version, MSI version and artifact filename consume the resulting shared properties. Every new workflow run, including Production after merge, advances the build number. A rerun attempt has the same source/run identity; use a new run for a new distributable package. Preserve the counter, and advance the series if creating a replacement workflow or before exceeding Windows Installer's field bounds. Never wrap the counter.

Use the actual MSI from the latest successful Production push as the routine upgrade baseline. Record its run and commit. Fail clearly if that artifact is absent or expired. Install it, upgrade with a running desktop, and verify one product registration, version, service recovery, repair/reinstall, downgrade rejection, uninstall closure and data survival. Retain the historic duplicate fix in git history rather than constructing it on every build.

## Consequences
Versions can have gaps and the third number is a CI build sequence, not a semantic patch count. Production versions exceed prior PR versions. Test and Production packages have different identities and both are validated. Artifact retention now matters for upgrade testing. No automatic deletion of old installed-product registry entries is needed.
