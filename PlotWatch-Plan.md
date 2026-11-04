# PlotWatch — Product Specification and Implementation Plan

Dalamud plugin for FFXIV housing lottery tracking, available-plot discovery, alerts, and bid records.

Commands: `/plotwatch` and `/pw`.

> **Status:** Phases 1–2 implemented; Phase 3 multi-world PaissaDB browser and hardening implemented. Phase 4 character management/manual bid records implemented; user validated manual add/edit and reload persistence, with remaining acceptance checks pending. Release build and offline verification results are recorded separately from in-game acceptance. Phase 2 schedule/dashboard and Phase 3 hardening validated in-game. Phase 5 read-only capture diagnostics implemented; automatic capture is not implemented. Optional Lifestream travel implemented; live acceptance pending. Alerts remain pending. Sweep automation and local housing capture are out of scope.

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
- English-first UI for initial release. Prefer verified message/sheet identity plus numeric SeString parameters for capture, avoiding localized rendered-text matching. Whether the housing custom-sheet confirmation is exposed through a suitable event remains unverified; English fallback requires explicit approval and validation.
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

### Travel actions

- Optional, user-triggered Lifestream travel from available-house and bid rows to a selected ward/plot is implemented; live acceptance remains pending.
- No automatic ward selection, review-tab cycling, sweep, local ward-data capture, bidding, claiming, or refund collection.
- Treat travel as a separate integration feature; keep core API browsing and bid tracking usable when Lifestream is absent.
- Validate Lifestream IPC, readiness/busy/error signals, cancellation, and destination parsing before implementation. Do not assume undocumented contracts.

### Notifications and travel actions

- Chat and toast notifications are configurable; optional sound is off by default.
- Optional, user-triggered Lifestream travel actions from bid rows and available-house listings are implemented; live acceptance remains pending.
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

**2026-11-04 cache hitch / truncated-response patch — implemented, live performance verification pending**

- User logs showed a 62.9 ms OnFrameworkUpdate hitch near world acceptance and a 37 ms synchronous save of the full 12 MB / 35,332-listing cache. Accepted worlds previously serialized/replaced the entire cache on the framework thread.
- `Paissa/HousingCacheWriter.cs` replaces synchronous full-cache saves with a single background writer. Framework acceptance copies only world snapshot references; response listing/warning collections are frozen before publication, and restored cache arrays are wrapped read-only. Serialization streams directly to a unique temporary file, flushes, then atomically replaces the previous cache. Existing JSON layout remains readable; failures retain the prior disk cache and current in-memory data.
- Pending saves coalesce over a fixed 30-second window (new requests do not indefinitely reset it). Only the latest pending generation is retained. Writes never overlap within the writer; a newer pending snapshot is written after any in-flight older save. Batch completion, including cancellation, requests an immediate asynchronous flush; framework updates do not await disk I/O. Results/timing are queued back for framework-side diagnostic logging.
- Unload rejects further save requests and asks the managed worker to drain latest pending data without waiting on the framework or accessing game/UI objects. As with other background cache work, abrupt process termination may lose the latest unsaved refresh interval; bids/watchlist persistence is unchanged. Failed saves retry on a later request/explicit flush, not a busy loop.
- World downloads get at most two attempts total across existing HTTP 429 and newly recognized truncated-response retries. Retries enter the same concurrency/pacing/shared Retry-After gates. ResponseEnded is classified by HttpRequestException/HttpIOException error codes (including wrapped causes), not English error-message matching. Short declared Content-Length is rejected before parsing. Cancellation, unrelated IO, invalid JSON, response-size limits, and ordinary HTTP errors are not broadly retried. Failed/rejected responses never replace prior accepted world data.
- Verification: 166/166 tests pass; Release build/diff check clean. Tests cover coalescing/flush, off-caller-thread persistence, in-flight serialization/order, failures/retry, nonblocking completion/drain, JSON compatibility/null/zero values, atomic file-failure cleanup, empty flush, bounded transport retries, pacing/cooldown, cancellation, short bodies and stream failure. They do not establish actual game-frame timings or eliminate every other possible hitch source. Next live refresh should log background cache saves rather than synchronous per-world saves.

**Shared dashboard watchlist — implemented 2026-11-01; live acceptance pending**

- Right-click a Housing plot number for Travel to plot and Track plot / Untrack plot. No new Housing row buttons. A `*` identifies tracked plots and has a context tooltip. Shared address identity is world ID + district ID + one-based ward/plot; invalid/inconsistent addresses are rejected and duplicate adds are idempotent.
- Dashboard Tracked plots section follows the lottery overview and bid summary. Displays address, size/price, entrants (null remains Not reported, zero remains zero), reported status, and world cache download age; Travel to and Untrack are explicit actions. Lifestream availability/busy/destination checks and queued travel are reused; untracking never aborts travel. No bid record or outcome is attached to watching.
- Separate `watched-plots.json` v1 stores shared addresses, display world names, UTC tracking times, and last-known API listing snapshots with original source/download timestamps. No character keys. Settings resets, character switches, and cycle rollover do not clear the list. Bid/settings/cache files are not rewritten by track/untrack.
- `WatchedPlotFileStore` follows bid-store safety patterns: version validation, atomic temporary writes/flush/replace, prior-save `.bak`, explicit backup restore preserving unreadable originals, read-only fallback, future-schema protection, and temporary-file cleanup. Save-before-publish prevents failed track/untrack/detail writes from changing displayed watchlist state. Dashboard exposes errors and confirmed backup recovery; failures remain retryable.
- Startup cache snapshots are propagated immediately to the window, independent of opening Housing or directory-request success. Last-known details update in one write per accepted watched-world snapshot (or explicit backup recovery), never once per rendered frame. Tracking causes no HTTP requests. Existing paced all-world refresh already covers tracked worlds independently of selected Housing filters.
- Resolution: matching snapshot → current cached listing; successful world snapshot lacking address → Not in latest listings with last-known details; no loaded world snapshot → Waiting for cached data. Cache older than saved details → Waiting for newer cached data, never overwrite newer history. Missing listings never imply sold/claimed or remove records. Failed/rejected world refreshes keep previous accepted snapshots and historical details; refresh errors are shown in status tooltips.
- Verification: 139/139 offline tests pass; Release build has zero warnings/errors. New tests cover address validation, duplicate/removal identity, shared schema and independent files, exact cache matching, present/missing/no-cache states, null/zero preservation, batched updates, failed/older refresh, persistence/backups/recovery, corruption/future-schema protection, and write/restore failures. Offline tests do not validate ImGui interaction, real character-switch lifecycle, or actual Lifestream movement.
- Pending in-game: track/untrack via Housing menu, immediate Dashboard update, restart persistence, another character sees same list, travel/disabled reasons, safe untrack during travel, missing-listing/failed-refresh tooltips, settings reset preservation, and no network burst on tracking.
- User found the plot context menu opening at screen top. Patch captures mouse position on right-button release before tooltip submission and explicitly anchors popup on appearance; menu notes wrap at a bounded scaled width. Housing and bid-address context menus share the helper. Release build/diff check pass; actual popup positioning still needs in-game retest.
- Dashboard tracked plots now group by server under collapsible `Server (count)` headers, sorted by world name then ID; plots within each server sort by district/ward/plot. Group identity uses world ID, not display name. Existing details and Travel to/Untrack actions remain inside each expanded group; a bounded outer child scrolls the server list vertically.
- Configuration v11 adds collapsed watched-server IDs, persisted only when a header is toggled. New servers default expanded. Json.NET replaces the list on load; normalization repairs null/invalid/duplicate IDs. Existing v10 period/status/filter preferences remain unchanged; settings reset expands all servers without deleting watched plots.
- Grouping verification: 142/142 offline tests pass and Release build/diff check are clean. Tests cover group counts/order, current cached names and equal-name/different-ID separation, collapsed-state reload/defaults/normalization/reset. In-game collapse/expand, travel/removal within groups, scrolling, and reload persistence remain pending.
- Tracked tables now have clickable single-column sorting independently per server: Address (district/ward/plot), Size / price (size then price), Entrants, Reported status (same typed API classification as Housing), and Cache age (youngest first ascending). Actions is not sortable. Unknown keys remain last in either direction, ties use stable address order, and missing listings never sort as their historical bidding status. Numeric details sort the same current/last-known values the table displays; watch/bid records and server collapse state are untouched. ImGui table sort specs use explicit stable column IDs.
- Sorting verification: 147/147 offline tests pass; Release build/diff check clean. New tests cover both address directions, composite size/price/null order, zero/missing entrant counts, known/unrecognized/historical status order, cache age/missing downloads, and stable ties. Header interaction and per-server sort behavior still require in-game checks.
- 2026-11-02 UI change: server dropdown buttons replace inline collapsible headers/panel. Each opens its tracked table in a popup anchored below the button, bounded/clamped to the viewport with scrolling for long/wide tables. Existing sort headers, detail tooltips, Travel to and Untrack remain; removing the last plot for a server closes its popup. Click outside/Escape dismisses it. Watchlist/bid/cache data are unchanged; legacy collapsed-server preferences are retained but no longer used by this presentation. Release build/diff check pass; dropdown positioning, sorting, scrolling and action behavior require live retest.
- Tracked hover tooltip patch: premeasure wrapped tooltip content and set size/position before BeginTooltip, near the current cursor from the first frame. Flip left/above and clamp to viewport margins using current content size, not previous-frame window dimensions. Applied to server-button hints, tracked address/count/status/cache-age fields, and disabled travel reasons. Server dropdown positioning is unchanged. Verification: 151/151 tests pass, Release build/diff check clean; tests cover placement math/edges/negative viewport coordinates/content changes, not actual ImGui rendering. Live retest needed for the reported first-frame screen-top flash.
- Tooltip placement now uses shared `Windows/PlotWatchTooltips.cs` across all explicit PW tooltips, including Available Houses period/status/end/entrants/source observation, cache-refresh hints, bid details, travel stop hints, and the diagnostic title-bar button. All content is assembled before first-frame measurement/placement. No direct SetTooltip/BeginTooltip calls remain outside the helper. Verification remains 151/151 offline tests, clean Release build/diff check; live hover rendering still needs verification. Popup/dropdown placement is unchanged.
- Housing Lottery Details now opens a dropdown popup beneath its button instead of expanding content below the dashboard. Application/Results occurrence details, timezone-aware timestamps, schedule explanation, and source link remain inside; it consumes the existing cached schedule snapshot. Popup width/height are bounded to the viewport and positioning is clamped. Click outside/Escape closes it. Release build/diff check pass; UI placement/dismissal needs live verification.

**Status:** Multi-world browser, saved filter preferences, restart-safe API snapshot cache, and browser hardening implemented. User confirmed hardening works in-game on 2026-10-29; individual checklist results were not recorded. Dedicated region, ward/plot-range, and price filters remain pending.

**Implemented browser behavior**

- Read-only API diagnostics, typed parsing, one-based display addresses, retained last-successful snapshots, paced/cancellable refreshes, and failure/partial-response protection.
- Multi-world selection with home-world, data-center, all, and clear shortcuts; saved district/size/eligibility/phase filters and sortable results.
- Recognized lottery purchase flags (1, 3, 5, 7) use the global schedule for Entry/Results and the active phase deadline, regardless of missing, expired, or Closed API reports. This supersedes the earlier per-plot Entry-to-Results inference.
- Recognized non-lottery flags (0, 2, 4, 6) show Not applicable; missing/unrecognized flags show Unknown, with no period end. Period filters are Entry, Results, and Unknown / Not applicable. Original API phase/deadline remain unchanged in cache and available in tooltips/diagnostics.
- Configuration version 9 migrates legacy phase selections once: Entry/Results preserved, Unavailable mapped to both, Unknown preserved. New/default/reset masks select all three categories.
- Last observed replaces Updated. Ward sweeps can refresh vacancy observations without refreshing lottery details; global phase never guarantees continued vacancy. Entrants remain reported values or Unknown, never fabricated zero or reset on rollover.
- Global Period is distinct from PaissaDB Reported status: null/missing → Not reported, 1 → Accepting entries, 2 → Results, 3 → Unavailable, other → Unrecognized. Global Entry never substitutes for missing purchase status. Entrant nulls display Not reported independently; zero remains zero.
- Report deadlines at or before the shared schedule time mark the reported status Outdated; no inferred replacement status. Missing/invalid deadlines retain status with timing unverified in tooltip. Future deadlines mean not expired, not proof of recent lottery observation. Missing status stays Not reported even if a raw deadline exists.
- Browser columns: Period, Reported status, Entries, Period ends, Last observed. Period ends/countdown remain global calculations, explicitly not verified bidding deadlines for an individual plot. Status tooltip retains raw phase/deadline (including Unix seconds), source observation/download time, and sweep-freshness caveat. Column IDs 0–10 retain existing meaning; reported-status sort uses new ID 11 with missing/unrecognized statuses last in both directions.
- Configuration version 10 adds independent status-filter bits 1/2/4/8 (Accepting entries/Results/Unavailable/Not reported or Unrecognized) and Include outdated reports. Migration enables all new status choices/outdated reports once, preserving existing period/world/district/size/eligibility/time preferences. Reload preserves choices, including empty selection; Reset Filters/default reset restore all status choices. Both period and status filters must match.
- Source freshness is separate: observations two or more phases old are Stale; invalid/missing timestamps have Unknown freshness. Cache download age is not source age.
- Effective Closes sorting and live countdowns use the same cached schedule time as phase interpretation. Unknown deadlines/counts sort last in either direction.
- Character changes select the new character’s home world, including same-world alts. Same-character manual choices survive framework updates and reloads. Selection works before directory download and saves once per character transition.
- Shared custom palette, UTC/local display preference, and right-aligned result summary implemented.

**Hardening verification**

- Verification on 2026-10-29: Release build passed with zero warnings/errors; 51/51 offline tests passed; `git diff --check` passed.
- Offline regression tests cover phase/filter boundaries, effective deadlines/countdowns, freshness boundaries, sorting, character-selection policy, and configuration reset/migration.
- In-game validation: user confirmed the patch works on 2026-10-29. Detailed regression checklist (individual results not recorded): Entry-only filtering; Phase/Closes sorting both directions; inferred Results deadline; hovered countdown; UTC/local toggle; different-world and same-world character switches; switch while window closed; manual-selection persistence; plugin reload and delayed directory load.
- Offline tests do not validate ImGui rendering, native character-state reads, or live Dalamud lifecycle behavior.
- Global-phase verification on 2026-11-01: Release build passed with zero warnings/errors; 97/97 offline tests passed. Coverage includes all recognized purchase flags, missing/unrecognized flags, raw Closed/null phases, exact boundaries/rollover, deadline/countdown/sorting consistency, unchanged reports/counts, and idempotent filter migration. Main/debug windows consume the shared cached schedule snapshot.
- Pending in-game global-phase acceptance: Midgardsormr → Mist → Ward 5 Plot 11 should show Entry, closing 2026-11-06 15:00 UTC (or local equivalent), and Unknown entrants without opening its placard. Check filter migration, Phase/Closes sorting, tooltips, and UTC/local display.
- 2026-11-01 reported-status patch supersedes that acceptance wording: latest Midgardsormr Mist W5 P11 reports Entry / Accepting entries / 0 entrants; its earlier null payload should show Entry / Not reported / Not reported entrants. Faerie Goblet W25 P54 reports Entry / Unavailable / 0 entrants, global period end Nov 6 15:00 UTC, raw report deadline Nov 10 15:00 UTC. User's placard log agrees with unavailable status; global Entry must not override it. Live table/filter/migration acceptance remains pending.
- Reported-status patch verification: Release build passed with zero warnings/errors; 123/123 offline tests passed. Tests cover classifications, global/API disagreements, null/zero preservation and unchanged reports, exact expiry/offset boundaries, missing/invalid deadlines, combined filters, stable sort IDs/unknown-last ordering, v7–v9 migration to v10, reload/idempotence/reset. API fetching/uploads/cache payloads and native bid probes are unchanged by this patch.

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

**Status:** Implemented; latest Release build and 75/75 offline tests pass, including bid-entry logging coverage. User confirmed manual bid entry works and that editing keeps one updated record which survives reload. Remaining UI/character-lifecycle and recovery checks remain pending.

**Implementation notes**

- Framework-thread character discovery runs at a monotonic one-second cadence. Tracks name/home world and stable identity without exposing Content IDs in UI/logs. Same-character metadata updates do not create duplicate characters.
- My Bids provides manual add/edit, active/history/current-character filters, stable cycle/address sorting, explicit outcomes, deliberate duplicate override, and confirmed deletion.
- Unknown size/ticket/entry/cycle remain unknown. A cycle reference enables calculated Results/claim estimates; manual or game-observed deadline fields override estimates. Existing game-observed dates take precedence over manual edits.
- Character controls appear in My Bids and Settings. Exclusion preserves records and prevents discovery/new bids; existing records remain editable. Removal deletes associated records with confirmation. Removal alone permits rediscovery on next login; remove-and-exclude suppresses it until explicitly re-enabled.
- `bids.json` is separate from settings/API cache: versioned schema, atomic temporary-file replacement, previous-save `.bak`, save-before-publish mutation, and visible read-only recovery after corrupt/newer-file loads.
- Recovery from a valid backup requires confirmation, preserves the unreadable primary in a uniquely named file, and retains the good backup. Unsupported newer schema blocks downgrade recovery; preserve files and update the plugin instead.
- Dashboard shows pending/won-unclaimed counts and up to five Results/claim dates with provenance and countdowns. Reaching dates never changes outcome or generates notifications.
- Legacy schema version 0 uses the same record shape and migrates in memory to version 1; loading does not rewrite the file.
- Bid-entry additions/edits log at Debug after persistence succeeds. Validation, duplicate, read-only, and save failures log at Warning. Logs include plot/type/cycle/ticket/outcome/deadlines and never character Content IDs.
- Delete-record dialog was changed to force viewport-center positioning every frame and disable saved positioning after appearance-only centering did not work for the user. Final centering behavior still needs explicit in-game confirmation.

**Offline verification**

- Tests cover discovery, exclusion/removal/rediscovery, manual validation, duplicate override, explicit outcomes, deadline precedence, character switching, schema migration, restart persistence, backup recovery, corruption/newer-schema protection, and save/restore failures.
- Offline tests do not cover native character-state reads, ImGui dialogs, or live plugin lifecycle.

**In-game acceptance**

- Confirmed by user: manual entry; district edit from Mist (339) to Lavender Beds (340); exactly one updated record; updated data survives reload. Corresponding added/updated Debug logs were supplied.
- Example test record: world 99, ward 1, plot 1, FC, unknown ticket, Pending, cycle -1. Calculated results 2026-10-28 15:00 UTC and claim deadline 2026-11-01 15:00 UTC match the schedule. Negative cycle IDs are valid relative to the 2026-11-01 anchor.
- Remaining checks below are not claimed as passed:

- Log in/switch characters, including same-world alts; inspect registry and current-character filter.
- Add a manual bid; verify world/district/one-based address/type/ticket and UTC input instructions. Enter historical or unknown dates; validate input errors.
- Edit record/outcome; confirm duplicate warning and deliberate override; cancel and confirm record deletion.
- Verify active/history filtering, calculated/manual date labels, dashboard countdowns, and UTC/local display preference.
- Exclude/re-enable; remove/cancel removal; confirm remove-only rediscovery on next login and remove-and-exclude behavior.
- Reload plugin and confirm records/exclusions persist. Validate backup recovery using copied test files only, not the sole copy of real bid history.

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

**Status:** Source and installed-game investigation, read-only Housing Capture diagnostics, and explicitly opt-in native success probe implemented; automatic capture is not implemented. Latest verification: Release build and 118/118 offline tests pass. User reports validate templates, Timers, and rendered placard count/deadline gap patch. Native probe compatibility/negative controls and actual successful-entry mapping require live verification.

#### User decisions and investigation boundaries

- Closely follow Housing Lotto Tracker's capture method, but inspect actual source rather than treating README/comments as authoritative. Do not silently reproduce assumptions; ask when evidence or behavior is ambiguous.
- User confirmed English client and month/day/year displayed dates. This is a fallback parsing preference, not proof of how every template formats dates.
- FC/Personal capture must be confirmed later when bidding opens. Until evidence is explicit, keep purchase type Unknown; do not default to Personal.
- Stay passive: no bidding, claiming, refunds, travel, or other automated game actions.
- Reference inspection and SqPack reads were read-only. No game files changed.

#### Authoritative source locations

- Knowledge base: `D:\Coding\C#\ffxiv-knowledge` (`/mnt/d/Coding/C#/ffxiv-knowledge`). Start with `knowledge/INDEX.md`; checked-out source wins over notes.
- Housing Lotto Tracker: `reference-plugins/HousingLottoTracker`, upstream `https://github.com/dexcss/HousingLottoTracker`, inspected commit `eaefb43d5be2044b8ebe60081e2cd3365667b4a8`. Added as a root submodule; nested dependencies were not initialized.
- HLT navigation note: `knowledge/repositories/housing-lotto-tracker.md`.
- AOCCH reference: `D:\Coding\C#\Another_Occult_Crescent_Helper` (`/mnt/d/Coding/C#/Another_Occult_Crescent_Helper`). Relevant implementation: `AOCCH/Automation/TreasureHintTracker.cs`; additional ID-based classification in `AOCCH/Shopping/ShopPurchaseController.cs`.
- Installed game: `D:\FINAL FANTASY XIV - A Realm Reborn` (`/mnt/d/FINAL FANTASY XIV - A Realm Reborn`). Actual Excel sheets are packed in `game/sqpack/ffxiv/0a0000.win32.dat0`, indexed by the corresponding `.index`/`.index2` files.
- Lumina is the reader library, not a distribution of the game sheets. The local game archives contain original EXH/EXD data and binary SeStrings.
- CSV navigation exports: `external/ffxiv-datamining/csv/{en,de,fr,ja}/custom/001/CmnDefHousingSignboard_00135.csv` and `csv/en/Addon.csv`. Note lowercase **b** in `Signboard`; an earlier case-sensitive search missed it.
- CSV exports render placeholders as UNKNOWN/sample values and may show only one conditional branch. They are not lossless SeString payload dumps. Raw installed-game data was inspected to verify branches and parameters below.

#### HLT's actual capture method

Paths in this subsection are relative to `reference-plugins/HousingLottoTracker/HousingLottoTracker/`.

1. **Chat creates entries:** `Plugin.cs:458–491` subscribes to `IChatGui.ChatMessage`, reads rendered text, parses through `Game/ChatLotteryParser.cs`, then calls `Data/BidStore.CaptureFromChat`. Chat supplies address/ticket/results text; a cached last-viewed placard is not required for its normal capture path. Plot size comes from the `HousingLandSet` sheet using territory and plot.
2. **Status recovers entries:** `Plugin.Poll` runs approximately every two seconds. `Game/PlacardReader.FindLotteryStatusText` scans loaded visible addons for lottery-status text; `Game/LotteryStatusParser.cs` parses address, optional size, ticket, and type; `Data/BidStore.CaptureFromStatus` creates/updates a record.
3. **Placards enrich:** `Game/PlacardSaleHook.cs` reads structured sale information and calls `Data/BidStore.CaptureFromSaleInfo`, which only updates existing records. `Game/PlacardReader.cs` provides a text-scrape fallback. Contrary to the simplified README description, the scrape path can create a record when it finds an entry number or win hint.
4. **Matching:** HLT keys by character Content ID, territory, ward, plot, and cycle. World and purchase type are absent from its key. It picks the first matching record and can overwrite a conflicting ticket; repeated status/placard observations can persist repeatedly rather than perform no-op deduplication.
5. **Outcomes:** HLT listens to `SelectYesno` for broad win wording and picks the first pending current-cycle record for the character. Its periodic tick also marks old pending records Expired. These outcome behaviors are separate from entry capture and must not be imported as part of the initial capture patch.

**Assumptions/risks found in HLT (not approved PlotWatch behavior)**

- Chat parser does not validate channel/sender and matches an entry substring rather than requiring a complete successful-confirmation envelope.
- Missing FC wording defaults to Personal. Status parser likewise represents missing/unknown type as false/Personal. Plot tenant eligibility is not independently equivalent to the type of bid actually placed, especially on mixed-eligibility plots.
- Character context uses home world; no independently established destination world is supplied by the confirmation text. Do not relabel home/current world without evidence.
- Unrecognized district can fall back to current territory.
- Dates try month/day before day/month, use OS-local conversion, and have a loose parsing fallback. Numeric timestamps would avoid these language/date-order ambiguities if exposed.
- Historical status recovery assigns observation time as entry time. PlotWatch must keep actual historical entry time Unknown unless observed independently.
- Status parser requires a numeric ticket. README says the game does not retain the ticket, while status parser expects one; live availability must be verified, not assumed.
- Placard hook uses a signature plus function-start offset and raw struct offsets. Successful installation does not prove current-patch correctness. It treats phase-end as results-start even during Results; raw Addon row 7044 confirms phase-specific deadline meaning.
- No license file was found in the inspected HLT checkout. Independently implement the method unless permission/license for copying implementation is established.
- HLT targets Dalamud API 15/.NET 10, matching PlotWatch. API compatibility does not validate live game memory layouts.

#### Verified raw custom-sheet confirmation

Virtual game files:

- `exd/custom/001/cmndefhousingsignboard_00135.exh`
- `exd/custom/001/cmndefhousingsignboard_00135_0_en.exd`

The header confirms 11 rows, two string columns. Column 0 is the symbolic message key; Column 1 is the message SeString.

| Row | Key suffix | Meaning |
| --- | --- | --- |
| 6 | `LAND_LOTTERY_LOTTED` | Successful entry: “You have submitted a lottery entry for plot …, ward …, …. Your lottery number is …. Results will be available from ….” |
| 7 | `LAND_LOTTERY_LOTTED_2` | Instructions to inspect Estate → Timers under Duty |
| 8 | `LAND_LOTTERY_REFUND_FULL_GIL` | Cannot claim refund because player cannot carry more gil |
| 9 | `LAND_LOTTERY_GUIDE` | Already entered a different plot; shows pending-entry address, not evidence of a new entry |
| 10 | `LAND_LOTTERY_GUIDE_OK` | Accept label |

Raw row 6 parameter references:

| SeString local number | Meaning established from template |
| --- | --- |
| `lnum1` | Territory ID, resolved through `TerritoryType` → `PlaceName` |
| `lnum2` | Ward as displayed by the message |
| `lnum3` | Plot as displayed by the message |
| `lnum4` | Player's lottery ticket number |
| `lnum5` | Input to results-date/time formatting; runtime value/unit must still be verified |

Row 6 includes conditional time/date formatting expressions. Do not parse the CSV sample timestamp as real data. Do not assume template local-number numbering equals a zero-based event-parameter array without checking the relevant runtime API.

The user supplied row 9's expanded text: “You have submitted a lottery entry for a different plot. Please confirm the status of your lottery entry by accessing the same placard by which you entered the lottery.” Followed by `Pending Entry`, plot `lnum3`, ward `lnum2`, and district resolved from territory `lnum1`. This may be recovery evidence, but provides no ticket/type/deadline and must never be interpreted as a successful new bid.

#### Verified Addon sheet messages

Read original SeStrings from `exd/addon.exh` and `exd/addon_0_en.exd` in the installed game. Conditional alternatives below were verified in raw payloads, not inferred from one CSV rendering.

| Row | Message/branches | Use |
| --- | --- | --- |
| 7040 | Available / Unavailable to free companies. | Plot eligibility, not entry-type proof |
| 7041 | Available / Unavailable to private buyers. | Plot eligibility, not entry-type proof |
| 7043 | First-come-first-served / Selling via lottery. (Current participants: count) / Currently unavailable for purchase | Placard purchase/availability wording |
| 7044 | Accepting entries until datetime / Results period ends datetime | Deadline interpretation must follow observed phase |
| 7046 | Deposit-confirmation dialog: personal plot / free company plot / generic plot of land; separate eligibility branches for FC-only, Personal-only, or both | Potential explicit type context before confirmation; still needs runtime correlation/validation |
| 7122 | Winning number, congratulations, winner-of-this-lottery purchase/claim prompt; Plot available until datetime | Explicit win-prompt template; not proof purchase completed |
| 7123 | Claim forfeited; cancellation fee and remaining-refund prompt | Separate forfeiture/refund evidence, not automatic Lost/Claimed |
| 7124 | Winning number; better luck in future; full-refund prompt | Separate unsuccessful-entry evidence; accepting refund is a different action |
| 7126 | Winning number for this plot / no participants | Plot result, not by itself a character outcome |
| 7127 | View Lottery Results | Action label, not outcome evidence |
| 7128 | Sole entrant won by default; purchase/claim prompt and Plot available until datetime | Alternate explicit win prompt; not completed claim |
| 7129 | There was one participant in this lottery. | Participant-count wording |
| 8841 | Must be on Home World to retrieve data. | Status-availability constraint |
| 8843 | Housing Lottery Status | Status window title |
| 8844 | Entry period in progress. | Status phase label |
| 8845 | Results period in progress. | Status phase label |
| 8846 | Current Entry | Status section label |
| 8847 | Plot, ordinal ward, district, and size (Small/Medium/Large branches) | Status address/size |
| 8848 | Lottery Number: number | Status ticket |
| 8849 | Type of Entry: Free Company Representative / Private Buyer | Both actual type branches present; live extraction remains to be validated |

HLT's alternatives such as “results are in”, “entitled to purchase”, and “claim” + “winnings” were not established as specific housing templates. Broad words also occur in unrelated content; do not treat them as verified housing evidence.

#### AOCCH's localized ID/parameter approach

Verified in `AOCCH/Automation/TreasureHintTracker.cs`:

- Subscribes/unsubscribes `IChatGui.LogMessage`.
- Classifies `message.LogMessageId` against configured known IDs.
- Reads numeric arguments with `TryGetIntParameter(index, out value)` rather than matching localized rendered text. Examples include direction at index 0 and survey counts at indexes 0/1; these are **AOCCH-specific mappings**, not housing mappings.
- Diagnostic probe enumerates `ParameterCount`, integer/string parameters, `LogMessageId`, and `GameData.RowId`. Do not copy general string dumping into normal PlotWatch logging.
- Some AOCCH fallback logic infers unique plausible parameter pairs; PlotWatch should use verified housing mappings, not silently import those heuristics.

Verified in knowledge-base Dalamud/ClientStructs source:

- `platform/Dalamud/Dalamud/Game/Chat/LogMessage.cs`: `ILogMessage.GameData` is specifically a `RowRef<LogMessage>`; `LogMessageId` refers to that sheet, not arbitrary Addon/custom rows.
- `platform/Dalamud/Dalamud/Game/Gui/ChatGui.cs`: event is raised while iterating `RaptureLogModule.LogMessageQueue`.
- `platform/FFXIVClientStructs/FFXIVClientStructs/FFXIV/Client/UI/Misc/RaptureLogModule.cs`: queue is documented as messages from the LogMessage sheet. Separate printing APIs also exist.
- `ILogMessage.Parameters` is valid only during the callback. Copy required primitive values into an immutable observation; never retain native pointers or the event wrapper.
- `FormatLogMessageForDebugging()` can cause side effects such as sound playback; do not call it in normal capture.

**Critical unresolved boundary:** custom-sheet row 6 is **not** automatically `LogMessageId = 6`. Searches of the exported LogMessage sheet did not locate the housing successful-entry text. Determine whether housing uses the queued LogMessage event, another event that retains custom-sheet identity/parameters, or a separate native evaluation/printing path. Absence in CSV alone is not runtime proof.

#### Revised implementation checkpoints

**Implemented diagnostic patch (2026-10-30; in-game checks pending)**

- Debug window → **Housing Capture** has Inspect sheet templates, Arm 15-second probe, Stop, Clear results, and Copy diagnostic report. Probe options are session-only and do not change configuration/bid data. Run all checks does not implicitly arm it.
- `Diagnostics/HousingProbeState.cs` owns monotonic expiry, character-bound invalidation, immutable view snapshots, candidate-only correlation, report generation, and a 200-entry history. Verbose event output is limited to 400 events per probe; the stop/expiry entry reports rate-limit suppression. Clear stops the probe and clears history while retaining inspected templates.
- Default `Diagnostics/HousingCaptureDiagnostics.cs` observation uses standard ChatMessage/LogMessage and addon PostSetup/PreFinalize events. Disposal unsubscribes. Native probing is a separately opted-in extension described below. No SeString evaluation, game callbacks, chat suppression, actions, or bid-store access is introduced.
- All sheet/native reads occur on the framework thread. Templates use the current client's sheet language without English fallback; raw macros, nested literal-branch prefixes, payload structure, and local-number references are reported. Local references are NOT assumed to be zero-based event indexes.
- Current character availability, home/current world IDs, territory, and language are sampled outside rendering. Private character identity only invalidates the probe and is never exported. Logout/character switch stops probing, including same-world alts; territory/world changes invalidate cached addon snapshots.
- Default LogMessage observation is metadata-only for the previously located housing-related LogMessage rows 3497/6099. **All LogMessage IDs** opt-in records arbitrary IDs/counts with values withheld; it does not identify them as housing events. To inspect numeric arguments for a discovered LogMessage ID, enter that ID and re-arm. Only up to 32 integer arguments are exposed for that explicitly selected ID; strings/entities/addresses remain withheld. Never enter custom-sheet row 6 merely because successful entry uses that row.
- Chat candidate inspection is limited to SystemMessage/SystemError/Notice/Urgent channels; player chat and Echo are ignored. Rendered text is compared with localized static template evidence for diagnostic correlation only. No automatic capture/parser is enabled. Raw chat is withheld; reports contain candidate template identities, static evidence and bounded short numeric text tokens, with long identifiers redacted.
- Visible addon snapshots are sampled at most every 500 ms, with bounded unit/node/value counts and change deduplication. Housing-named addons or addons containing known template evidence are inspected; chat-log addons are excluded. Reports identify addon names, AtkValue indexes/types, supported 32-bit numeric/bool/float values, relevant node indexes/IDs, and candidate template evidence. Arbitrary strings, 64-bit values and pointers are withheld. Complete template-based address matching is considered only after separate housing evidence identifies an addon.
- Optional lifecycle discovery logs addon names/events only, to locate unknown status-window names. This is time-limited metadata, not a dump of unrelated addon contents.
- Debug logs report arm/stop/template-summary actions; Verbose logs hold event detail; Warning logs report missing/unavailable evidence. No general chat-text logging or `FormatLogMessageForDebugging()` call is used.
- Offline regressions cover exact expiry, clock jumps, logout/same-world character changes, context boundaries, stop/clear, history/log limits, template references, conditional-branch candidate matching, redaction, immutable views, and reports. They do not validate native memory layout, addon discovery, live event channels/ordering, or actual housing numeric mappings.

**First in-game diagnostic runs**

1. Reload plugin, open diagnostics from the bug icon or Settings, then select Housing Capture.
2. Click Inspect sheet templates. Verify custom row 6 exposes lnum1–lnum5 and Addon 7044/8849 retain both conditional branches. Missing rows/languages are warnings, not runtime values.
3. Enable All LogMessage IDs (metadata only) if investigating the unknown event path. Leave selected numeric LogMessage ID at 0 initially. Arm, open Housing Lottery Status, then Stop/Copy report. Repeat with a known Results-period placard and then a different plot. Cancel any claim/refund prompt; do not accept it for testing.
4. If a plausible LogMessage ID is observed, select that actual ID, re-arm and repeat the manual interaction to inspect integer arguments. Do not assume the ID corresponds to an Addon/custom row.
5. Test expiry, unrelated UI, logout/character switch, and Clear. No bid records should change as a result of this probe.
6. Wait for Entry to inspect a real intended successful bid; cancelled entry and actual success need separate runs. Verify Personal and FC evidence separately. If standard events do not expose custom identity/parameters, the result is inconclusive and a separate native-probe decision is required.

**Live diagnostic findings and coverage fixes (2026-10-30)**

- Initial LogMessage template reads incorrectly used raw column 0. Installed EXH inspection established raw text column 5; the probe now uses typed `LogMessage.Text`. User confirmed all 35 templates loaded after this fix.
- Timers trace: `ContentsInfo` has 313 AtkValues. Value 30 matched Housing Lottery Status; value 32 matched Results period in progress. User clicked status but no detail popup opened. This does not establish absence of a bid or the reason the detail view did not open.
- LogMessage IDs 501/533 in that trace correspond to action-ready/action-use text in reference data, not verified housing confirmation IDs.
- Placard trace: `HousingSignBoard` had 5 AtkValues; node 64 matched Available to free companies; node 56 contained an address-like line with numeric tokens 19/5. User confirmed this was a for-sale plot they had not bid on. These observations describe a plot, not proof of an entry. FC eligibility is not an FC-bid confirmation.
- Two coverage gaps were identified: the first 128 of 313 Timers values were inspected, and placard traversal was top-level only. Undefined entries and a single long snapshot also obscured evidence; short-prefix matching falsely treated Plot Details/Plot Size labels as addresses.
- Patch: inspect up to 512 AtkValues, preserving original indexes and omitting Undefined/Null entries. Report total/inspected counts and explicit value-limit status.
- Patch: traverse nested component UldManagers and child/sibling links with frame-local cycle guards, at most 1024 discovered nodes and depth 8. Report node counts/limit status; no native addresses are retained or exported.
- Patch: emit lossless numbered snapshot parts with bodies capped at 2400 characters instead of one truncation-prone line. Preserve evidence beyond the old first 12 matches. History/rate caps remain, with dropped/suppressed reporting.
- Patch: Addon/8847 candidate matching now requires the complete localized template address structure, valid plot/ward bounds, and district text; optional size suffix is supported. Short labels alone cannot match. This is still diagnostic correlation, not native identity or bid evidence.
- Added Copy events only, retaining context/status/events but omitting repeated template dumps. Use this for the next Timers/placard retests.
- New offline regressions cover label/incomplete-address rejection, synthetic localized address structures, lossless chunking including index 312 evidence, and events-only export. They do not validate the native traversal or prove support for untested client languages.
- Retest validated Timers coverage: all 313/313 values inspected, 205 nodes traversed, no limit hit, and Undefined noise omitted. The report still contained only the status title/Results summary, not an individual entry. Placard retest traversed 92 nodes with no limit hit and matched a complete address with numeric tokens 26/20; the label-only address false positives were gone.
- User clarified they have no placed bid and cannot bid during the current Results period. Screenshot of Mist Ward 20 Plot 26 showed Small, available to both FC and Personal, currently unavailable for purchase, and no displayed deadline. Therefore missing bid/ticket/deadline evidence in these runs is not a failed successful-entry capture test; that test awaits Entry and a real intended bid.
- Screenshot exposed two further probe issues: whole-node start matching omitted later messages in a combined eligibility/status block, and text nodes used UTF-8 ToString rather than decoding SeString payloads. AtkValue strings already used SeString extraction.
- Patch: both node and AtkValue strings now use the same bounded ReadOnlySeString.ExtractText path. Only complete payload-free localized sheet messages from Addon 7040/7041/7043 may match within a combined block, with boundary checks. Static message extraction does not evaluate templates or obtain live lnums. Address matching remains full-structure; chat/outcome matching is not broadened.
- 2026-11-01 live Entry-period checks: Timers without a placed bid exposed Addon/8843 and 8844 text candidates, but no current-entry details. Mist W5 P11 placard screenshot confirmed FC-only, 0 participants, and displayed close 8:59 a.m. 11/6/2026. Prior probe found eligibility but did not separately identify dynamic count/deadline within the combined text node.
- Gap patch: only HousingSignBoard snapshots use localized Addon/7040/7041/7043/7044 template-shaped recognition to export bounded eligibility, purchase-status/count, and displayed-deadline segments. Supported numeric/time/conditional macros generate allowlisted patterns; unsupported macros and incomplete fields fail closed. Unmatched arbitrary text remains withheld. Values are rendered observations, never native identity/local parameters or a placed-bid event. Displayed 8:59 remains 8:59; no date-order inference or Unix-time conversion. Global calculated phase/UTC close is logged separately.
- Placard addresses have their own evidence category. Shared Addon/8847 address wording is a format reference only, not evidence that the Timers template was invoked. Full address validation remains required.
- Gap-patch verification: Release build clean; 110/110 tests pass, including combined zero-count screenshot fixture, both eligibility types, Results/unavailable/first-come statuses, localized shapes/multiline input, incomplete/unsupported macros, privacy, and address categorization. Live retest: re-arm probe, open Mist W5 P11, and export events-only report. No bid/cache/API writes or game actions added.
- Added regressions using the screenshot's FC + Personal + unavailable text, single-line/multiline variants, incomplete/embedded-word rejection, and scope checks preventing inline chat/outcome expansion. These test pure matching policy, not live native text decoding. Repeat the placard probe after reload and send Copy events only.

**A — Read-only evidence probe (first)**

- Trace housing's actual runtime message emission/evaluation path before choosing the API. Prefer verified sheet/message identity and primitive parameter extraction over English text regexes.
- Add explicit, short-lived diagnostics for relevant IDs/sheet identity and numeric arguments, not persistent all-chat logging. Never expose character Content IDs or native addresses.
- Verify local-number indexing, ward/plot numbering, results-time value/unit, sender/event provenance, character/world context, and actual type evidence while Entry is open.
- Inspect status/placard context already available during Results without fabricating a successful-entry event or spending gil only to test capture.
- No automatic record writes during probing. If the localized path is unavailable, ask before adopting an English-text fallback or a more invasive native hook.

**Native diagnostic patch — implemented 2026-11-01, live verification pending**

- Read-only disassembly of the installed `ffxiv_dx11.exe` established a narrower pre-formatting queue boundary, avoiding speculative hooks on several global text formatters. Runtime code uses no absolute addresses.
- Reproducible static trace for installed version `2026.10.16.0000.0000`: the Lua binding for string `SystemTalk` points to wrapper VA `0x1417DE640`; wrapper dispatches scene implementation vtable slot 23. EventSceneModule constructor establishes the usual implementation vtable at `0x1422DA900`; slot 23 targets `0x1417A4860`. That routine resolves the template, collects Lua arguments 4..N into a TextParameter deque, and calls `EventFramework.QueueFormatStringCallback` at `0x140B874B0`. These are disassembly references only, not runtime offsets.
- Collector at `0x1417CE1A0` appends numeric arguments as type 0, int32 at offset 0, and self-value pointer at offset 8. Queue receives `(EventFramework*, Utf8String*, StdDeque<TextParameter>*, callback*, ulong)` and copies the unformatted template/parameters into its deferred queue. Runtime indexing and success-scene context remain candidate interpretations until live capture confirms them.
- `HousingNativeProbe.cs` uses the unique 25-byte queue prologue signature, exact executable SHA-256 `46a52ab1dbe80b9a8d6c4d4531bf66693904200c5135facacd053ef28a5de14a`, version and ClientStructs layout checks, plus a live prologue comparison before hook creation. Unknown build/layout, missing template/signature, or pre-existing prologue patch leaves the probe unavailable rather than guessing. No hard-coded VA or fallback formatter hooks.
- New Housing Capture checkbox: **Native successful-entry probe (experimental, exact game build only)**. Off by default and not persisted. Arm enables it only for the shared monotonic 15-second probe window. Stop, expiry, character loss/change, clear, and disposal disable observation; disposal releases the hook.
- Only exact localized raw bytes from housing custom-sheet row 6 qualify. Rows 7/9 and errors are not targets. Reports label this exact-template-byte evidence, never native sheet/row identity. No formatter/evaluator replay, emitted chat, native callback invocation, or bid-store writes.
- Detour forwards original arguments exactly once. Recursion/off-thread guards, bounded ReadProcessMemory copies, maximum 16 parameters, 32 pending observations, and 64 observations per arm limit inspection. It never dereferences arbitrary parameter value pointers, reads string parameters, retains native pointers, or formats/logs observations in the detour. Framework draining performs interpretation and reporting.
- Reports include probe/epoch, observation UTC, public world/territory context, numeric scene/event IDs, parameter indices/types/int32 values, read failures, duplicate/drop/invalidation counters, and candidate fields only when all five arguments and context pass validation. Expected context is housing event 721031 and scene 1; disagreement with observed EventState fields withholds interpretation but preserves bounded raw evidence for investigation. Character identity remains a private invalidation token. FC/Personal remains unverified.
- Pending observations invalidate across character/world/territory/language changes, stop, and re-arm. Duplicate diagnostic observations are counted, not treated as additional bids. Candidate results time is preserved and compared against the separate global schedule; no date parsing, field substitution, or timestamp correction.
- Offline verification: 118/118 tests and Release build pass; tests cover exact compatibility gates/template identity, typed/indexed arguments, address/time/context rejection, duplicate/storage limits, session invalidation, and privacy. Tests do **not** establish native ABI correctness, hook lifecycle safety in the game, or successful-entry runtime mapping.
- First live check: opt in, arm, open/cancel placard, copy events-only report; no NativeSuccess observation expected. For a bid already intended, arm immediately before final confirmation and compare candidate world/district/ward/plot/ticket/results time against the game and Timers. Never spend gil only for diagnostics. An unavailable gate or unexpected observation is a blocker, not permission to bypass checks.
- Follow-up only after live verification: automatic record persistence/deduplication, verified type context or Timers recovery, and cached API price/size enrichment with source provenance. Missing enrichment must not block a verified bid record.

**B — Confirmed entry capture**

- Use the verified successful-entry identity and mappings, with source/language-independent parsing where supported. Numeric date/time extraction should avoid month/day and DST parsing ambiguities; displayed times still use the saved UTC/local preference.
- Associate with valid current character and an evidenced destination world. Do not assume current world and home world are interchangeable.
- Missing ticket/size/type remains Unknown. FC/Personal still awaits live verification; the generic success template itself has no explicit purchase-type field.
- Apply bounds checks and reject ambiguous/conflicting evidence. Invalid district must not silently become the current territory.
- Reconcile uniquely against existing manual records using character/world/address/cycle/type evidence. Preserve record ID, existing outcome, and unrelated manual fields; fill unknowns only with supported evidence. Unknown type/cycle may match only if remaining evidence uniquely identifies the record.
- Repeated confirmation is a no-op; no duplicate record or unnecessary save. Conflicting tickets/multiple matches require review. Never use manual duplicate override automatically.
- Respect exclusions and read-only storage; save before publishing mutations. Failed saves remain retryable without duplicate creation.
- Add an explicit capture setting, initially opt-in for validation. Manual records remain usable independently.
- Review field-level provenance and migrate schema safely if ticket/cycle/address enrichment needs finer evidence ownership than the current grouped DetailsSource.

**C — Status recovery and placard enrichment**

- Follow HLT's status-recovery/placard-enrichment split, but use verified sheet/addon/native identity and parameters where available instead of scanning unrelated text.
- Recovery must not invent historical entry time. Keep missing fields unknown; do not require ticket if a different verified identity can safely establish the entry.
- Placards update known matching bids only; ordinary browsing, targeting, failed entry, and cancelled confirmation create no records.
- If pre-entry type context from Addon 7046 is used, bind it to character/session/world/address, verify event ordering, expire/invalidate it on cancellation or context changes, and consume only after successful confirmation. Do not invent a timeout before observing actual timing.
- Keep entry-end/results-start distinct from results-end/claim deadline. Eligibility is separate from actual bid type. Do not mark a derived claim deadline as directly observed unless the game supplied it.
- Validate hook signature, memory layout, thread context, phase semantics, and failure fallback for the installed patch before enabling native hooks.

**D — Outcome observation (separate follow-up)**

- Use explicit, correctly associated win/loss/claim evidence. A win prompt is not completed purchase/claim, and refund availability is not refund collection.
- No generic congratulatory matching, first-pending-record selection, or outcome changes from time passage. Keep existing Pending/Won/Lost/Claimed policy; do not import HLT's Expired behavior as an unrequested change.
- Notifications belong to the alert/reminder phase, not the initial capture patch.

#### Tests and live acceptance for capture

- Unit tests against production policies for verified message IDs/parameters (or explicitly approved fallback fixtures), missing/mistyped/out-of-range arguments, unknown IDs, district/world mismatches, date/cycle boundaries, and localization independence.
- Context tests for character/logout/territory changes, stale or conflicting type evidence, cancelled/failed entries, and event ordering.
- Reconciliation tests for new capture, unique manual enrichment, duplicate observations, repeated status recovery, unknown fields, ticket conflicts, multiple matches, exclusions, outcome preservation, and save failures/retry.
- Persistence tests for any provenance migration, stable record IDs/history, reload, and newer-schema protection.
- In-game: unrelated placard and cancellation create nothing; a real intended bid creates exactly one correct record; reopen status/placard and reload without duplicates; verify recovery and manual-record enrichment; disabled/excluded capture creates nothing.
- Personal and FC capture require separate runtime evidence. Do not claim untested languages/paths work merely because a localized template exists.
- Use an actual bid the user intends to place; do not spend gil or perform a synthetic game action just to test capture.

**Deliverables**

- Capture successful bid-confirmation evidence passively, preferably by verified sheet/message identity and parameters. English rendered-text fallback is a separate decision if the structured path cannot be established.
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

### Phase 7 — Integration validation (optional travel)

**Status:** User approved travel integration. Implemented with no required Lifestream dependency; live IPC/movement validation pending. Available Houses: right-click a plot number → Travel to plot. My Bids: right-click address or use Travel....

- Source-confirmed IPC: `Lifestream.GoToHousingAddress`, `IsBusy`, `Abort`, `CanVisitSameDC`, `CanVisitCrossDC`. Temporary housing-address tuples do not save address-book entries. House=0; ward/plot remain one-based, including subdivision plots 31–60.
- District mapping to Lifestream ResidentialAetheryteKind: Mist 339→8, Lavender Beds 340→2, Goblet 341→9, Shirogane 641→111, Empyreum 979→70. World names resolve from game data by ID rather than trusting manual bid spelling.
- Address validation, login, IPC availability, busy state, and world support gate travel. Queued actions recheck before execution on the framework thread. IPC failures are caught and logged without disabling core browsing/bids.
- Requested destination/status and Stop travel are shown. Stop is offered only while a PW-requested operation remains busy; Lifestream Abort affects its entire current queue/movement. Idle status does not prove arrival. Actual route completion depends on Lifestream settings and housing paths; PW performs no placard interaction or lottery actions.
- Verification: 105/105 offline tests pass; Release build has zero warnings/errors. Tests cover mapping, address bounds/one-based tuples, unavailable/busy/logged-out/unsupported states, queued-state recheck, cancellation guards, IPC failure, and unload. Live IPC signatures and actual movement are not covered by these fake-provider tests.
- Pending live checks: missing/reloaded Lifestream, same ward, other ward/district/world, subdivision plot, pathing disabled, cancellation, and no saved address-book entry.

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
