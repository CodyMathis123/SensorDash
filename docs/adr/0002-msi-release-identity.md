# 0002: Versioned MSI major upgrades and stable release identity

- Status: Accepted
- Date: 2026-10-30

## Context
Multiple changed packages shipped as 0.8.2 with automatically generated ProductCodes. Default major-upgrade detection excludes equal versions, so Windows registered these side by side. Clean-install tests did not catch this.

## Decision
Use `Directory.Build.props` for the three-part release version, ProductCode and permanent UpgradeCode. Version 0.8.3 introduces this policy. Increment a Windows Installer-recognized version field for every shipped change (major/minor/build limits 255/255/65535). Assign a new ProductCode for each release, preserving it for rebuilds of that release. WiX generates the package identity for each MSI build; never reuse a PackageCode for a changed package. Component GUIDs remain stable for unchanged component identities/key paths.

Retain UpgradeCode `6D8F4B3B-3780-4A7C-AD1D-58343B1AD2CE` across the product family. Use major upgrades, reject downgrades, and schedule removal after InstallInitialize so removal participates in rollback. A higher version removes all detected older related ProductCodes, including the duplicate 0.8.2 registrations. Keep user data outside installer-owned resources.

## Alternatives
Random ProductCodes for unchanged version rebuilds recreate duplicate registrations. Enabling equal-version replacement masks release discipline problems and cannot distinguish newer from older payloads at that version. Minor updates require different servicing rules and are unnecessary for this project. Changing UpgradeCode would strand existing installations.

## Consequences and validation
Release PRs must advance version and ProductCode; CI enforces this. The Windows integration test uses frozen historical installer authoring with the current payload to reproduce duplicate registration and tests migration, repair, downgrade rejection, service restart, cleanup and data survival. This is a servicing test, not a compatibility test of historical application binaries. Actual release artifacts remain immutable; install the resulting 0.8.3 MSI to repair related old registrations. Do not manually delete Windows Installer registry records.

References: [Microsoft UpgradeCode guidance](https://learn.microsoft.com/en-us/windows/win32/msi/using-an-upgradecode), [Microsoft major upgrades](https://learn.microsoft.com/en-us/windows/win32/msi/major-upgrades), [WiX MajorUpgrade scheduling](https://docs.firegiant.com/wix/schema/wxs/majorupgrade/).
