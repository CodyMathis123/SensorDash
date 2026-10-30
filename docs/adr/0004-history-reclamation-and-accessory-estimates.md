# 0004: History space reclamation and accessory energy

- Status: Accepted
- Date: 2026-10-30

## Context
Retention deleted readings without reclaiming database file space. The overview omitted fan and pump energy despite configured measurements or ratings.

## Decision
Enable SQLite FULL auto-vacuum before collection starts. Existing databases receive a one-time VACUUM migration; new databases enable the mode before schema creation. Retention deletes samples and events in one transaction, then requests a non-waiting WAL truncation checkpoint. Run cleanup hourly and when retention changes. Active readers may defer the checkpoint until a later cleanup.

Accessory statistics stream recorded sensor values through the existing gap-aware energy accumulator. Prefer measured watts, then whole-group calibrated PWM watts, then configured rated power during observed RPM runtime. Ratings use watts per unit first, otherwise volts times amps, multiplied by quantity. Never multiply calibrated whole-group measurements by quantity or infer runtime from the requested summary duration. A rating without runtime remains a rating with unknown energy. Current component settings interpret historical samples; source labels and short information tooltips disclose estimates. No synthetic sensors or hardware commands are introduced.

## Alternatives
Keeping freed pages without reclamation was rejected for predictable retention storage. Recurring full VACUUM would repeatedly rebuild large files and interrupt collection. Applying ratings continuously across the summary window would invent energy through shutdowns; multiplying rating by PWM is not a validated power model.

## Consequences and validation
The one-time migration can delay first startup and needs temporary free space. Auto-vacuum adds pointer-map overhead and may move pages during commits. SQLite migration and retention regression checks verify retained data, integrity and actual file shrinkage. Accessory regressions verify source precedence, group calibration, observed runtime and gap exclusion. Windows CI runs these without accessing hardware. UI tests preserve unknown values and concise provenance.
