# DriveSync: design implementation plan

Source of truth: `design/drivesync.html` (tokens, markup, behaviour) and `design/DriveSync-UI-Design.pdf` (38 pages).
Target: the existing WPF app (.NET 8), rebuilt to the design, with every prototype fake replaced by the real thing.

## 1. What existed before this work

| Area | Before |
|---|---|
| UI framework | WPF, .NET 8 (`DriveSync.App`): one window with a file-type picker, folder picker, name box, results grid and a preview panel |
| Scanning | `FileScanner`: parallel walk of every fixed/removable drive, one worker per core over a shared folder stack |
| Index | `FileIndex`: `Dictionary<folderPath, FolderNode>`; every search walked all names with `Parallel.ForEach` + `Contains` (no token map) |
| Storage | `IndexStore`: SQLite at `%LOCALAPPDATA%\DriveSync\index.db`, `folders(id,path)` + `files(folder_id,name,size,modified)`, rewritten after every scan |
| Live updates | `IndexWatcher`: one `FileSystemWatcher` per drive; `IndexManager` rescanned everything every 30 min and on buffer overflow |
| Preview | `TextExtractor` (txt/md/csv/code, docx/pptx/xlsx via raw zip+xml, html) and `SummaryService` (frequency summary) |

## 2. Screens and states → code

| Screen / state (PDF page) | Implementation |
|---|---|
| Window shell: Mica, title bar with breadcrumb, status chip, 46-px caption buttons | `MainWindow` + `WindowChrome`, `Shell/Native.cs` (`MicaHelper`: DWM Mica backdrop, dark frame, rounded corners, solid fallback below Windows 11 22H2) |
| Collapsible navigation, accent pill, Live activity badge, indexing status card, Settings | `MainWindow` nav + `Controls/NavItem` |
| Home (p7), Home rebuilding (p8) | `Pages/HomePage` |
| Search ready state (p9), results + summary (p10), outline (p11), details (p12), code file (p13) | `Pages/SearchPage`, `Pages/PreviewPane`, `Core/Parsing` |
| Preview error: locked (p14), warning: size limit (p15) | `PreviewService` (sharing violations, Restart Manager names the app holding the lock) |
| Offline drive (p16), no results (p17), empty index (p18) | `SearchPage` states, infobars and Offline pills, driven by folder status and engine state |
| Filter dropdown (p19), context menu (p20), toast (p21) | `Controls/FluentMenu`, `Controls/DropDown`, `Shell/Overlays.cs` (`ToastHost`) |
| Indexed folders (p22), validation (p23), Add folder (p24), scanning (p25), remove confirm (p26) | `Pages/FoldersPage`, `Dialogs/AddFolderDialog`, `Dialogs/Dialogs.cs` (`ConfirmDialog`) |
| Live activity (p27) | `Pages/ActivityPage`, `Controls/Charts.cs` (`HourlyBars`) |
| Benchmarks (p28) | `Pages/BenchmarksPage`, `Core/Benchmarking/BenchmarkRunner`, `Controls/Charts.cs` (`LogLineChart`) |
| Settings (p29, p30), confirm clear (p31), keyboard shortcuts (p32) | `Pages/SettingsPage`, `ConfirmDialog`, `ShortcutsDialog` |
| First run (p33–36) | `Dialogs/OnboardingDialog` |
| Closed to tray (p37) | `Shell/TrayService` (WinForms `NotifyIcon`) + `Shell/TrayFlyout` |
| Narrow window (p38) | `MainWindow` breakpoints 1180 / 920 / 720 px; icon rail; preview as a sliding sheet |
| Design system (p3–5) | `Themes/Tokens.xaml` (every `:root` token), `Themes/Icons.xaml` (every SVG symbol, generated), `Themes/Controls.xaml` (all component states) |

Your existing "All drives" and Browse… folder scope is kept. The Folder filter offers All drives, every indexed folder and Browse…, and first-run setup offers All drives (pre-selected when an index from DriveSync 1 is found).

## 3. Architecture

**Core (`DriveSync.Core`)**

- `Indexing/IndexEngine`: struct-of-arrays file table.
  - Folders are numbered in depth-first order and files are stored folder by folder, so a folder plus everything below it is one contiguous range of folder ids and one of file ids.
  - Name tokens and folder-name tokens live in sorted arrays with posting lists, so prefix lookup is a binary search.
  - Queries are bitset ANDs plus range operations, and the top K results come from a heap.
  - Watcher changes go into a small delta (appended rows + tombstones) under a reader/writer lock. Full scans build a new engine off-thread and swap it in, replaying changes made meanwhile.
  - Close-match fallback: substring first, then letters in order close together.
- `Indexing/FileScanner`: the original parallel walker, plus per-folder options (subfolders, file types), exclusions, hidden/system handling, created time and live progress.
- `Indexing/IndexService`: indexed folders, one job at a time (add, rescan, refresh, rebuild, setup) through the Select → Index → Parse → Search stages with progress.
  - Watching: one watcher per folder; on buffer overflow only that folder is rescanned.
  - Schedule and devices: scheduled refresh, drive reconnect checks, battery-saver pause.
  - Storage: SQLite writes in batches.
  - Data: Live activity log, exclusions with Undo, folder removal with Undo, search history, lookup times.
- `Storage/IndexStore`: SQLite.
  - The index is a compact binary snapshot stored as 8 MB blobs, plus a journal of watcher changes written every 3 s and replayed at start-up.
  - The same database holds the folders, settings, exclusions, history, activity events and saved benchmark results.
- `Parsing`:
  - Formats: DOCX/PPTX/XLSX (DocumentFormat.OpenXml, streamed), PDF (PdfPig), ODT/ODS/ODP, HTML, RTF, Markdown, CSV/TSV, text and about 60 code extensions.
  - Outlines: real ones from Word heading styles, PDF bookmarks or large-font lines, slide titles, sheet names, Markdown/HTML headings, and code namespaces/types/members.
  - `ExtractiveSummarizer`: term frequency + position.
  - `FileLockInfo`: Windows Restart Manager.
  - `PreviewService`: on-demand parsing with a 200-entry cache and start-up warm-up.
- `Benchmarking/BenchmarkRunner`:
  - Collections of 2k/10k/50k/100k of the user's real files, timed both as indexed lookups and as a real directory walk.
  - Index build time and distinct tokens per collection.
  - Windows Search through `Search.CollatorDSO` OLE DB.

**App (`DriveSync.App`)**

- Theme: one shared resource dictionary set. Accent, transparency, reduce motion and compact rows apply live through `ThemeManager`.
- Motion: every animation goes through `Motion`, which turns it off for Reduce motion or the Windows animation setting. Durations are 180–280 ms with the `cubic-bezier(.2,.8,.2,1)` easing.
- Searching:
  - The query is debounced at 50 ms, the lookup runs on a background thread, and only the newest answer is shown.
  - Results use a lazy `IList` in a virtualizing, recycling `ListBox`, so 100,000+ rows scroll smoothly.
  - Watcher-driven refreshes keep the scroll position and selection.
- System integration: global hotkey via `RegisterHotKey`; Start with Windows via the HKCU Run key with `--background`; single instance (a second launch brings the window forward); real tray icon and flyout; close-to-tray.

## 4. Libraries added

| Package | Why |
|---|---|
| DocumentFormat.OpenXml 3.0.2 | DOCX, PPTX, XLSX text and headings |
| PdfPig 0.1.9 | PDF text, bookmarks, font sizes for headings |
| System.Data.OleDb 8.0.1 | Windows Search (SystemIndex) benchmark series |
| Windows Forms (framework) | `NotifyIcon` for the real tray icon |

## 5. Performance

All numbers are measured on this PC with the real saved index from DriveSync 1: 1,291,664 files in 729,548 folders. The queries are `report`, `lecture virtualization`, `index`, `lab 03`, `screenshot`, `dijkstra`, `main`, `readme`, `config json`, `invoice 2026`, `a` and `zzqx`; each value is the median of 9 runs in a Release build.

The PC's speed changed during the session, so each pair below was measured back to back under the same conditions.

**Run A** (machine at normal speed):

| | Before (substring scan) | After (token index) |
|---|---|---|
| Median lookup over the 12 queries | 53.6 ms | **4.7 ms** |
| Broadest query (`a`, 811k–972k matches) | 111.1 ms | **58.2 ms** |
| Extension-only filter (`.pdf`) | 49.9 ms | **3.7 ms** |
| Selective query (`screenshot`) | 58.7 ms | **0.08 ms** |
| Load the saved index at start-up | 4.8 s | not measured in this run |

**Run B** (machine about 2× slower; both versions measured again):

| | Before | After |
|---|---|---|
| Median lookup over the 12 queries | 100.1 ms | **9.0 ms** |
| Slowest query | 298.0 ms | **100.4 ms** |
| Extension-only filter (`.pdf`) | 66.0 ms | **7.1 ms** |
| Load the saved index at start-up (cold process) | 8.9 s | **6.1 s** (2.5 s read + 3.5 s build) |
| Full save of the index to SQLite | (one row per file) | **3.5 s** (snapshot; 33.4 s with one row per file) |

Notes:
- Queries with no exact match (`dijkstra`, `zzqx`) run the close-match fallback over every name. That is the slowest path, and it stays below the old engine's time for the same query.
- In the app, the median lookup time is shown on Home and the time of each search next to the result count.
- Benchmarks page, measured live on 10,000 of the user's files: indexed lookup 0.063 ms, directory walk 44 ms, Windows Search 56 ms.

## 6. Checklist: every PDF page and prototype interaction

✓ = built and checked in the running app (screenshot tour and/or by hand) or by the 25-test service suite. Notes where something differs.

| # | Item | Status |
|---|---|---|
| p2 | Search reachable from every screen (Ctrl K, global Alt Space); timing shown, not claimed | ✓ |
| p2 | Status in title bar, navigation card, tray and pipeline | ✓ |
| p3 | Colours and materials: wall, Mica (alpha .55–.97 slider), layers, strokes, text, accent + derived, semantic, 9 file-type hues | ✓ in `Tokens.xaml`. Note: WPF popups (menus, tooltips) can't blur what's behind them, so they use the design's acrylic colour (rgba(34,40,46,.92)) without the blur. Dialogs blur the page behind them, and the window uses real Mica |
| p4 | Type scale, 4-px spacing, radii, elevation 1–4, motion 180/220–280 ms | ✓. Note: Segoe UI Variable and Cascadia Code replace Sora/Figtree/JetBrains Mono, as requested. WPF text has no letter-spacing, so eyebrows and titles use normal tracking |
| p5 | Buttons: rest, hover, pressed (0.98 scale), disabled (40%), focus ring; Primary, Default, Ghost, Danger, icon | ✓ |
| p5 | Inputs: rest, focus (2-px accent bottom), error (2-px red + message) | ✓ |
| p5 | Toggle, checkbox, segmented control, slider | ✓ |
| p5 | Menus with tick, shortcut and danger item | ✓ |
| p5 | Pills (Watching, Paused, Scanning %, Drive not found), chips, kbd, timing pill | ✓ |
| p5 | Toast, warning infobar, nav item with accent pill and badge, result row (selected/hover/highlight) | ✓ |
| p6 | Flows: find a file; add a folder; stay current; recover from a reset; run in the background; first run | ✓ all six |
| p7 | Home: greeting + date, hero search (Enter), recent chips or tip, 4 stats (files + added this session, folders + drive warning, last refresh + live countdown, median lookup), Recently changed (click opens in Search), pipeline, Watched folders, Add folder, Refresh index | ✓ |
| p8 | Home while rebuilding: progress in title chip, nav card and pipeline (Running…/Done/Waiting) | ✓ |
| p9 | Search ready state: operator tips, recent searches, key hints | ✓ |
| p10 | Live results, highlight, New tags, count + timing pill, extractive summary | ✓ |
| p11 | Outline tab: real headings (H1–H3) | ✓ |
| p12 | Details tab: type, size + bytes, folder, location, modified, created, indexed, words + read time, index tokens | ✓ |
| p13 | Code file: structural overview, symbols outline (ns/type/fn) | ✓ |
| p14 | Locked file: names the app, Try again, "still open" toast | ✓ (real Restart Manager lookup) |
| p15 | Over the size limit: Summarize anyway, Change limit (jumps to the setting) | ✓ |
| p16 | Offline drive: banner with Check folders, Offline pills, preview "Drive isn't connected" with Check again, Open shows an error toast | ✓ (driven by real drive checks every 10 s) |
| p17 | No results: Remove filters, Search without operators, Index another folder, paused-folders note | ✓ |
| p18 | Empty index after Clear: explanation + Rebuild index | ✓ |
| p19 | Type / Folder / Modified / Sort dropdowns, Reset filters, sortable Modified/Size headers | ✓ (Folder adds Browse…) |
| p20 | Context menu + Shift F10/Menu key: Open, Show in folder, Copy path, Copy name, Summarize, Exclude this folder (Undo) | ✓ |
| p21 | Toasts: ok, info, warn, err; actions (Undo, Search it, Rebuild now, Open DriveSync); max 4; auto-dismiss; hover pauses | ✓ |
| p22 | Indexed folders table: status pills, files, last scan, Pause/Resume, Rescan, Remove; disconnected-drive banner + Check again; summary line; Pause watching; exclusions editor applied at once | ✓ |
| p23 | Add folder validation: empty, invalid, duplicate, already covered, drive not connected, whole-drive warning, nested folders merged | ✓ (plus "folder doesn't exist") |
| p24 | Add folder dialog: quick picks, Browse… (Windows folder picker), display name, Include subfolders, 8 type checkboxes, "pick at least one" | ✓ |
| p25 | Scanning: % pill + progress bar in the table, title chip, nav card; toast with "Search it" | ✓ |
| p26 | Remove confirmation naming what happens; Undo toast | ✓ |
| p27 | Live activity: real watcher events (Added/Modified/Renamed/Removed), filters, count, fresh-row animation, Watch for changes, Refresh now, index health (7 KPIs), 24-hour chart | ✓ |
| p28 | Benchmarks: real indexed vs directory-walk runs, log chart, table (indexed, full scan, Windows Search, × faster, build time, tokens), planned evaluation | ✓ Windows Search measured, so its row reads "Measured · see chart"; the others stay "Scheduled" |
| p29 | Settings general and indexing: Start with Windows, Keep running when closed, global shortcut (4 options), Detect changes instantly, Scheduled refresh, Largest file (1–500 MB validated), hidden files, battery saver | ✓ |
| p30 | Privacy and appearance: Offline badge, index location + Copy, Rebuild (confirm), Clear (confirm); 6 accents live, transparency live, reduce motion, compact rows; About (version, credits, Run setup, View shortcuts) | ✓ |
| p31 | Clear index confirmation with acknowledgement checkbox | ✓ |
| p32 | Keyboard shortcuts dialog | ✓ (also lists the global shortcut) |
| p33–36 | First run: Welcome / Choose folders (All drives, indexed, quick picks, any path, Browse…) / Build index (4 live stages, bar, file count, Continue in background) / Ready (Review folders, Start searching) | ✓; shown on first launch and from Settings |
| p37 | Close to tray: window hides, watching continues, balloon hint, flyout with status, files, last refresh, changes today, Open / Pause / Refresh now | ✓ (flyout adds a small Quit button so DriveSync can be exited) |
| p38 | Narrow window: icon rail (56 px), sliding preview sheet with close button, hidden columns | ✓ |
| — | Search-as-you-type 50 ms debounce; history after 1.4 s or Enter; arrow keys; Enter opens; Up from the first row returns to the box | ✓ |
| — | Ctrl K, /, Ctrl ,, ?, Alt 1–6, Ctrl Shift C, Shift F10, Esc (dialogs, menus, preview sheet); visible focus rings | ✓ |
| — | Double-click title bar maximizes; minimize/maximize/restore; maximized window fits the screen | ✓ |
| — | Scheduled refresh at the Settings interval; watcher overflow rescans only the affected folder; drive reconnect catches up | ✓ |
| — | Open (default app) and Show in folder (`explorer.exe /select`) really run | ✓ |
| — | Index survives restarts; start-up loads it instead of rescanning | ✓ |

**Replaced by the design:** the old "type any extension" picker is replaced by the design's Type groups plus the `ext:` operator (`ext:dart`, `ext:psd`…), which covers every extension.

**Start-up defaults (from the design):** Start with Windows is on and the global shortcut is Alt Space, which overrides the Windows window-menu shortcut while DriveSync runs. Both can be changed in Settings.
