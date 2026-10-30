# Hardware driver setup

SensorDash embeds LibreHardwareMonitorLib 0.9.6. PawnIO is needed for supported low-level CPU and motherboard operations, not for every sensor. GPU vendor APIs, storage APIs and USB controllers can have independent access paths. Missing CPU temperature or board fan sensors can therefore coexist with otherwise working telemetry.

The SensorDash MSI does **not** install PawnIO. Install or update the **official signed edition** from [pawnio.eu](https://pawnio.eu/), then restart **SensorDash Collector** in Windows Services or restart Windows. SensorDash Settings → Hardware driver reports status, and a warning appears across pages when selected hardware families may need attention. No driver download or installation runs from a web request. The MSI remains usable without PawnIO and uninstall never removes it; other apps may share it.

## Detection and recovery

The elevated collector checks both registry views for PawnIO's version and opens/closes its device handle with the access required by LibreHardwareMonitor, without loading modules or executing IOCTLs. This distinguishes registered installation from accessible driver. Checks refresh every 30 seconds. Version 2.0 or newer follows the prerequisite check in [LibreHardwareMonitor v0.9.6](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitor/UI/MainForm.cs); registration and device path follow its [PawnIO implementation](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/v0.9.6/LibreHardwareMonitorLib/PawnIo/PawnIo.cs).

States: missing, outdated, unavailable, unknown, available, restart-required. Available means the collector can open the driver, not that all modules or hardware are supported. After a driver appears, recovers or changes version in an already-running collector, restart is requested instead of automatically reinitializing hardware or active fan controls. LibreHardwareMonitor caches installation information, and hardware discovery must run again. Inaccessible installations should be repaired with the official installer or resolved by restarting Windows; do not disable Windows security protections.

CPU, motherboard and memory collection selections determine whether the global driver warning is relevant. Settings retains status even when those families are disabled. Errors do not claim the driver is missing. Status travels in sensor frames and diagnostics so it reflects the service account rather than the browser user. Live status updates only replace the driver UI region, preserving ongoing edits.

## Delivery validation

Hardware-free backend self-tests cover state evaluation, version checks and restart transitions. Browser tests cover warning visibility and status changes. Windows CI verifies embedded UI delivery and collector status on the standalone API, then runs the usual MSI lifecycle checks. CI never installs PawnIO or opens real devices during `--self-test`.

A bundled dependency would require an explicitly consented bootstrapper, pinned signed payload, licensing review and separate shared-driver servicing policy. Do not introduce nested driver installation as an MSI custom action.
