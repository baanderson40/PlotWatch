# PlotWatch — Product Specification and Implementation Plan

Dalamud plugin for FFXIV housing lottery tracking, available-plot discovery, alerts, and PaissaDB-contributing ward sweeps.

Commands: `/plotwatch` and `/pw`.

> **Status:** Phase 1 shell implemented and Release build verified. In-game UI/lifecycle validation remains pending. Lottery tracking, PaissaDB access, alerts, integrations, and sweeps are not implemented yet.

## Product specification

### Goals

- Track housing lottery application/results periods and per-character bids.
- Show crowdsourced available houses from PaissaDB (`https://paissadb.zhu.codes`).
- Alert on matching open plots and important bid/result deadlines.
- Automate ward-menu review sweeps to contribute observed housing data through the optional PaissaHouse plugin.
- Work as a standalone plugin for tracking and browsing; PaissaHouse is required only for the planned contribution sweep flow.

### Lottery schedule

- Cycle: 9 days total — 5-day application period, then 4-day results period.
- Dashboard shows current period, explanation, live countdown, start/end, and next occurrence.
- Schedule reference: [Xenoveritas FFXIV timer](https://xenoveritas.org/static/ffxiv/timer.html). Its published schedule is modeled as a repeating 9-day cycle; calculations run locally and do not depend on scraping the page.
- Display times in the user's local timezone, including timezone abbreviation. Keep timestamps in UTC and let timezone rules handle daylight-saving changes.
- Initial schedule anchor from the supplied reference: application period starts 2026-11-01 15:00 UTC. Validate the anchor against the reference schedule before release and keep it isolated/configurable in the schedule policy.
- Game-observed deadlines on individual bids take precedence over cycle-derived estimates. Label estimates clearly.
- Test exact boundaries, cycle rollover, local timezone conversion, and daylight-saving transitions.

### Bid tracking and characters

- Automatically discover and track every character that uses PlotWatch and places or reports a bid.
- Record character name/world, region, district, ward, plot, plot size, FC/personal purchase type, bid number when observed, entry time, result time/deadline, and outcome.
- Capture bid confirmation messages passively. Support recovering existing entries from the Housing Lottery Status UI and enriching a known bid from its placard.
- Provide manual add/edit for data the game no longer exposes.
- Outcomes: Pending, Won, Lost, Claimed. Do not infer a loss merely because results period started.
- Notify once when results become available and when a win is observed; provide configurable claim/refund reminders.
- Character management supports exclude/re-enable and remove. Excluding stops future capture but preserves records. Removing requires confirmation and deletes that character's saved records; removal alone does not prevent rediscovery.
- English-first UI and game-message parsing for initial release.
- Persist bid history and user settings locally per plugin installation. Single-client scope; no cross-client shared storage requirement.

### PaissaDB house browser and alerts

- Use public read endpoints at `https://paissadb.zhu.codes`; inspect `/openapi.json` and live response schema during implementation.
- `/worlds` lists worlds; `/worlds/{worldId}` returns world and district details including available plots.
- Convert PaissaDB's zero-based ward/plot values to the game's one-based display values in one API boundary.
- Show world, district, ward, plot, size, price, purchase system, lottery phase, entrant count when reported, and last observed/update time.
- Filter by region, data center, world, district, ward/plot ranges, size, price, and FC/personal availability. Blank filters mean any.
- Watch rules alert on matching plots. Acknowledge/Seen suppresses repeats until plot is observed closed and reopens.
- Refresh on a configurable interval and on demand. Use bounded requests, timeout, backoff, and cache last successful response.
- Never present API failure or missing fields as “no houses”; show unavailable/stale state and retain last successful data with its timestamp.
- Do not maintain a separate durable local copy of housing inventory. Temporary API cache and in-progress sweep state are acceptable.

### Ward sweeps and contributions

Sweeps exist primarily to contribute ward observations to PaissaDB. PlotWatch does not claim sweep data was uploaded unless integration provides an explicit acknowledgment.

Planned dependencies:

- **PaissaHouse:** optional companion plugin and required for starting contribution sweeps. It observes and submits housing data. Validate compatibility with automated menu cycling and avoid duplicating uploads.
- **Lifestream:** optional for tracking/browser features; required for automatic travel between cities during a multi-district sweep.
- **vnavmesh:** planned navigation dependency for reaching city aetherytes when not already in interaction range.

Sweep flow:

1. User chooses district(s) and ward(s)/range; default is current district and all its wards.
2. Validate required plugins and current game state before starting.
3. Lifestream travels to the selected major city; PlotWatch waits for arrival and safe state.
4. vnavmesh approaches the correct city aetheryte if needed.
5. PlotWatch targets/interacts with the aetheryte and navigates **Residential District Aethernet → Go to Specified Ward (Review Tabs)**.
6. Cycle selected ward tabs, waiting for the corresponding ward observation before advancing.
7. Repeat for requested districts/cities, then show completed, skipped, timed-out, and unverified wards.

Controls and safeguards:

- Manual start; visible progress; pause/resume/cancel.
- Require PaissaHouse availability before starting; clearly report the missing dependency. No misleading local-only “contribution sweep” mode.
- Validate target, menu state, selected ward, and observed response at every stage; no fixed-delay-only success assumptions.
- Bounded retries and timeouts. Stop or pause safely on unexpected UI, combat, logout, territory change, user cancellation, unavailable travel, or missing observations.
- Never count a ward as complete without seeing its corresponding data. If PaissaHouse exposes no upload acknowledgment, report “observed; upload status unknown,” not “uploaded.”
- No automatic lottery entry, bid placement, purchase confirmation, or refund collection.
- Keep navigation and UI automation opt-in, transparent, and subject to current FFXIV/Dalamud rules. Validate in-game; automation can carry account risk.

### Notifications and travel actions

- Chat and toast notifications are configurable; optional sound is off by default.
- Optional Lifestream travel actions from bid rows and matching available-house alerts.
- Hide or disable integration actions when the relevant plugin is missing or busy. Never make optional integrations a requirement for core tracking.

### Main UI

1. **Dashboard:** current lottery phase and countdown, next transitions, upcoming period dates, pending bids, reminders.
2. **Available Houses:** PaissaDB listings, filters, sorting, source freshness, optional travel action.
3. **My Bids:** per-character bid history and outcomes; add/edit/recover, travel, character exclude/remove.
4. **Alerts:** watch rules, matching plots, acknowledge/Seen, refresh controls.
5. **Sweeps:** plugin readiness, city/district/ward selection, progress, pause/resume/cancel, final status summary.
6. **Settings:** refresh/notification preferences, column visibility/order, integration status, character management, diagnostics.

### Architecture direction

- `Plugin.cs`: Dalamud entry point, commands, lifecycle, composition root.
- `Data/`: configuration, bid and watch-rule models, persistence, migrations.
- `Lottery/`: pure schedule/cycle policy and bid state transitions.
- `Paissa/`: typed API client, DTOs, cache, error/staleness state.
- `Game/`: English chat/status/placard observation boundaries; framework-thread access only.
- `Integrations/`: narrow IPC clients for PaissaHouse, Lifestream, and vnavmesh; optional dependency checks.
- `Sweeps/`: explicit state machine for travel, navigation, menu interaction, ward observations, pause/cancel, and recovery.
- `Windows/`: dashboard, houses, bids, alerts, sweep, settings.
- Keep schedule and filtering logic pure and testable. Keep native/game/UI/IPC reads on Dalamud framework updates, not ImGui rendering or arbitrary background tasks.
- Use `IPluginLog`; log state transitions and failures without exposing character Content IDs.

## Phased implementation plan

### Phase 1 — Foundation and working UI

**Status:** Implemented; Release build passes. In-game acceptance checks remain pending.

**Deliverables**

- Main window with Dashboard, Houses, Bids, Alerts, Sweeps, and Settings tabs.
- Bug-icon title-bar button opens a separate diagnostics window with left navigation and right action/results panel.
- Diagnostics actions are explicit, queued, read-only checks; results appear in the window and `IPluginLog.Debug` output. Warnings/errors use corresponding log levels.
- Debug sections cover overview, configuration, commands/UI lifecycle, dependency metadata, and bounded session history.
- `/plotwatch` and `/pw` both open the main window. Register `/pw` only if available; remove it only if PlotWatch registered it.
- Settings includes an **Open diagnostics** action; the debug window starts closed and opens from either that action or the bug icon.
- Register `UiBuilder.OpenMainUi` and `UiBuilder.OpenConfigUi`; unsubscribe callbacks during disposal.
- Add manifest tags for housing, lottery, and tracking.
- Versioned configuration/persistence boundaries, diagnostics, and integration-readiness display.
- Persisted text-size slider from 75% to 150% (default 100%).
- Add appropriate `.gitignore`; preserve dependency lockfiles.

**Acceptance checks**

- Windows .NET Release build succeeds.
- Main/config buttons open the real UI.
- Bug icon and Settings action open/focus diagnostics; diagnostics actions log findings without running automation or mutating configuration.
- Reloading plugin leaves no orphaned callbacks or command registrations.
- Dalamud validation notices are resolved through real UI callbacks and manifest metadata, not dummy handlers.

### Phase 2 — Lottery clock and dashboard

**Deliverables**

- Pure UTC schedule service for the nine-day cycle: five-day application, four-day results.
- Initial application-start anchor: **2026-11-01 15:00 UTC**, subject to verification against the [Xenoveritas timer](https://xenoveritas.org/static/ffxiv/timer.html).
- Dashboard shows current phase/explanation, live countdown, start/end, and next occurrence.
- Local timezone display with UTC option; schedule calculations work offline.
- Explicitly distinguish calculated schedule dates from game-observed per-bid deadlines.

**Tests**

- Exact phase boundaries, cycle rollover, dates before anchor, timezone conversion, and daylight-saving transitions.
- Countdown refresh after system clock adjustment and plugin reload.

### Phase 3 — Character management and bid records

**Deliverables**

- Detect characters using PlotWatch and associate each bid with its character/world.
- Persist bid address, size, FC/personal type, ticket number when known, entry/result/claim dates, outcome, and field provenance.
- Manual bid add/edit for missing or historical information.
- Character exclude/re-enable and confirmed record removal; optional combined remove-and-exclude.
- Versioned, recoverable, per-installation persistence. Single-client scope.

**Rules and acceptance**

- Excluding a character stops future capture but preserves existing records.
- Removing a character deletes that character's saved records; removal alone allows later rediscovery.
- Unknown data remains unknown. Records survive restart; duplicate observations do not create duplicate bids.
- Persistence errors show recovery guidance rather than silently losing data.

### Phase 4 — Passive bid capture and results

**Deliverables**

- Capture English bid-confirmation messages passively.
- Recover existing bids from Housing Lottery Status and enrich an already known bid from its placard.
- Unrelated placard browsing never creates bid records.
- Outcomes: Pending, Won, Lost, Claimed. Capture results and claim reminders when evidence supports them.
- Game-observed deadlines override calculated estimates; ticket number remains editable if unavailable.

**Tests and acceptance**

- Duplicate messages/placard visits, missing ticket/deadline, different characters/cycles, and mismatched plot/world.
- Results becoming available without an observed outcome.
- No false Won/Lost transitions from generic placard text; results-period start alone never marks a bid Lost.

### Phase 5 — PaissaDB house browser

**Deliverables**

- Typed client verified against live `https://paissadb.zhu.codes/openapi.json` and API responses.
- Query world/district listing data; centralize conversion from zero-based API ward/plot numbering.
- Show address, size, price, purchase restrictions, lottery phase, entrant count when reported, and source freshness.
- Filters for region/DC/world/district/ward/plot ranges/size/price/purchase type, with sorting and manual refresh.
- Bounded concurrency, cancellation, timeouts, backoff, and last-successful temporary response cache.

**Rules and acceptance**

- No durable independent local housing inventory; temporary API cache is acceptable.
- Unknown entrant count is not zero. API failure is not “no houses.”
- Cached listings remain visible with stale status and last-success timestamp.
- API errors and partial responses are visible; opening UI does not cause request bursts.

### Phase 6 — Watchlists and notifications

**Deliverables**

- Saved multi-filter rules; blank filters mean any; ward/plot ranges supported.
- Matching-house list with Seen acknowledgment and manual recheck/clear acknowledgments.
- Configurable chat and toast alerts; optional sound disabled by default.
- One-time results/win notifications and configurable claim reminders.

**Rules and acceptance**

- Acknowledged plots alert again only after observed closure then reopening.
- Failed/partial refreshes cannot prove closure or trigger false reopen alerts.
- Deduplicate notifications across refreshes and plugin reloads.

### Phase 7 — Integration validation

| Integration | Required for |
| --- | --- |
| PaissaHouse | Starting contribution sweeps |
| Lifestream | Automatic travel between cities; optional plot travel from bid/listing rows |
| vnavmesh (planned) | Approaching city aetherytes when outside interaction range |

**Deliverables**

- Verify current IPC contracts, readiness/busy/error signals, cancellation, and ownership semantics; do not assume undocumented APIs.
- Narrow optional integration clients and show dependency status in UI.
- Verify PaissaHouse observes ward-menu automation and determine whether upload acknowledgment is exposed.
- Verify Lifestream arrival locations and interaction range at each supported city.

**Acceptance / decision gate**

- Missing providers disable only dependent actions; core tracking/browsing remain usable.
- Sweeps are blocked without PaissaHouse. Never claim an upload without acknowledgment.
- If no upload acknowledgment exists, report the strongest verified state, e.g. “observed; upload status unknown.” Document findings before sweep implementation.

### Phase 8 — Single-district contribution sweep

Start with the user already at an open ward-selection menu to validate observation and contribution behavior independently from travel/navigation.

**Deliverables**

- Select district and ward(s)/range; default to current district and all its wards.
- State machine: `Validate → Select ward → Await matching observation → Advance → Summarize`.
- Manual start, progress, pause/resume, cancel, per-step timeout, and bounded retries.
- Temporary progress only; no durable local plot inventory.

**Rules and acceptance**

- Ward count comes from verified menu/game data where possible.
- Never mark ward complete from elapsed time alone; correlate observation to selected world/district/ward.
- Report observed, upload-confirmed/unknown, skipped, timed-out, and unverified separately.
- Unexpected UI or missing data pauses/stops safely and never appears as success.

### Phase 9 — City interaction and navigation

**Deliverables**

- Resolve correct city aetheryte; use vnavmesh to approach when needed.
- Confirm player readiness and interaction range; target/interact with the aetheryte.
- Navigate **Residential District Aethernet → Go to Specified Ward (Review Tabs)**.
- Validate target and menu at each transition; handle verified city-specific differences.

**State flow:** `Validate → Approach → Interact → Validate menu → Open ward review → Sweep`.

**Acceptance**

- Test representative arrival positions in every supported city.
- Wrong target/menu never triggers blind follow-up clicks.
- Unreachable target times out; cancellation releases only PlotWatch-owned activity.

### Phase 10 — Multi-city sweeps

**Deliverables**

- Select multiple districts and coordinate Lifestream travel with aetheryte approach/menu/sweep flow.
- Preflight destinations, show per-district and total progress, summarize skipped/incomplete work.
- Configurable skip/stop behavior for known blockers; bounded recovery from interrupted travel.

**State flow:** `Preflight → Travel → Confirm arrival → Approach → Open menu → Sweep → Next district`.

**Acceptance**

- Full selected-city sweep completes in-game and reports only verified observations.
- Combat, logout, territory changes, unavailable travel, provider loss, and cancellation stop safely.
- No indefinite retries; plugin restart never silently resumes automation.

### Phase 11 — Release hardening

**Deliverables**

- Tests for schedule, filters, bid identity/state, alert deduplication, persistence, and sweep transitions using fake clock/game/provider seams.
- In-game checklist for supported cities, ward counts, missing-plugin scenarios, and interruption/recovery paths.
- Dependency/setup documentation, source attribution, license review, automation disclosure.
- Redact character Content IDs from logs; verify clean install and release packaging.

**Acceptance**

- Release build and offline tests pass; in-game validation is recorded separately.
- Optional dependencies degrade gracefully; no automatic bidding, claiming, or refund collection.

### Delivery checkpoints

1. **Tracking MVP:** phases 1–4.
2. **Housing discovery:** phases 5–6.
3. **Verified contribution sweep:** phases 7–8.
4. **Automated multi-city sweeps:** phases 9–10.
5. **Release candidate:** phase 11.

### Licensing and attribution

- HousingLottoTracker is a feature reference, not a code dependency. This project will implement equivalent user-facing bid-tracking behavior independently unless reuse permission and a suitable license are verified.
- PaissaHouse is MIT-licensed per its repository; prefer runtime integration rather than bundling its code. Preserve required attribution/license notices if any source is later reused.
- Attribute PaissaDB as the source of crowdsourced listing data and link users to its site/API documentation.

## Build

Requires .NET 10 SDK and Dalamud API 15 development files.

From `/mnt/d/Coding/C#/PlotWatch`, build with installed Windows .NET SDK:

```sh
"/mnt/c/Program Files/dotnet/dotnet.exe" build PlotWatch/PlotWatch.csproj -c Release
```

The initial scaffold registers `/plotwatch` and `/pw`; product features described above are pending implementation.
