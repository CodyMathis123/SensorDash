# Cooling component presets

Presets are reviewed, per-unit product data packaged into SensorDash. Select one in **Cooling Systems → Electrical specifications → Preset**, then choose **Apply preset**. Fans and pumps have separate filtered lists.

## Contribute a preset

1. Copy a file from `cooling/` and name it `<id>.json`. Use a stable lowercase ID with hyphens. No application code or registry edit is needed.
2. Set `schemaVersion: 1`, `revision: 1`, the component kind (`fan-group` or `pump`), manufacturer and exact model/variant. Use a new ID for a different voltage or product variant.
3. Cite public HTTPS source URLs for the ratings and any curve, preferably manufacturer documents. Describe test voltage, sampling and limitations in `notes`. Do not assume an older or similarly named model has identical specifications. Do not copy whole reviews into the repo.
4. Enter per-unit electrical ratings and, optionally, `powerCurve` rows in PWM percent, RPM and watts per unit. At least one speed column is needed per row; at least two unique coordinates on an axis are needed to interpolate. Null/omitted speed coordinates are allowed. Duplicate coordinates on an axis are rejected. Curves require a short `powerCurveSource` label. Keep ratings separate from measured curves; never invent points by multiplying rated watts by PWM percent.
5. Run `node scripts/test-cooling-presets.cjs` and the normal repo checks. Submit a PR targeting `Production`. CI validates the schema, embeds every preset, checks the standalone executable serves the catalog, and runs the normal MSI lifecycle test.

[`cooling.schema.json`](cooling.schema.json) defines allowed fields, units and bounds. Unknown fields are rejected. A rating may use watts per unit, or volts plus amps per unit. A sourced curve can also be contributed without a nameplate rating. The Node validator additionally checks provenance URLs, IDs/filenames and curve coordinates.

For corrections, retain the ID and increment `revision`. Explain changed numbers and evidence in the PR. Reviewers should verify sources and exact product variant. New catalog fields require a deliberate schema/runtime change; the dependency-free validator fails on unsupported schema keywords.

## Application behavior

Applying a preset replaces model ratings and curve with a copied snapshot, including the preset ID/revision. It preserves quantity (defaults to one for a new component), sensor bindings, power source/wiring limits and user-measured whole-group calibration. A preset cannot supply those installation-specific values. Replacing a curve with a rating-only preset clears the old curve rather than leaving data from another product.

Upgrades do not silently reapply or revise saved components, including older components without preset IDs. Reapply explicitly to adopt revised catalog data. Values remain editable. Sources and notes ship with the catalog; the application does not fetch vendor websites. Presets never authorize fan control.

The initial entries are ARCTIC P12 Pro PST LN and Swiftech MCP355. See [power-curve calculation notes](../docs/power-curves.md).
