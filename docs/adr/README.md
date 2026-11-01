# Architecture decision records

Use ADRs for durable decisions affecting architecture, dependencies, persistence, security/control boundaries or delivery workflow. Use the next four-digit number and a descriptive filename. Copy [the template](template.md), describe alternatives and tradeoffs, and link the accepted decision here. Supersede historical records with a new ADR rather than rewriting their rationale.

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-repository-guidance-and-delivery.md) | Accepted | Shared repository guidance and validated PR delivery |
| [0002](0002-msi-release-identity.md) | Accepted | Versioned MSI major upgrades and stable release identity |
| [0003](0003-ci-msi-versions.md) | Accepted | Increasing CI build versions and previous-Production upgrade tests; supersedes allocation/testing portions of 0002 |
| [0004](0004-history-reclamation-and-accessory-estimates.md) | Accepted | Retention space reclamation and accessory power history |
| [0005](0005-cooling-preset-catalog.md) | Accepted | Reviewed JSON cooling presets packaged with the application |
| [0007](0007-remote-telemetry-boundary.md) | Superseded for local UI by 0008 | Opt-in outbound-only telemetry, stable IDs, bounded cursors and schema evolution |
| [0008](0008-local-control-observability.md) | Implemented on feature branch; physical verification pending | Windows controls and authoritative cooling configuration, Pi historical visualization |
| [0009](0009-history-family-cadence.md) | Implemented on feature branch; Windows CI pending | Independent Storage/Network archive intervals; keep thermal history and Pi export unchanged |
