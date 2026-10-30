# User interface guidance

Write for someone operating SensorDash. Keep implementation details in developer documentation or collapsed diagnostics.

- Show useful values, units, status and actions. Use concise labels; avoid paragraphs that restate nearby controls.
- Put numeric limits and brief explanations in field tooltips. Keep actionable errors and hardware-control consent visible.
- Use compact single-row editors: sensor/name, value, small accessible delete button. Long names may truncate with a full-name tooltip; controls must remain usable at narrow widths.
- Show aliases as the primary name. Sensor Explorer and the alias editor retain original names for identification.
- Keep measured, calibrated and rated estimates distinguishable with a short source label and a focusable information tooltip. Do not imply missing history is zero or a rating is measured consumption.
- Display history storage size and estimated daily growth prominently. Database internals belong in diagnostics, not routine settings prose.
- Verify layout in a browser and preserve shared settings persistence and fan-control approval boundaries.
