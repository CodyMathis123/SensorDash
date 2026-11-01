# User interface guidance

Write for someone operating SensorDash. Keep implementation details in developer documentation or collapsed diagnostics.

- Show useful values, units, status and actions. Use concise labels; avoid paragraphs that restate nearby controls.
- Put numeric limits and brief explanations in field tooltips. Keep actionable errors and hardware-control consent visible.
- Use compact single-row editors: sensor/name, value, small accessible delete button. Long names may truncate with a full-name tooltip; controls must remain usable at narrow widths.
- Show aliases as the primary name. Sensor Explorer and the alias editor retain original names for identification.
- Keep measured, calibrated and rated estimates distinguishable with a short source label and a focusable information tooltip. Do not imply missing history is zero or a rating is measured consumption.
- Keep the local SQLite **export retry spool** and retention status understandable in Settings; use Grafana for historical storage trends and long-term graphs. Hide internal database details behind diagnostics instead of recreating legacy Windows history dashboards.
- Verify layout in a browser and preserve shared settings persistence and fan-control approval boundaries.
- Settings add-sensor pickers exclude already configured IDs (including zero-valued thresholds). Add/remove actions preserve pending Settings edits, and recording-mode options retain brief descriptions.
