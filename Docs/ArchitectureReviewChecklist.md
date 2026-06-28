# ClashSharp Architecture Review Checklist

Date: 2026-06-28

Scope: main WinUI/WPF-style desktop app under `ClashSharp/ClashSharp`, including MVVM boundaries, service decoupling, runtime hook data flow, notification system, log/statistics system, trigger system, and test coverage.

Verification baseline: `dotnet test ClashSharp\ClashSharp.slnx --no-restore` passed with 629 tests.

Fix status: P1/P2 correctness issues listed below have targeted regression coverage. Final verification: `dotnet test ClashSharp\ClashSharp.slnx --no-restore` passed with 640 tests.

## Executive Summary

The codebase is not a raw code-behind application. Most business-heavy services use injected narrow interfaces, and many ViewModels are testable through adapters. The main architectural weakness is that composition and orchestration are split across `App`, `MainWindow`, page code-behind, ViewModels, and singleton service factories. That makes runtime behavior work, but it leaves important cross-system flows hard to reason about.

Highest-risk items found and current status:

1. Active-connection traffic double-counting: fixed with delta-based sampling.
2. Notification-trigger self-loop: fixed with trigger-origin notification suppression.
3. Periodic trigger conditions and traffic window context: fixed with periodic evaluation and recent traffic lookup.
4. SQLite log export with WAL: fixed with SQLite backup API export.
5. `ClashSharp/ClashSharp/MainWindow.xaml.cs`, `ClashSharp/ClashSharp/View/Settings.xaml.cs`, and large ViewModels remain orchestration-heavy design debt.

## Architecture Snapshot

| Layer | Current Shape | Review Result |
| --- | --- | --- |
| App startup | `App.OnLaunched` initializes localization, audit logging, trigger singleton, startup proxy recovery, main window, connection sampling | Works, but startup orchestration is embedded in app/window classes |
| UI shell | `ClashSharp/ClashSharp/MainWindow.xaml.cs` handles navigation, tray callbacks, Win32 subclassing, close behavior, startup dialogs, startup mode application, triggers | Too many responsibilities in shell code-behind |
| Views | Pages construct ViewModels and production adapters directly | Practical, but composition root is fragmented |
| ViewModels | Use local interfaces/adapters for most service dependencies | Good testability pattern; several ViewModels are oversized |
| Services | Singleton defaults plus injected constructors/factories | Mixed DI style; good for unit tests, less clear for runtime wiring |
| Models | Mostly simple records/enums | `ActiveConnection` display formatting moved to ViewModel display rows |
| Tests | 629 passing baseline plus targeted regression tests added | Good baseline; cross-system runtime behavior now has stronger coverage |

## Runtime Hook Data Flow

### Startup Flow

`App.OnLaunched`:

1. Sets language from `AppSettingsService`.
2. Handles startup restore helper argument.
3. Starts `AppSettingsAuditLogService`.
4. Starts `TriggerService` by constructing/subscribing singleton.
5. Applies stale proxy recovery.
6. Creates and activates `MainWindow`.
7. Starts connection sampling if enabled.

`MainWindow.RunStartupFlowAsync` then:

1. Evaluates `TriggerEventKind.AppEntered`.
2. Shows startup conflict dialog if enabled.
3. Shows startup guide if enabled.
4. Resolves startup proxy mode.
5. Applies mode through `NetworkTakeoverService`.
6. Logs result, notifies mode change, and evaluates/publishes `ProxyStarted` behavior.

Key evidence:

- `ClashSharp/ClashSharp/App.xaml.cs:35`
- `ClashSharp/ClashSharp/MainWindow.xaml.cs:231`
- `ClashSharp/ClashSharp/MainWindow.xaml.cs:246`

### Proxy Mode Flow

User command path:

`MasterControlViewModel.ApplyModeAsync` -> `NetworkTakeoverService.ApplyMode` -> settings current mode update -> log append -> notification/trigger publish callback.

Tray command path:

`SystemTrayService` Win32 message -> `MainWindow` dispatcher callback -> `TrayCommandService` -> `NetworkTakeoverService` -> log -> notification/trigger publish helper.

Trigger action path:

`TriggerService.ExecuteActionAsync` -> `ApplicationActionService.DispatchAsync` -> `NetworkTakeoverService` -> notification -> runtime event hub.

Key evidence:

- `ClashSharp/ClashSharp/ViewModel/MasterControlViewModel.cs:674`
- `ClashSharp/ClashSharp/Service/TrayCommandService.cs:65`
- `ClashSharp/ClashSharp/Service/ApplicationActionService.cs:59`
- `ClashSharp/ClashSharp/Service/NetworkTakeoverService.cs:119`

### Notification Flow

`NotificationService.Show`:

1. Reads enabled and level settings.
2. Builds Windows AppNotification.
3. Sends notification.
4. Appends notification log.
5. Publishes `TriggerRuntimeEvent(NotificationRaised)`.

Key evidence:

- `ClashSharp/ClashSharp/Service/NotificationService.cs:90`
- `ClashSharp/ClashSharp/Service/NotificationService.cs:106`
- `ClashSharp/ClashSharp/Service/TriggerRuntimeEvents.cs:40`

### Log and Statistics Flow

Event logs:

Callers synchronously call `LogStorageService.AppendLog`, which serializes writes through `_syncLock`.

Traffic logs:

`ConnectionSamplingService` periodically calls `MihomoConnectionService.GetActiveConnectionsAsync`, then `LogStorageService.AppendConnectionSnapshot`, which inserts connection rows and updates traffic aggregation tables.

Key evidence:

- `ClashSharp/ClashSharp/Service/LogStorageService.cs:482`
- `ClashSharp/ClashSharp/Service/ConnectionSamplingService.cs:173`
- `ClashSharp/ClashSharp/Service/LogStorageService.cs:400`

### Trigger Flow

Runtime events:

`TriggerRuntimeEventHub.Publish` -> `TriggerService.OnRuntimeEventRaised` -> queue -> `DrainRuntimeEventsAsync` -> `EvaluateAsync`.

Evaluation:

`TriggerEvaluationContextFactory` builds a snapshot from SQLite traffic summary and current process runtime, then `TriggerService.Matches` checks task conditions and dispatches actions.

Key evidence:

- `ClashSharp/ClashSharp/Service/TriggerRuntimeEvents.cs:38`
- `ClashSharp/ClashSharp/Service/TriggerService.cs:297`
- `ClashSharp/ClashSharp/Service/TriggerService.cs:308`
- `ClashSharp/ClashSharp/Service/TriggerEvaluationContextFactory.cs:20`

## Findings

### P1: Active Connection Traffic Is Double-Counted

Status: fixed.

Mihomo connection fields `upload` and `download` are parsed as current per-connection byte counters. The sampler stores every sampled value as a new row and also adds the whole value into profile/node/stat totals. A long-lived connection sampled repeatedly will contribute the same earlier bytes multiple times.

Evidence:

- `ClashSharp/ClashSharp/Service/MihomoControllerClient.cs:337` parses connection id plus cumulative upload/download.
- `ClashSharp/ClashSharp/Service/ConnectionSamplingService.cs:177` gets the full active connection list per sample.
- `ClashSharp/ClashSharp/Service/LogStorageService.cs:415` iterates sampled active connections.
- `ClashSharp/ClashSharp/Service/LogStorageService.cs:417` adds full `UploadBytes`.
- `ClashSharp/ClashSharp/Service/LogStorageService.cs:418` adds full `DownloadBytes`.
- `ClashSharp/ClashSharp/Service/LogStorageService.cs:434` upserts full node traffic.
- `ClashSharp/ClashSharp/Service/LogStorageService.cs:445` upserts full profile traffic.

Impact:

- Statistics page total traffic can be inflated.
- Trigger conditions based on total traffic can fire too early.
- Daily/profile/node aggregates can become inaccurate.

Applied fix:

Track previous byte counters by stable connection key, likely `Id` plus `StartedAt`. Persist only positive deltas. Drop state when a connection disappears. Consider storing raw snapshots separately from delta-based aggregate rows.

Test checklist:

- Same connection sampled twice with unchanged bytes writes zero delta on second sample: covered.
- Same connection sampled twice with increased bytes writes only the increase: covered.
- Restarted connection with same id but newer start time is treated as new.
- Negative counter reset does not subtract from totals.

### P1: Notification Trigger Loop Is Possible

Status: fixed.

The notification service publishes a `NotificationRaised` runtime event after sending a notification. Trigger actions can send a notification. A trigger configured with condition `NotificationRaised` and action `SendNotification` can keep re-enqueueing itself.

Evidence:

- `ClashSharp/ClashSharp/Service/NotificationService.cs:82` exposes custom notification.
- `ClashSharp/ClashSharp/Service/NotificationService.cs:106` publishes `NotificationRaised`.
- `ClashSharp/ClashSharp/Service/TriggerService.cs:284` maps trigger action to application action.
- `ClashSharp/ClashSharp/Service/TriggerService.cs:290` maps `SendNotification`.
- `ClashSharp/ClashSharp/Service/ApplicationActionService.cs:84` executes custom notification.
- `ClashSharp/ClashSharp/View/Triggers.xaml.cs:341` exposes `NotificationRaised` condition.
- `ClashSharp/ClashSharp/View/Triggers.xaml.cs:357` exposes `SendNotification` action.

Impact:

- User can create an infinite notification loop.
- Trigger event queue can grow continuously.
- Logs can flood SQLite.

Applied fix:

Add event origin metadata and suppress trigger-origin notifications from producing trigger events, or add a per-task cooldown/reentrancy guard. The cleanest model is `TriggerRuntimeEvent(EventKind, NotificationLevel, Source, CorrelationId)` plus a policy that trigger-generated notifications do not match notification triggers unless explicitly allowed.

Test checklist:

- `NotificationRaised` + `SendNotification` trigger fires at most once per originating event: covered.
- A normal app notification still fires notification triggers.
- Queued runtime events are still processed after suppression.

### P1: Periodic Trigger Conditions Are Exposed But Not Scheduled

Status: fixed.

Before the fix, the model and UI exposed `TotalTraffic`, `TrafficInWindow`, `Runtime`, and `SystemTime`, but no periodic publisher was visible. `TrafficInWindow` was always set to zero.

Evidence:

- `ClashSharp/ClashSharp/Model/TriggerTask.cs:18` defines `Periodic`.
- `ClashSharp/ClashSharp/Model/TriggerTask.cs:30` defines `TotalTraffic`.
- `ClashSharp/ClashSharp/Model/TriggerTask.cs:31` defines `TrafficInWindow`.
- `ClashSharp/ClashSharp/Model/TriggerTask.cs:32` defines `Runtime`.
- `ClashSharp/ClashSharp/Model/TriggerTask.cs:33` defines `SystemTime`.
- `ClashSharp/ClashSharp/Service/TriggerEvaluationContextFactory.cs:28` sets `WindowTrafficBytes` to `0`.
- `ClashSharp/ClashSharp/Service/TriggerService.cs:371` evaluates total traffic.
- `ClashSharp/ClashSharp/Service/TriggerService.cs:372` evaluates window traffic.
- `ClashSharp/ClashSharp/Service/TriggerService.cs:373` evaluates runtime.
- `ClashSharp/ClashSharp/Service/TriggerService.cs:374` evaluates system time.

Impact:

- Users can configure trigger conditions that never naturally run.
- Feature behavior does not match localized descriptions.
- Tests currently validate structure more than runtime semantics.

Applied fix:

`TriggerService` now owns a periodic timer with injectable interval/context creation for tests. `TriggerEvaluationContextFactory` now computes recent traffic from `TrafficSnapshots` instead of returning zero.

Test checklist:

- Scheduler publishes periodic events only when triggers are enabled: covered.
- Runtime threshold fires after threshold, not before.
- System time trigger handles once-per-day or cooldown semantics.
- Traffic window trigger uses non-zero recent-window calculation: covered.

### P2: SQLite Log Export Is Unsafe With WAL

Status: fixed.

Before the fix, the schema enabled WAL but the export path copied only the main database file. Recent transactions can live in the WAL sidecar file, so the copied file could be stale or incomplete.

Evidence:

- `ClashSharp/ClashSharp/Service/LogStorageSchema.cs:30` enables `PRAGMA journal_mode=WAL`.
- `ClashSharp/ClashSharp/View/Settings.xaml.cs` delegates export to `LogStorageService.Instance.ExportDatabase`.
- `ClashSharp/ClashSharp/Service/LogStorageService.cs` uses SQLite backup for a consistent snapshot.

Impact:

- Exported log database can omit recently committed records.
- Exported file may not represent a consistent SQLite snapshot.

Applied fix:

Use SQLite backup API through `LogStorageService.ExportDatabase`, so code-behind no longer opens the database file directly.

Test checklist:

- Append a log, export immediately, exported DB contains the log: covered.
- Export succeeds while WAL mode is active: covered.
- Export path does not require UI code-behind to know database internals: covered.

### P2: Shell Code-Behind Is Doing Application Orchestration

Status: confirmed.

`ClashSharp/ClashSharp/MainWindow.xaml.cs` handles navigation, Win32 window subclassing, tray callbacks, startup trigger evaluation, startup conflict dialogs, startup proxy mode application, notification publishing, and safe shutdown.

Evidence:

- `ClashSharp/ClashSharp/MainWindow.xaml.cs:79` constructs shell VM and adapters.
- `ClashSharp/ClashSharp/MainWindow.xaml.cs:98` hooks startup flow to content frame loading.
- `ClashSharp/ClashSharp/MainWindow.xaml.cs:246` begins startup orchestration.
- `ClashSharp/ClashSharp/MainWindow.xaml.cs:270` applies startup network mode.
- `ClashSharp/ClashSharp/MainWindow.xaml.cs:273` sends notification.
- `ClashSharp/ClashSharp/MainWindow.xaml.cs:388` builds tray state.
- `ClashSharp/ClashSharp/MainWindow.xaml.cs:430` manually notifies and triggers mode-applied behavior.

Impact:

- Startup behavior is harder to unit test end-to-end.
- The same mode-applied side effects are partly duplicated between shell, master page, tray service, and application action service.
- Future hooks may be added in the wrong layer.

Recommended fix:

Extract `StartupFlowService`, `ShellTrayCoordinator`, and `ModeAppliedPublisher`. Leave `MainWindow` with window primitives, navigation, and dialog hosting only.

### P2: Page Code-Behind Still Owns Non-Trivial Workflow Logic

Status: confirmed.

`ClashSharp/ClashSharp/View/Settings.xaml.cs` and `ClashSharp/ClashSharp/View/MasterControl.xaml.cs` include workflow logic beyond XAML event translation. Some of this is acceptable because file pickers and dialogs are UI concerns, but data package export/import, startup conflict checking, and latency test orchestration are still mixed into the page. Log DB export internals have been moved into `LogStorageService`.

Evidence:

- `ClashSharp/ClashSharp/View/Settings.xaml.cs:632` begins data export workflow.
- `ClashSharp/ClashSharp/View/Settings.xaml.cs:641` begins data import workflow.
- `ClashSharp/ClashSharp/View/Settings.xaml.cs:791` still hosts log export picker flow, but delegates database snapshot creation.
- `ClashSharp/ClashSharp/View/Settings.xaml.cs:878` applies imported settings to runtime services.
- `ClashSharp/ClashSharp/View/MasterControl.xaml.cs:131` runs latency dialog workflow.
- `ClashSharp/ClashSharp/View/MasterControl.xaml.cs:174` checks startup conflicts directly.

Impact:

- Harder to unit test workflows without UI.
- Business decisions are split between ViewModel and View.
- Runtime side effects are not centralized.

Recommended fix:

Create UI workflow services with injected file/dialog abstractions where practical:

- `DataPackageWorkflow`
- `LogExportService`
- `StartupConflictWorkflow`
- `LatencyTestWorkflow`

Keep code-behind as a host for picker/dialog primitives.

### P2: `TriggerService.TriggersEnabled` Runtime Semantics Are Ambiguous

Status: fixed.

Before the fix, the default singleton captured trigger enabled state at startup and ignored later setter calls. Settings UI treated this as restart-required, but `TriggerService` still exposed a writable property.

Evidence:

- `ClashSharp/ClashSharp/Service/TriggerService.cs:88` exposes `TriggersEnabled`.
- `ClashSharp/ClashSharp/Service/TriggerService.cs:257` captures startup setting.
- `ClashSharp/ClashSharp/Service/TriggerService.cs:266` returns captured startup value.
- `ClashSharp/ClashSharp/Service/TriggerService.cs:267` ignores setter.
- `ClashSharp/ClashSharp/View/Settings.xaml.cs:438` treats settings toggle as restart-required.
- `ClashSharp/ClashSharp/ViewModel/TriggersViewModel.cs:95` writes through `TriggerService.TriggersEnabled`.

Impact:

- Trigger page can appear to toggle runtime editability without actually changing persisted setting in the default instance.
- Future callers can assume the setter changes evaluation behavior.

Applied fix:

The runtime-toggle contract is now explicit: the default `TriggerService` reads/writes `AppSettingsService.Instance.TriggersEnabled`, and evaluation changes immediately. Settings UI no longer marks trigger enablement as restart-required.

### P3: Log Store Has Multiple Responsibilities Behind One Lock

Status: design risk.

`LogStorageService` stores event logs, connection history, traffic snapshots, profile traffic, node traffic, node health, and rule hits. Every operation is serialized with one lock.

Evidence:

- `ClashSharp/ClashSharp/Service/LogStorageService.cs:65` declares single service.
- `ClashSharp/ClashSharp/Service/LogStorageService.cs:67` single lock.
- `ClashSharp/ClashSharp/Service/LogStorageService.cs:482` event log append.
- `ClashSharp/ClashSharp/Service/LogStorageService.cs:400` connection snapshot append.
- `ClashSharp/ClashSharp/Service/LogStorageService.cs:681` cleanup by date.
- `ClashSharp/ClashSharp/Service/LogStorageService.cs:704` cleanup by size.

Impact:

- Cleanup/vacuum can block log writes and traffic sampling.
- Trigger context reads can block behind maintenance.
- Service surface is large and hard to reason about.

Recommended fix:

Split interfaces first, not necessarily files:

- `IEventLogStore`
- `IConnectionHistoryStore`
- `ITrafficStatsStore`
- `ILogMaintenanceStore`

Then split implementation once call sites are clean.

### P3: ViewModels Are Testable But Oversized

Status: design risk.

`SettingsViewModel` and `MasterControlViewModel` contain many unrelated settings, labels, formatting functions, tile catalog logic, and runtime actions.

Evidence:

- `ClashSharp/ClashSharp/ViewModel/SettingsViewModel.cs` is about 2593 lines.
- `ClashSharp/ClashSharp/ViewModel/MasterControlViewModel.cs` is about 1279 lines.
- `ClashSharp/ClashSharp/ViewModel/ManagementPageViewModels.cs` contains multiple page VMs.
- `ClashSharp/ClashSharp/ViewModel/DisplayPageViewModels.cs` contains multiple page VMs.

Impact:

- Changes become broad.
- Tests are larger and less focused.
- Feature ownership boundaries are blurred.

Recommended fix:

Split by page section or feature:

- `SettingsAppearanceViewModel`
- `SettingsStartupViewModel`
- `SettingsNotificationViewModel`
- `SettingsTriggerViewModel`
- `MasterStatusViewModel`
- `MasterTileGridViewModel`

Keep shared formatting helpers in small stateless classes.

### P3: Models Contain UI/Service Formatting

Status: fixed for active connections.

`ActiveConnection` used to expose display properties that called `MainlandChinaTextDisplayService.Instance`. That display policy has been moved to `ActiveConnectionDisplayRow` in `ConnectionsViewModel`, with the production text filter injected from the page composition boundary.

Evidence:

- `ClashSharp/ClashSharp/Model/ActiveConnection.cs` no longer references `ClashSharp.Service`.
- `ClashSharp/ClashSharp/ViewModel/ConnectionsViewModel.cs` owns active-connection display rows.
- `ClashSharp/ClashSharp/View/Connections.xaml.cs` injects `MainlandChinaTextDisplayService.Instance.Apply`.

Impact:

- Model remains a pure data record for active connections.
- Display policy is testable through the ViewModel.
- Non-UI consumers do not accidentally use UI-filtered active-connection values.

Applied fix:

Move display formatting to a ViewModel display row and add a resource test that prevents `ActiveConnection` from referencing display services again.

## Positive Findings

- Many services already use narrow injected interfaces, for example `NetworkTakeoverService`, `ConnectionSamplingService`, `StartupLaunchService`, `RuntimeShutdownService`, and `TrayCommandService`.
- Notification and trigger coupling is not direct; `TriggerRuntimeEventHub` is an explicit event boundary.
- `TriggerService` queues runtime events raised while evaluation is active, avoiding dropped events in normal reentrant cases.
- ViewModels are broadly unit-testable through adapters rather than raw singleton calls.
- Settings changes are auditable through `AppSettingsAuditLogService`.
- Test count is substantial, and resource tests catch many UI wiring regressions.

## MVVM Boundary Checklist

| Check | Status | Evidence | Next Action |
| --- | --- | --- | --- |
| ViewModels use dependency abstractions | Mostly pass | Adapter files under `ClashSharp/ClashSharp/ViewModel/*Adapters.cs` | Continue pattern |
| Views avoid constructing services directly | Partial | Pages construct adapters from singleton services | Introduce app composition root/factories |
| Code-behind only handles UI primitives | Partial | `ClashSharp/ClashSharp/View/Settings.xaml.cs`, `ClashSharp/ClashSharp/View/MasterControl.xaml.cs`, `ClashSharp/ClashSharp/MainWindow.xaml.cs` own workflows | Extract workflow services |
| Commands await side effects and expose failures | Partial | Some tile actions use fire-and-forget dispatch | Make commands async and stateful |
| Models remain UI-free | Mostly pass | `ActiveConnection` no longer uses display service singleton | Continue moving remaining display-only properties out when touched |
| ViewModels are focused | Partial | Large `SettingsViewModel` and `MasterControlViewModel` | Split by feature/section |

## Hook Coverage Checklist

| Hook | Current Publisher | Current Consumer | Gap |
| --- | --- | --- | --- |
| App entered | `MainWindow.RunStartupFlowAsync` direct evaluate | `TriggerService` | Tied to shell |
| Proxy started | `ApplicationActionService` or shell helper | `TriggerService` | Duplicate publishing paths |
| Notification raised | `NotificationService` | `TriggerService` | Trigger-origin notifications are suppressed to prevent loops |
| Periodic | `TriggerService` periodic timer | `TriggerService` | Scheduler added |
| Traffic threshold | Periodic trigger evaluation | `TriggerService` | Recent-window traffic context added |
| Runtime threshold | Periodic trigger evaluation | `TriggerService` | Periodic hook added |
| System time | Periodic trigger evaluation | `TriggerService` | Periodic hook added |
| Settings changed | `AppSettingsService.SettingChanged` | Audit log only | No general settings event bus |

## Recommended Refactor Plan

### Phase 1: Correctness Fixes

1. Fix traffic delta accounting in connection sampling.
2. Add notification-trigger loop prevention.
3. Add trigger scheduler and implement non-zero traffic window.
4. Fix SQLite WAL-safe export.

### Phase 2: Runtime Orchestration Boundaries

1. Extract `ModeAppliedPublisher`.
2. Extract `StartupFlowService`.
3. Extract `ShellTrayCoordinator`.
4. Make `MainWindow` call these services rather than owning workflow details.

### Phase 3: MVVM and Workflow Cleanup

1. Extract settings data import/export workflows.
2. Extract latency test workflow.
3. Split large ViewModels by feature section.
4. Move model display formatting out of `ActiveConnection`.

### Phase 4: Test Coverage Expansion

1. Add traffic delta tests.
2. Add trigger loop prevention tests.
3. Add periodic trigger scheduler tests.
4. Add WAL export integration test.
5. Add composition tests that ensure mode-applied side effects use one publisher path.

## Completion Checklist for Future Fix PRs

- [x] `dotnet test ClashSharp\ClashSharp.slnx --no-restore` passes.
- [x] Traffic aggregate tests prove no double-counting.
- [x] Notification trigger loop test proves bounded execution.
- [x] Periodic trigger tests prove periodic conditions can fire without unrelated events.
- [x] SQLite export test proves an immediately appended log appears in exported DB.
- [ ] `ClashSharp/ClashSharp/MainWindow.xaml.cs` no longer applies startup mode directly.
- [x] `ClashSharp/ClashSharp/View/Settings.xaml.cs` no longer copies SQLite files directly.
- [x] `ActiveConnection` no longer calls singleton services for display text.
- [ ] ViewModel tests cover each split settings section after refactor.
