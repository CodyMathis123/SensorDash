# 0005: Packaged cooling component preset catalog

- Status: Accepted
- Date: 2026-10-30

## Context
Fan and pump product specifications were hardcoded in the electrical editor. Contributions should be reviewable data changes, with clear sources and no machine-specific wiring or sensor configuration.

## Decision
Store one versioned JSON document per product/variant in `presets/cooling/`, governed by `presets/cooling.schema.json`. Require stable IDs, revisions, kind, manufacturer/model and provenance. Catalog data is per unit; quantities, sensor IDs, wiring limits and user calibration are not allowed. Validate structure, numeric bounds and usable non-ambiguous curves in CI.

Embed all catalog documents into the executable using an MSBuild wildcard. A read-only local endpoint serves them without network/vendor access. The editor filters the dropdown by component kind. Applying a preset copies explicit specification fields and records ID/revision while preserving installation-specific fields and calibration. Saved components retain snapshots across catalog updates; no automatic reapplication occurs.

## Alternatives
A JavaScript registry still requires application edits for each contribution. A generated catalog adds a build-generation dependency and risks stale checked-in output. Online discovery introduces availability and trust requirements that are unnecessary for reviewed bundled presets.

## Consequences and validation
JSON-only contributions become dropdown options on the next build. Source review is still necessary: schema validation cannot establish measurement accuracy. The lightweight validator supports this schema's keywords and fails on unsupported additions. Backend self-tests check embedded resources; the standalone HTTP smoke test compares served IDs/revisions against source files. UI tests cover filtering, data-only extensions, scope locks, independent snapshots and preservation of quantities, mappings and calibration. Full MSI lifecycle validation remains required.
