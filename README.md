# SensorDash 0.8.3 — MSI upgrade and release identity fix

The 0.8.3 installer replaces earlier related installations, including duplicate 0.8.2 registrations. Release identity and upgrade requirements are documented in [the installer ADR](docs/adr/0002-msi-release-identity.md).

# SensorDash 0.8.2 — Cooling electrical preset click fix and Production consolidation

Contributors: start with [AGENTS.md](AGENTS.md), the [project/source map](docs/project.md), [development and delivery workflow](docs/development.md), and [architecture decisions](docs/adr/README.md).

The old **Curve Sandbox** screen is now **Fan Control**, with continuous service-owned, administrator-approved fan profiles. It no longer limits actual profile operation to 120 seconds or 70–100% duty; those restrictions remain only for optional manual hardware commissioning tests.

## Continuous fan profiles

- Create as many independent profiles as needed (up to 20), with up to 12 physical PWM outputs and 8 temperature curves per profile.
- Each curve references a discovered physical or virtual temperature source. The collector evaluates all its curves and takes the **maximum requested PWM percentage**, so CPU and GPU can jointly control radiator fans.
- Supports a configurable minimum fan duty, temperature hysteresis (°C), immediate increases, and smoothed decreases. No fan-stop/zero RPM automation is enabled by default.
- Unavailable or stale temperature source: request **100% duty (or the maximum supported by that controller)** until the source recovers. Outputs that disappear or cannot be written are reported as degraded rather than silently ignored.
- A profile is a normal editable draft until clicked **Enable profile**, which requests Windows UAC. The elevated collector snapshots the selected curves and physical outputs and persists the approval separately in `%ProgramData%\SensorDash\approved-fan-profiles.json`.
- Approved profiles **resume after service restart**. Merely editing or deleting a draft does not change approved hardware settings; select **Approve updated profile** to apply changes, or **Stop** to restore firmware/default control.
- Stopping an output/profile and clean service shutdown call LibreHardwareMonitor's `SetDefault()`. Firmware-level recovery after a hard crash/power loss remains dependent on the actual motherboard; BIOS fan safety remains important.
- A writable PWM output can be assigned to at most one approved profile. Optional manual tests cannot commandeer outputs already owned by approved profiles. SensorDash does not guess physical fan headers.
- The DDC pump can remain monitored as a tachometer without an associated writable pump output.

## Included optional curve starters (exact earlier values)

**Shared radiator fans:** Create a CPU curve (40°C/10%, 55/10, 60/15, 65/20, 70/25, 75/35, 80/50, 85/75, 90/100) and GPU-hotspot curve (40°C/10%, 55/10, 60/20, 65/25, 70/35, 75/50, 85/75, 95/100). Combine them in one profile via MAX. Temperature hysteresis is 3°C, falling-speed response 10 seconds. The source sensor IDs are suggested from detected hardware; physical output assignments remain blank until verified.

**NVMe cooler:** Create separate curve (35°C/30%, 40/30, 45/40, 50/55, 55/75, 60/100) with its own output assignment. Storage-sensor age and staleness are enforced, and the output goes to full available speed if the temperature stops updating.

Starter profiles are **opt-in** and hardware-neutral. Installations do not automatically create or activate these profiles.

## Electrical preset action regression (0.8.2)

The Cooling Systems **Use ARCTIC P12 Pro PST LN ×19** and **Use MCP355 / DDC 3.2** buttons previously referenced the shared settings `cfg` before its JavaScript `const` declaration, causing `Cannot access 'cfg' before initialization`. The shared settings binding now occurs at the beginning of the event handler. A **real action-dispatch regression** in `scripts/test-workspace.cjs` clicks both presets, verifies settings persisted, confirms existing RPM assignments survive, checks the **43.08 W** nameplate summary, and verifies that a locked cooling system refuses preset changes.

After successful Windows validation, this fixes the final known blocker before bringing the development work into the repository's `Production` branch. This change is only in the web component editor and has no influence on privileged PWM hardware authorization.

## Cooling electrical component inventory (0.8.1)

**Cooling Systems → Cooling system components** now supports optional electrical specifications for each `Fan group` and `Pump`. These are saved with the existing cooling system settings, independently of the privileged fan-control system:

- Model, physical fan/pump **count**, rated volts, amps per unit or watts per unit, optional power connection and source current limit. The editor lets users associate an existing tachometer, a discovered PWM **Control sensor**, and an optional **dedicated power sensor** with each component; all readings must be fresh before being presented as live. If both rated watts and amps are provided, watts per unit take precedence for nameplate watts.
- Opt-in, editable presets: **ARCTIC P12 Pro PST LN ×19** (12 V, 0.11 A each, powered hub; **25.08 W rated**) and **Swiftech MCP355 / Laing DDC 3.2** (12 V, **18 W rated**, Molex). Together they have **43.08 W combined nameplate rating**. These presets are *not* automatically assigned to unknown hardware; users deliberately select them on the matching component to avoid inventing hardware identity.
- A compact read-only electrical budget appears both on the **Overview** (per cooling system) and on each Cooling Systems page. It shows component rated watts, aggregate rated budget, fan RPM and duty where a suitable sensor exists, source current-limit warnings, and whether power estimates are complete or partial.
- **Actual consumption is not calculated from PWM % × rated watts**. An optional *direct* power sensor can report actual measured component power. Alternatively, users can supply two **whole-group** measured wattages at known PWM levels; a current, fresh Control sensor then enables clearly marked **rough linear interpolation** within those measured bounds. The input measurements already cover the full fan group and are **never multiplied by fan quantity again**.
- Rated pump wattage is a nameplate rating, not automatically measured power. The total estimated live draw includes only measured/calibrated component estimates, explicitly indicates coverage, and never silently treats unknown consumption as zero.
- Reported current vs user-entered connection current limit warns about potentially overloaded power paths. A tachometer on a multi-fan splitter does not measure each fan individually. Power sources and electrical specifications are **advisory metadata only**: no fan control approval, PWM writing, safety thresholds, or output mapping is changed.
- The UI updates electrical readouts on the existing live refresh with an 8 s upper-rate limit; **no new collector polling, SQLite writes or background threads**. Validation clamps counts and electrical inputs server-side; configurations round-trip through the existing settings API. An isolated, hardware-independent Node regression validates the 43.08 W nameplate, rejection of naive PWM estimates, optional direct/calibrated readings, source current alerts, component presets and UI plumbing; C# self-tests validate settings persistence.

**Measurement caution:** A CPU or GPU package power sensor is not a dedicated fan/pump power sensor; don't bind one here. For accurate cooling electricity consumption, use a sensor or external electrical measurement that captures the target component group only. Energy history and lifetime kWh modeling from these new estimates remain separate future capabilities.

## Raw historical maxima, power usage, and storage diagnostics (0.8.0)

The **Overview** now has an on-demand read-only panel for recorded CPU/GPU/NVMe temperature peaks and selected CPU/GPU package/board power sensors. Lookbacks: last 24 hours, 7 days, 30 days, 90 days, or the currently retained history. Individual sensor names come from the current discovered catalog and saved aliases, with no physical device IDs hardcoded.

- Recorded **max temperature** and occurrence date/time, with sample average, are computed from **raw SQLite measurements**, not the 500-point chart averages. Missing history is displayed as missing, not silently replaced by a current live value.
- Power shows **recorded peak W, average sampled W, approximate Wh and actual observed/covered hours**. Watt-hours integrate trapezoids only between successive power measurements separated by a reasonable recording interval (max 90 s for the standard 10-second logging cadence, at most 6 minutes). Unobserved gaps, sleep and shutdown are explicitly **excluded**; this is not estimated whole-PC wall power, electricity billing, or precise physical energy measurement. Sensor domains may overlap; they are deliberately **not summed**.
- SQLite work is read-only and performed only on Overview requests. Separate APIs aggregate raw history server-side using a streaming O(1)-memory accumulator; at most eight sensor IDs per request. The UI caches summaries for 120 s so the usual 1–2 s collector polling does not trigger history scans.

**Settings → History storage footprint** provides the actual file sizes of the SQLite database, WAL and shared memory, total sample rows, earliest/latest samples, and reusable SQLite pages. Hardware-device and individual-sensor tables show **row counts, percentages and approximate proportional shares of main DB size**, explicitly distinguished from exact per-sensor physical file sizes (SQLite pages, indexes, events and freed space are shared). Data is fetched only when Settings opens or Refresh is clicked, with a brief cache interval.

**Retention** remains explicitly adjustable in Settings, now **1–1,095 days (up to three years)**; new installations still default to 30 days. The normal hourly cleanup removes both samples and events older than retention, but increasing retention cannot restore previously purged measurements. Deletions make SQLite pages reusable without automatically truncating the DB file. The database records at the configured save interval and may grow considerably depending on sensor count and recording mode; use real footprint measurements before choosing very long retention.

Tests cover exact raw peaks, bounded gap-aware energy integration, missing-energy cases, overview sensor prioritization, storage allocation labels, and retention controls; Windows CI validates the installer.

## Calendar-aware history time axis (0.7.3)

- **7-day charts** show readable *weekday + month/day* labels positioned at local calendar midnights, rather than ambiguous AM/PM-only labels. Wide charts show approximately one label per day; narrow charts automatically space labels out.
- **30-day charts** show *month/day* at calendar-aligned intervals (roughly every 4–7 days depending on width), including correct rollover across month and year boundaries.
- **15-minute, 1-hour, 6-hour and 24-hour charts** retain conventional clock-time labels; hovering any history chart now shows **full weekday, date, year and local time**, even when the graph crosses midnight.
- Date ticks advance using local calendar days rather than assuming every day lasts exactly 24 hours, avoiding daylight-saving shifts; and tooltip width accommodates the longer timestamp.
- Formatting lives in a small pure `chart-time-axis.js` module with deterministic locale/date-boundary tests. No extra sampling, cached history, IPC, database or fan-control work.

## Accurate history gaps (0.7.2)

- Temperature, power, fan RPM, duty and other canvas histories now **break the plotted line wherever the sample gap exceeds the supported recording interval**. A long outage no longer creates artificial sloped or flat lines between unrelated observations. Isolated readings render as small points.
- Gap tolerance follows the backend's actual SQLite history bucketing (`max(10 seconds, range/500)`, with a 90-second minimum gap tolerance). It scales from the short-history chart through 7-/30-day views without assuming every sensor is collected every second.
- Only when multiple reporting sensors share the same outage do charts display a subtle shaded **NO DATA** band with the missing duration. Individual sensor gaps break only that sensor's line. A small legend explanation appears only when the graph has real gaps.
- Hover and synchronized cooling-system cursors now use bounded, timestamp-aware nearest readings. Hovering midway through an outage shows **No data** for each unavailable sensor rather than an hours-old value.
- `chart-gaps.js` implements the pure gap segmentation, shared-outage intersection, and binary-search hover lookup. Deterministic Node regression tests exercise missing periods, SQL bucket scaling, stale hovers, isolated points, and the actual canvas drawing calls. Changes do not affect collector sampling, SQLite history storage, or hardware fan control.

## Graphs, performance and release labels

- Hover tooltips display sensor names beside their color swatches and current values, rather than colors and numbers alone.
- History queries cache sensor/range pairs for 60 seconds, survive page navigation and reuse in-flight requests. Missing IDs are batched 12 at a time. The cache is bounded to 120 entries; a manual Refresh discards it.
- Sensor frames and chart painting remain independent. While dragging or hovering a graph, periodic frame updates do not reset the canvas.
- Sidebar and footer now say **Fan Control**, show the installed version from the running executable, and indicate whether persistent profiles or temporary manual control are active.
- No physical fan commands are issued merely by installing or opening the UI. The Windows build tests remain manually triggered or on merge/push to Production only.
- Version 0.6.1 spreads chart rendering across animation frames and adds on-demand desktop/collector memory plus browser chart/cache diagnostics in Settings.
- Version 0.6.2 separates the approved-profile lifecycle from the temperature planner, shared interpolation, PWM hardware adapter and write/recovery policy. Hardware-free self-tests exercise actual profile execution with simulated PWM channels, absent/stale/invalid readings, readjustment, write failures, limited duty ranges and shutdown behavior.

**Upgrade:** Close the tray application; install the v0.8.2 MSI. Existing SQLite history, aliases, workspaces, tray readouts, and saved temperature curve drafts remain in place. Quit the separate Fan Control application before enabling the corresponding SensorDash output(s).

---

# SensorDash 0.5.1 — More accurate fan takeover and simpler safety UI

LibreHardwareMonitor IControl reports Undefined for newly initialized controls unless its own settings say otherwise. This is an internal library state, **not a measurement of whether firmware is active or a competing fan controller exists**. SensorDash v0.5.0 mistakenly required Default and conflated mode with supported PWM range; v0.5.1 accepts Undefined, Default and Software modes after explicit user confirmation that other fan-control software is closed.

Output eligibility now uses the channel's declared minimum/maximum PWM range; manual test duty must be inside the channel's supported range and the 70–100% commissioning band. A first takeover from LHM Software mode explicitly transitions through Default so a cached identical SoftwareValue cannot suppress the actual register write. Restore operations remain best-effort; an abrupt process crash cannot run cleanup.

UI wording is intentionally calmer: acknowledge only that other fan-control software is closed and you wish SensorDash to control the output. The conservative 70% test floor and 120-second session limit remain until physical PWM output behavior is validated.

---

# SensorDash 0.5.0 — Experimental Live Fan Control (prior release notes)

Fan Control (the separate app) can simply be closed. Quit its tray/background process and disable its automatic startup before commanding a channel with SensorDash. No migration integration is required. Confirm the fan remains operational under BIOS/default control.

This release introduces the first writable-channel implementation, not a complete unattended replacement for Fan Control.

### Supported experimental workflow

1. Keep SensorDash Collector installed and running. Open http://127.0.0.1:8765/#curves.
2. Discover actual IControl-backed hardware channels. An RPM-only Fan reading is not a writable output.
3. Exit any competing fan-controller application. The LHM control object's mode may report Undefined, Default or Software; this is not a reliable cross-process ownership indicator. Verify actual fan behavior.
4. Select one output. Choose Manual PWM or a saved temperature curve, then a 30/60/120-second test.
5. Acknowledge exclusive ownership and click Request elevated takeover. Windows presents UAC for a one-shot administrator helper.
6. Observe status and fan response. Release the channel to firmware control or allow the lease to expire.

### Security and limitations

- No persisted fan takeovers. Every live session requires an admin-approved UAC prompt, lasts at most two minutes and commands only one channel.
- Initial duty floor 70–100%, even when a saved curve calculates a lower duty. This is a test safety restriction, not a quiet long-term cooling profile.
- A separate named pipe (SensorDash.Fan.Admin.v1) permits only local Windows administrators and SYSTEM to request writes. The ordinary telemetry IPC exposes read-only output inventory/status, not write commands.
- The loopback browser command requires same-origin JSON with a custom header, and only launches the UAC helper; it cannot itself write to hardware.
- All LibreHardwareMonitor SetSoftware/SetDefault operations run on the collector's hardware thread.
- Authorized curves are snapshotted upon elevated acceptance, preventing ordinary settings writes from changing a privileged live lease.
- On lease expiry, explicit release, service shutdown, sensor failure or hardware reinitialization the collector attempts firmware/default restoration. This is BEST EFFORT, not a hardware watchdog: power loss, process crash or a stalled driver can prevent it.
- Fan RPM sensors and PWM control channels are not automatically paired. Confirm physical wiring and fan behavior before using a channel, and do not take over essential pumps until fallback behavior is verified.
- Not yet implemented: silent startup control, unlimited continuous operation, multi-channel control, low-duty/zero-RPM stop modes, independent external watchdog, calibration and automatic RPM matching.

### Validation

The hardware-free self-test checks that fan controls stay disabled by default and rejects unauthorized or unknown channels. Windows MSI workflow verifies admin-only IPC, explicit UAC action, read-only inventory and service installation, without writing any fan hardware on CI.

Upgrade from v0.4.0 by exiting the tray first and running the v0.5.0 MSI. Historical data and saved configurations remain in ProgramData.

---

# SensorDash — General capabilities (0.6.x)

A local-first Windows hardware telemetry dashboard with configurable cooling systems, dashboards, sensor discovery, tray readouts and **previewable, administrator-approved fan profiles**. The collector is an elevated Windows Service; the browser dashboard and notification-area application run as a standard user. Built with .NET 10, LibreHardwareMonitorLib, SQLite and a lightweight embedded web UI.

**Development version: 0.8.2.** The feature branch may be ahead of the most recently validated installer. The standard MSI pipeline runs on `Production` pushes or manual dispatch. Temporary validation branches may deliberately enable a one-off build; routine feature commits do not trigger it.

## Sleep/resume hardware recovery and diagnostic trace (0.7.1)

- The main collector detects a polling interval greater than **20 seconds** (or a backward clock jump). For an **already approved** persistent fan profile, it invalidates PWM writer cache and reasserts physical channel control through `SetDefault → SetSoftware` on the next hardware poll. Hardware write authorization does not change, no new channels are selected, and manual sessions still retain their own shorter authorization/failsafe rules.
- This protects against motherboards resetting their PWM hardware on Windows wake while LibreHardwareMonitor still reports cached software mode/duty. It is a best-effort first recovery layer; **it cannot prove hardware accepted the command** and does not reinitialize underlying ITE/SuperIO device drivers. FanCtrl's explicit Windows resume-driven hardware reload is a useful reference for a future second-stage recovery if the measured problem persists.
- The elevated collector now writes a **bounded, rotating plain-text file** at `C:\\ProgramData\\SensorDash\\logs\\fan-control.log` with `.1` as the previous segment. Each segment is at most roughly 2 MiB. On ordinary operation it records one snapshot per active profile channel per 60 seconds, plus state/duty transitions, approved requests, resume gaps and collector failure/reset messages. It records requested duty, last PWM command, LHM-reported control mode and the **mapped tachometer RPM** (when fresh); logging is best-effort and never blocks hardware control. A tachometer on a splitter only represents the reported sensor.
- The Fan Control screen has a collapsible **Sleep / PWM diagnostic log** section. Show, copy or save the **latest 250 log lines** on demand over an existing authenticated local IPC read path. There is no continuous diagnostic file polling or per-second database write.
- Hardware trace files are intentionally separate from normal SQLite time-series sensor history and events. Existing SQLite audit events log administrator-approved fan actions; the text file explains hardware controller state. Historical logs do not prove that physical PWM changed, nor that the fan spins solely because RPM is present.
- Deterministic C# tests use a fake PWM output that retains a stale software-mode/duty after a simulated 65-second sleep, and verify it is reset and rewritten. Browser tests verify diagnostic fetch/copy, UI and bounded logging contracts. To report a repeat, capture the trace **before rebooting**, plus CPU/GPU temps and radiator fan RPM.

## Phase 1 Cooling Systems analysis (0.7.0)

Cooling Systems now uses the existing saved component-to-sensor assignments for a **read-only, system-aware overview**: independent heat sources, fan banks, pumps, optional coolant, optional case air and optional room ambient. Dedicated `coolant-probe`, `case-air` and `room-ambient` roles are supported alongside the general temperature-probe role. A case-air measurement is **never** treated as room ambient. Missing probes are labeled *Not measured*, and the feature is useful with no coolant or ambient probe installed.

- All automatic temperature, power, RPM, duty and flow graphs use the **existing cached SQLite history API** and a synchronized cross-chart hover cursor/time range for a given system. Individual cooling systems stay independent. Click a relevant event to inspect its instant across charts.
- **Exploratory co-movement** compares the first assigned temperature and power reading within each heat-source component, only when at least 12 time-aligned samples have sufficient variation. Pearson r describes simultaneous co-movement, **not** heat rejection, cooling efficiency, causal impact or thermal lag. No CPU/GPU wattages are blindly summed.
- The recent cooling activity strip filters the existing monitoring event feed to sensors or fan channels/profiles assigned to this system. New UAC-approved fan-test, profile-approval and stop requests are recorded by the elevated service using the existing event store. Events are explicitly labeled **requests** because physical PWM writes occur later on the collector thread; there is no invented retrospective event history.
- Component configuration is collapsed by default to prioritize live analysis, expandable for edits, with existing per-system editor locks and saved assignments retained. The event list, correlations and fan timelines are observational only; **fan behavior and authorization remain unchanged**.
- New modules `cooling-insights.js`, `cooling-timeline.js` and `CoolingEventRecorder.cs` keep analysis and activity tracking out of the existing UI/controller monolith. Dedicated deterministic JavaScript and C# self-tests exercise role normalization, missing probes, correlation, event filtering, cross-chart cursor, and accepted action audit.

**Hardware note:** Coolant, case and room temperature probes are all optional. You can add a case-air sensor whenever ready without claiming it measures room ambient. Actual coolant-to-ambient delta and radiator efficiency analysis will be addressed in a later phase when appropriate sensors exist.

### Desktop host resource footprint

The non-elevated `SensorDash.exe` tray/web process now uses the ASP.NET Core **slim** minimal host and **reuses the bridge worker's latest 2-second sensor frame** when updating tray icons. Previously the tray made its own duplicate collector IPC request and parsed the same sensor JSON independently. The elevated hardware collector is unchanged, as are the server-sent events, local-only HTTP endpoints and per-user preferences. These changes reduce unnecessary initialization and duplicate allocation work; **do not assume a particular MB reduction** from CI, because Windows process working set includes runtime, DLL mappings and shared pages. Compare private bytes and thread counts after a cold startup and a warmed-up browsing session; the external browser's memory is not included in the tray process.

## Live-profile changes, compact sections and editor locks (0.6.4)

- Approved profiles retain a collector-owned snapshot: **saving a draft or curve does not silently change live PWM**. Each `fanOutputs` status read compares the approved hardware snapshot against the latest saved editable settings, reporting `needsReapproval` and clear reasons for changed PWM assignments, minimum duty, hysteresis, response time, curve assignments, source sensors, freshness or duty points. Renaming alone does not force reapproval.
- Fan Control prominently displays a **saved changes pending** warning and per-curve impact on enabled profiles, with a one-click **Approve saved changes** action that still requires Windows UAC. The button approves the saved draft only; it never silently includes uncommitted input field edits. Saving a changed curve immediately refreshes collector status, and unsaved curve edits show a warning.
- Fan Control is reorganized into collapsible **Profiles, Temperature curves, Commissioning, and Manual diagnostics**, with expandable individual profiles and curves. Collapse state persists through rerenders within the browser session.
- **Cooling system lock**, **Fan Control editor lock** and the existing **Dashboard layout lock** use the same concept: a persistent preference that hides/disables editing, never a security boundary. Per-cooling-system locks preserve all live readings and charts. Fan Control editor lock leaves **Stop & restore defaults** and manual **Release** available even while other edits are locked. Administrator approval remains separate and mandatory.
- Physical **PWM-to-RPM mapping is the single source of truth** for fan-bank labels and tachometer monitoring. The old optional RPM/control sensor selector is no longer shown on individual curves. Profiles show the mapped RPM live and report whether it is verified or unavailable. Historic `OutputSensorId` configuration remains loadable for compatibility but no longer drives active fan feedback or sensor-retention decisions. This is observational monitoring only, not RPM stall detection or proof every fan behind a splitter spins.
- Newly created radiator starters now use the tuned **10% plateau from 40°C through 55°C**. Only two points are needed to define a flat interpolated segment; the redundant 50°C/10% point is omitted. Existing saved curves and active approved profiles are not overwritten or retuned by the upgrade. A 10% running duty observed on a fan is not proof of reliable startup at 10% across every fan in a bank.

## Guided physical channel identification and calibration (0.6.3)

The Fan Control screen includes a dedicated **Identify → Name → Calibrate → Verify** assistant. It stores *advisory* mappings of discovered PWM outputs to fan-bank labels and optional tachometer (RPM) sensor IDs. It does not guess header wiring or use mapping metadata as hardware-write authorization.

- Identify each channel as **fan**, **pump**, or **unknown**. Unknown/pump outputs have no commissioning test buttons, and the privileged controller also rejects timed manual tests for channels identified as pumps. The user must confirm fan versus pump wiring.
- Test a named fan using the existing UAC-authorized exclusive **30-second manual session**, at **100%, 85%, or 70%** PWM only. Each test requires a new elevation prompt. Stop/release remains available.
- When an RPM sensor is assigned, capture a **fresh, positive** reading during the **matching active manual test**, after a five-second stabilization period. Recordings are evidence of observed RPM, not proof all fans on a splitter are spinning.
- User verification requires physical confirmation and at least two measurements separated by 10 PWM percentage points with an 8% RPM rise. Changes to channel type or tachometer clear observations/verification. Mappings and measured points persist with settings across upgrades.
- **No automatic low-duty sweep, discovery of a minimum spinning duty, or fan-profile modification.** Actual spin-up duty, pump safety and firmware recovery remain hardware-specific.

The implementation lives in `FanCommissioningModels.cs`, `FanCommissioningSelfTest.cs` and `fan-commissioning.js`, not a monolithic fan controller. C# normalization verifies bounds, deduplication and no pump calibration. A separate JavaScript regression suite runs in CI.

## Fan-control architecture and tests (0.6.2)

The live control path is deliberately split into small, testable responsibilities:

| Module | Responsibility | Hardware dependency |
| --- | --- | --- |
| `FanCurveMath` | One source of truth for point interpolation; shared by simulation and live control | None |
| `FanProfilePlanner` | Curve/source evaluation, MAX demand and temperature hysteresis; immediate full-speed failsafe if any required source is missing/stale | None |
| `PersistentFanProfiles` | Persist separately approved snapshots, validate ownership, coordinate activation/stop on collector thread | Only thin adapter at tick boundary |
| `IFanPwmChannel` / `LhmPwmChannel` | Abstract direct LibreHardwareMonitor control calls for real and synthetic channels | Adapter only |
| `FanPwmWriter` | Bounds, takeover, immediate upward changes, smooth downward changes, recovery attempts | Injected interface |
| `FanControlSelfTest` | Execute real profile/writer logic with fake outputs and deterministic timestamps | None |

Run `SensorDash.exe --self-test` to run the deterministic workspace and fan-control suites without opening any hardware. The same test suite runs in the Windows CI workflow before the MSI is packaged. The short manual commissioning session retains its separate conservative 70–100% / 120-second limits.

**Important safety limits:** a successful `SetSoftware` call does not verify actual fan RPM. There is no automatic mapping from PWM channels to tachometer sensors, and software cannot guarantee restoring firmware mode after a process crash or power loss. Real calibration, stall detection and hardware-specific firmware recovery must be validated on the machine. Use motherboard BIOS thermal protections and avoid running competing controllers.

## What Cooling Systems are for

A **Cooling System** is an analytical view of related hardware measurements, not merely a named folder of sensors or a literal inventory of every mechanical part. Create as many liquid, air, or custom cooling systems as needed. Each system consists of a user-defined set of components, with a small number of meaningful roles:

| Component role | Appropriate sensor assignments | What it contributes |
| --- | --- | --- |
| Heat source | Temperature, power, load, clock | Hardware heat source readings and historical graphs |
| Fan group | Fan RPM, available output duty, optional temperature | Fan performance and response over time |
| Pump | Pump RPM, flow rate, reported duty, optional temperature | Circulation and pump monitoring |
| Temperature probe | Temperatures (coolant, ambient, inlet, outlet, etc.) | Water/air temperature history |
| Flow meter | Flow readings | Flow trend |
| Other hardware | Any compatible sensor | Specialized hardware without a preset role |

Passive radiators are **not** a required component type: a radiator is only relevant through measurements provided by its fans, probes and associated system. The role labels guide assignment and visualization; no assumptions about a particular CPU, GPU, coolant loop, number of radiators or fan controller are embedded.

Each cooling system automatically displays a telemetry summary and independent temperature, power, RPM, duty and flow charts **where those sensor types are assigned**. It can also **Create dashboard**, which builds ordinary editable dashboard widgets from the current associations. That created dashboard is a snapshot of the assignments; the Cooling System itself always reflects its current components. Power measurements are shown individually and not blindly summed because sensor readings may overlap (e.g., package and core power). Temperature and flow charts never imply physically measured cooling capacity.

## Sensors, usage and visibility

**Sensor Explorer** discovers every reading the collector reports. It can group by hardware or sensor type, filter to a selected device, search by hardware/name/ID/usage, and quickly show **In use** or **Hidden** readings. Generic hardware names such as "Temperature #1" come from the hardware library; default Overview cards prioritize descriptive names, while Explorer lets you add aliases.

The **Used in** section of a sensor lists every assignment to named dashboards and widget titles, cooling systems and component names, tray readouts, virtual sensors, simulated curves, and monitoring thresholds. Clicking a cooling-system reference navigates directly to the **specific component** and highlights it. One physical sensor may be referenced in multiple places without duplication.

Visibility, collection and history are deliberately **different settings**:

1. **Hide sensor, device or category** — presentation-only. Configured dashboards and curves still receive data.
2. **Disable collection for a hardware family** — turns off that LibreHardwareMonitor `Computer` category and reopens discovery; all readings for that family disappear until reenabled. All eight current families use the same generic UI: CPU, GPU, Motherboard/Super I/O, Memory, Storage, Network, Controllers and Power Monitors.
3. **Pause an individual discovered hardware device** — skip that device's periodic `Update()` and expose its sensor names with unavailable values. Underlying driver and parent-device behavior can still require shared work.
4. **Choose SQLite history policy** — Balanced (default), All, or In-use-only, with independent hardware-type exclusions. This reduces future database writes without necessarily changing hardware polling. Retained old samples expire on normal cleanup.

There is no misleading single-sensor "hardware polling" toggle: the hardware library typically performs updates at device granularity.

## Dashboards and tray

Dashboards contain configurable metric, chart and live-reading widgets; create a blank dashboard or use thermal, cooling or storage templates. **Lock layout** hides widget edit/add/remove controls while preserving visualization. It is a UI convenience rather than an authorization boundary.

Pin up to six independently configured Windows notification-area **numeric readouts**. Select the physical or virtual sensor, label, accent color and precision. Short, high-contrast digits optimize readability at native 16–32 px tray sizes. Hovering shows the complete value and name. The main tray menu opens the dashboard, reports collector status and closes the desktop UI without stopping logging.

Fan curves use an editable **Temperature (°C) / Fan speed (%) numeric table** with add/remove rows, range checks, live preview and 1 Hz simulation. Simulations never write to outputs. Saved curves can be added to persistent fan profiles, which require administrator approval and exclusive ownership of verified physical PWM channels. Do not run competing fan controllers on the same outputs.

## Installation and data

A successful [Windows build](https://github.com/CodyMathis123/SensorDash/actions/workflows/windows-build.yml) produces the MSI. To install or upgrade, exit the SensorDash tray, run the MSI and approve administrator access. It installs an automatic `SensorDashCollector` Windows Service, the branded `SensorDash` all-users Start Menu shortcut, and a tray autostart entry. Uninstall removes those managed components.

The service's SQLite history and settings live under `%ProgramData%\SensorDash`. They are retained across MSI updates. Prior prototype-specific preferences are not maintained with compatibility shims; adjust any old custom configurations using the new editors if needed.

The collector and desktop communicate over an ACL-restricted local named pipe. The elevated collector has no HTTP listener. The standard-user dashboard listens only on `http://127.0.0.1:8765/`. Hardware monitoring, fan-curve simulation, and locally approved fan profiles work without cloud access. The MSI does not install, replace or configure Fan Control or PawnIO.

## Build and test

On Windows with the .NET 10 SDK:

```powershell
node --check src/SensorDash/wwwroot/app.js
node --check src/SensorDash/wwwroot/workspace.js
node scripts/test-workspace.cjs
dotnet publish src/SensorDash/SensorDash.csproj -c Release -r win-x64 -o dist
.\dist\SensorDash.exe --self-test
dotnet build installer/SensorDash.wixproj -c Release -p:Platform=x64
```

The JavaScript regression test covers auto-generated cooling analytics, meaningful component types, in-use sensor references, exact Explorer→Cooling navigation, history query batching, hidden sensors, layout locks and generated dashboard widgets. The executable self-test covers virtual sensor arithmetic, simulated curves, missing/stale readings, tray formatting, collection groups, history filtering and cooling component normalization.

The full Windows workflow also smoke-tests the service-to-dashboard named pipe and verifies MSI install, service startup, Start Menu shortcut creation, tray registration and uninstall. It runs on **`Production` pushes or manual dispatch** once the workflow exists on the default branch. A validation branch can temporarily enable its own build trigger. Ordinary feature commits do not trigger it.

This application is not safety-rated fan-control software. Live fan profiles are implemented, but physical PWM channel mapping, fan startup at low duty, ownership, firmware-level recovery and fail-safe response must be verified on each target machine before relying on unattended control.
