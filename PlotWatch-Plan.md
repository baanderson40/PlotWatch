# PlotWatch — Product Specification and Implementation Plan

Dalamud plugin for FFXIV housing lottery tracking, available-plot discovery, alerts, and bid records.

Commands: `/plotwatch` and `/pw`.

> **Status:** Phases 1–2 implemented; Phase 3 multi-world PaissaDB browser and hardening implemented. Release build and offline verification results must be recorded separately from in-game acceptance. Phase 2 schedule/dashboard validated in-game. Bid records, passive capture, alerts, and optional travel remain pending. Sweep automation and local housing capture are out of scope.

## Product specification

### Goals

- Track housing lottery application/results periods and per-character bids.
- Show crowdsourced available houses from PaissaDB (`https://paissadb.zhu.codes`).
- Alert on matching open plots and important bid/result deadlines.
- Use PaissaDB API as the source for available-house listings.
- Track placed bids per character.
- Keep any travel to wards/plots tentative and user-triggered through optional Lifestream integration.
- Do not capture ward data or maintain a historical housing inventory locally; replaceable PaissaDB snapshot caching is allowed. Do not automate sweeps or ward selection.

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
- Implemented filters: selected worlds, district, size, FC/personal eligibility, and interpreted phase; home-world/DC/all/clear shortcuts. Checkbox filters require a selected category match. Dedicated region, ward/plot-range, and price filters remain pending.
- Watch rules alert on matching plots. Acknowledge/Seen suppresses repeats until plot is observed closed and reopens.
- Refresh on a configurable interval and on demand. Use bounded requests, timeout, backoff, and cache last successful response.
- Never present API failure or missing fields as “no houses”; show unavailable/stale state and retain last successful data with its timestamp.
- PaissaDB API is the sole source for housing listings. Persist a replaceable last-successful API snapshot cache for startup continuity, not a historical/local housing inventory. Do not capture ward data.

### Travel actions (tentative)

- Optional, user-triggered Lifestream travel from available-house and bid rows to a selected ward/plot may be explored later.
- No automatic ward selection, review-tab cycling, sweep, local ward-data capture, bidding, claiming, or refund collection.
- Treat travel as a separate integration feature; keep core API browsing and bid tracking usable when Lifestream is absent.
- Validate Lifestream IPC, readiness/busy/error signals, cancellation, and destination parsing before implementation. Do not assume undocumented contracts.

### Notifications and travel actions

- Chat and toast notifications are configurable; optional sound is off by default.
- Optional, user-triggered Lifestream travel actions from bid rows and available-house listings remain tentative.
- Hide or disable integration actions when the relevant plugin is missing or busy. Never make optional integrations a requirement for core tracking.

### Main UI

1. **Dashboard:** current lottery phase and countdown, next transitions, upcoming period dates, pending bids, reminders.
2. **Available Houses:** PaissaDB listings, filters, sorting, source freshness, optional travel action.
3. **My Bids:** per-character bid history and outcomes; add/edit/recover, travel, character exclude/remove.
4. **Alerts:** watch rules, matching plots, acknowledge/Seen, refresh controls.
5. **Settings:** refresh/notification preferences, column visibility/order, integration status, character management, diagnostics.

### Architecture direction

- `Plugin.cs`: Dalamud entry point, commands, lifecycle, composition root.
- `Data/`: configuration, bid and watch-rule models, persistence, migrations.
- `Lottery/`: pure schedule/cycle policy and bid state transitions.
- `Paissa/`: typed API client, DTOs, cache, error/staleness state.
- `Game/`: English chat/status/placard observation boundaries; framework-thread access only.
- `Integrations/`: narrow optional IPC clients, initially Lifestream only if tentative travel is approved.
- `Windows/`: dashboard, houses, bids, alerts, settings.
- Keep schedule and filtering logic pure and testable. Keep native/game/UI/IPC reads on Dalamud framework updates, not ImGui rendering or arbitrary background tasks.
- Use `IPluginLog`; log state transitions and failures without exposing character Content IDs.

## Phased implementation plan

### Phase 1 — Foundation and working UI

**Status:** Implemented; Release build passes. In-game acceptance checks remain pending.

**Deliverables**

- Main window with Dashboard, Houses, Bids, Alerts, and Settings tabs.
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

**Status:** Implemented; Release build and schedule tests pass. User validated schedule and dashboard in-game; visual polish deferred.

**Deliverables**

- Pure UTC schedule service for the nine-day cycle: five-day application, four-day results.
- Initial application-start anchor: **2026-11-01 15:00 UTC**, subject to verification against the [Xenoveritas timer](https://xenoveritas.org/static/ffxiv/timer.html).
- Dashboard shows current phase/explanation, live countdown, start/end, and next occurrence.
- Local timezone display with UTC option; schedule calculations work offline.
- Explicitly distinguish calculated schedule dates from game-observed per-bid deadlines.

**Tests**

- Exact phase boundaries, cycle rollover, dates before anchor, timezone conversion, and daylight-saving transitions.
- Countdown refresh after system clock adjustment and plugin reload.

### Phase 3 — PaissaDB house browser

**Status:** Multi-world browser, saved filter preferences, restart-safe API snapshot cache, and browser hardening implemented. User confirmed hardening works in-game on 2026-10-29; individual checklist results were not recorded. Dedicated region, ward/plot-range, and price filters remain pending.

**Implemented browser behavior**

- Read-only API diagnostics, typed parsing, one-based display addresses, retained last-successful snapshots, paced/cancellable refreshes, and failure/partial-response protection.
- Multi-world selection with home-world, data-center, all, and clear shortcuts; saved district/size/eligibility/phase filters and sortable results.
- Entry, Results, Unavailable, and Unknown use one typed classification for labels, filtering, and sorting.
- Expired Entry becomes Results only during the matching cycle’s Results period; its displayed close becomes that Results-period end. Original API phase/deadline remain unchanged and available in tooltips.
- Source freshness is separate: observations two or more phases old are Stale; invalid/missing timestamps have Unknown freshness. Cache download age is not source age.
- Effective Closes sorting and live countdowns use the same cached schedule time as phase interpretation. Unknown deadlines/counts sort last in either direction.
- Character changes select the new character’s home world, including same-world alts. Same-character manual choices survive framework updates and reloads. Selection works before directory download and saves once per character transition.
- Shared custom palette, UTC/local display preference, and right-aligned result summary implemented.

**Hardening verification**

- Verification on 2026-10-29: Release build passed with zero warnings/errors; 51/51 offline tests passed; `git diff --check` passed.
- Offline regression tests cover phase/filter boundaries, effective deadlines/countdowns, freshness boundaries, sorting, character-selection policy, and configuration reset/migration.
- In-game validation: user confirmed the patch works on 2026-10-29. Detailed regression checklist (individual results not recorded): Entry-only filtering; Phase/Closes sorting both directions; inferred Results deadline; hovered countdown; UTC/local toggle; different-world and same-world character switches; switch while window closed; manual-selection persistence; plugin reload and delayed directory load.
- Offline tests do not validate ImGui rendering, native character-state reads, or live Dalamud lifecycle behavior.

**Deliverables**

- Begin with explicit, read-only diagnostics for `/worlds`, `/worlds/{worldId}`, and `/openapi.json`; use live responses to confirm the browser's fields and unknown/null behavior.
- Typed client verified against live `https://paissadb.zhu.codes/openapi.json` and API responses.
- Query world/district listing data; centralize conversion from zero-based API ward/plot numbering.
- Show address, size, price, purchase restrictions, lottery phase, entrant count when reported, and source freshness.
- Filters for region/DC/world/district/ward/plot ranges/size/price/purchase type, sorting, manual refresh.
- Bounded concurrency, cancellation, timeouts, backoff, and last-successful temporary response cache.

**Rules and acceptance**

- PaissaDB API is authoritative for listings; no local ward-data capture or historical housing inventory. Disk cache contains replaceable API snapshots only.
- Unknown entrant count is not zero. API failure is not “no houses.”
- Cached listings remain visible with source freshness and separate download timestamp; downloading old observations does not make them fresh.
- API errors and partial responses are visible; opening UI does not cause request bursts.

### Phase 4 — Character management and bid records

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

### Phase 5 — Passive bid capture and results

**Deliverables**

- Capture English bid-confirmation messages passively.
- Recover existing bids from Housing Lottery Status and enrich a known bid from its placard.
- Unrelated placard browsing never creates bid records.
- Outcomes: Pending, Won, Lost, Claimed. Capture results and claim reminders when evidence supports them.
- Game-observed deadlines override calculated estimates; ticket number remains editable if unavailable.

**Tests and acceptance**

- Duplicate messages/placard visits, missing ticket/deadline, different characters/cycles, and mismatched plot/world.
- Results becoming available without an observed outcome.
- No false Won/Lost transitions from generic placard text; results-period start alone never marks a bid Lost.

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

### Phase 7 — Integration validation (tentative travel)

| Integration | Required for |
| --- | --- |
| Lifestream | Optional user-triggered travel to a ward/plot from bid/listing rows |

**Deliverables**

- Verify Lifestream IPC contract, readiness/busy/error signals, cancellation, and destination semantics; do not assume undocumented APIs.
- Implement narrow optional integration client and show dependency status in UI only if travel is approved.
- Verify destination behavior and safety before enabling travel actions.

**Acceptance / decision gate**

- Missing Lifestream disables only travel actions; browsing and bid tracking remain usable.
- Travel remains user-triggered; no ward-menu automation or sweep behavior is added.

### Phase 8 — Release hardening

**Deliverables**

- Tests for schedule, API parsing/filtering, bid identity/state, alert deduplication, persistence, and optional travel boundaries using fake clock/game/provider seams.
- In-game checklist for API failure/staleness, bid capture, missing-plugin scenarios, and optional travel behavior.
- Dependency/setup documentation, source attribution, license review, automation disclosure.
- Redact character Content IDs from logs; verify clean install and release packaging.

**Acceptance**

- Release build and offline tests pass; in-game validation is recorded separately.
- Optional dependencies degrade gracefully; no automatic bidding, claiming, or refund collection.

### Delivery checkpoints

1. **Housing discovery:** phase 3.
2. **Bid tracking MVP:** phases 4–5.
3. **Alerts and optional travel validation:** phases 6–7.
4. **Release candidate:** phase 8.

### Licensing and attribution

- HousingLottoTracker is a feature reference, not a code dependency. This project will implement equivalent user-facing bid-tracking behavior independently unless reuse permission and a suitable license are verified.
- PaissaHouse is an optional reference only; PlotWatch does not depend on it or automate contribution sweeps.
- PaissaWah is AGPLv3-licensed; use as a feature reference only unless AGPL obligations are intentionally accepted.
- Attribute PaissaDB as the source of crowdsourced listing data and link users to its site/API documentation.

## Build

Requires .NET 10 SDK and Dalamud API 15 development files.

From `/mnt/d/Coding/C#/PlotWatch`, build with installed Windows .NET SDK:

```sh
"/mnt/c/Program Files/dotnet/dotnet.exe" build PlotWatch/PlotWatch.csproj -c Release
```

The initial scaffold registers `/plotwatch` and `/pw`; Phases 3–8 remain pending implementation.
