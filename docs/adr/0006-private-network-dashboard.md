# ADR 0006: Opt-in private-network dashboard viewing

- Status: Accepted
- Date: 2026-10-31

## Context

The desktop process currently binds Kestrel to loopback and provides both telemetry reads and collector-backed mutations. A phone on the LAN needs dashboard viewing, but serving those APIs on an unauthenticated Internet-facing endpoint would expose sensitive telemetry and hardware-adjacent settings.

## Decision

- Remain loopback-only by default. A desktop-user preference in `%LocalAppData%\\SensorDash\\network.json` opts in to listening on IPv4 interfaces at port 8765. Changes require a desktop restart.
- Permit non-loopback requests only from private IPv4 ranges, RFC6598 carrier-grade NAT addresses (for Tailscale IPv4), IPv6 unique-local and link-local ranges. Deny public source addresses. The administrator must separately configure Windows Firewall for trusted networks.
- **All non-loopback clients are read-only**, enforced in server middleware before routing. GET/HEAD are allowed; POST/PUT/PATCH/DELETE are denied even for trusted networks. Collector privileges and fan authorization rules do not change.
- Display the remote read-only state in the UI. Keep all API calls same-origin; no permissive CORS. Never auto-enable firewall rules or router port forwarding.
- For access away from home, recommend an authenticated private VPN such as Tailscale; public HTTPS with domain validation and a reverse proxy is a separate deployment feature.

## Tradeoffs

LAN telemetry and preferences can be read by other users on the trusted network without application authentication and HTTP is unencrypted. The private-address check is not identity authentication, and NAT or reverse proxies can change the source IP. Do not port-forward or use untrusted Wi-Fi. A future authenticated HTTPS mode should introduce a distinct secure access policy, not reuse this unauthenticated option.
