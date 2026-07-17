# Changelog

## [1.2.0] - 2026-07-17

The GitHub Desktop release — the dashboard becomes a full local git client.

### Added
- **Per-repository work area** in the detail view, as tabs: Overview, Changes, History, Branches, Issues, Pull Requests, Stashes (Ctrl+1–7 to switch)
- **Changes** -- staged / unstaged / conflicted file lists, per-file native diff viewer (parsed from `git diff`, no web view, with line-number gutters and merge/mode-change handling), stage/unstage per file or all, discard and untracked-delete (both confirmed), commit box with amend (prefills the last message), Ctrl+Enter to commit
- **Branches** -- local branches with upstream tracking and ahead/behind, create, switch, and safe delete (refuses unmerged)
- **History upgrade** -- per-commit changed-file list and per-file diff, plus the existing commit-to-GitHub link
- **Issues** as a full list (number, title, author, labels, updated) and **Pull Requests** with draft state and an aggregated checks verdict (passing/failing/pending); Enter or double-click opens on GitHub
- **Stashes** -- list, apply, pop, and drop (drop confirmed)
- **Branch bar + sync** -- current branch, ahead/behind, Fetch / Pull (fast-forward only) / Push (auto-sets upstream on the repo's actual remote)
- **State banner** -- surfaces merge / rebase / cherry-pick / revert / bisect / detached-HEAD / conflicts loudly, with Open in Terminal (no in-app merge tool)
- **Clone** -- pick from your GitHub repositories (type-to-filter) or paste any URL (https/ssh/file/local); clones into the projects root
- **Sync All** -- fetches every clean repo, fast-forwards the ones behind and pushes the ones ahead; dirty, diverged, detached, and conflicted repos are skipped and reported
- **Command palette** (Ctrl+K) -- fuzzy-jump to any project or action
- **Auto-refresh** -- a debounced file watcher updates a card within a couple of seconds of an on-disk edit, commit, or branch switch (toggle in Settings)
- **Remote discovery** (ROADMAP v1.1) -- your GitHub repos with no local clone appear as one-click-cloneable "Cloud" cards (toggle in Settings)
- Cards now show the current branch, ahead/behind, and a loud attention state for conflict / mid-operation / detached repos
- `PD_DATA_DIR` environment variable relocates all app state under one directory (portable mode)

### Changed
- **Every subprocess goes through one hardened `ProcessRunner`** -- both pipes drained concurrently (no deadlocks on chatty git output), UTF-8 decoding (no mojibake on unicode paths/authors), `ArgumentList` quoting, timeout + cancellation, and a failed process launch returns a result instead of throwing
- GitHub visibility and issue/PR counts are fetched in one batched `gh api graphql` call per ~25 repos (was three `gh` spawns per repo); counts are nullable so an unreachable repo reads as absent, never a false zero
- Origin URLs are parsed properly -- SSH/scp forms, `.git` inside names (e.g. `user.github.io`), and non-GitHub hosts no longer produce wrong links or pointless `gh` calls
- The About version reads from the assembly (was a hardcoded string that had drifted)

### Fixed
- Worktree checkouts (whose `.git` is a file) are now discovered
- The Hidden view no longer overwrites a repo's real Status, and no longer gets clobbered by search/sort/refresh while it's shown
- Manifest edits no longer appear reverted on relaunch within the cache window
- The sidebar keeps updating after the first refresh; back-navigation no longer crashes on a project entry
- A faulted discovery scan shows a banner instead of an empty dashboard; unobserved background-task failures are logged
- Opening a project (card or palette) lands on the right repo (was navigating by page type and landing on the first)
- Full keyboard back-navigation (Alt+Left / Backspace), theme-correct Notes editor and code blocks in Light theme, and screen-reader names on chips and combo boxes

## [1.1.1.2] - 2026-06-01

### Added
- **Clickable open-issues link** -- the open-issue count on each card now links straight to that repo's open issues on GitHub
- **Open pull-request count** -- a per-card PR count chip, clickable through to the repo's pull-request list
- **In-app issue filing** -- right-click a card to Report a Bug or Request a Feature; opens a pre-filled, labeled GitHub new-issue page (bug reports auto-fill app version, OS, and .NET runtime)

### Fixed
- The card open-issue count could never display (an always-collapsed style trigger); it now shows whenever a repo has open issues

## [1.1.1.1] - 2026-05-31

### Added
- **Out-of-source project metadata** -- per-project manifests now live in a single path-keyed index at `%APPDATA%\ProjectDashboard\manifests.json`, keeping source repos clean. Legacy repo-root `project-manifest.json` files are auto-imported on first scan.
- **Visibility nav filters** -- Public, Private, and Non-Local (has-a-remote) items in the left pane; Dashboard resets all filters
- **Installer** -- per-user NSIS installer (no elevation, no signing), framework-dependent on the .NET 10 Desktop Runtime (detected, never bundled). Start Menu + Desktop shortcuts, Add/Remove entry, branded wizard, unique app icon
- **GitHub status surfacing** -- dashboard banner when gh is missing or not signed in, with an in-app "Sign in to GitHub" button; Settings shows live gh status, a Re-check button, and a gh.exe path picker
- **Data-quality chips** -- "Remote mismatch" (origin slug != folder name) and "Needs metadata", shown only when non-zero
- **Full keyboard navigation** -- arrow-key nav in the left pane, Tab/arrows/Enter through the card grid, keyboard-activatable summary chips and commit/issue rows, visible focus rings
- **Diagnostic log** -- `%APPDATA%\ProjectDashboard\log.txt` records previously-silent failures

### Changed
- **Status indicators are glyphs, not colored dots** -- sync (check / edit / cloud-off), visibility (globe / lock / desktop / unknown), gh connection (plug). Badges use soft tonal backgrounds; all status colors consolidated into one named palette
- **Detail view loads instantly from cached data** -- no git/gh subprocess calls per project switch (freshness comes from Refresh / Sync Now)
- gh is delegated to for all GitHub access; the app never reads, stores, or transmits tokens. gh and git resolve via known install dirs then PATH (survives a stale Start-Menu PATH)
- New Project no longer writes a `project-manifest.json` into the repo

### Fixed
- A repo with no commits (or where git can't be read) no longer falsely shows "Synced" -- it reports its real state, or "status unavailable"
- gh fetch failures no longer masquerade as "0 issues" or "local" -- an unreachable visibility reads as "unknown"
- Hidden view no longer sticks; the selection indicator follows the clicked nav item; opening Settings after viewing a project lands on Settings
- Saving Settings no longer resets window position/size
- Removed a sidebar event-handler leak that re-wired on every refresh

## [1.10.0] - 2026-03-29

### Added
- Open in Terminal context menu item (launches Windows Terminal in project directory)
- About section on Settings page (version, description, tech stack, author)
- Self-contained release build (win-x64, no .NET runtime required)

## [1.9.0] - 2026-03-29

### Added
- App icon (blue rounded square with git branch + card grid)
- Hidden projects now load full git/manifest data (version, sync status, visibility, notes)
- Window state restore clamps to MinimumSize (prevents layout collapse from stale state files)

### Changed
- **Typography rationalized** -- reduced from 11 font sizes to 4 (12, 13, 14, 22). Two fonts: Segoe UI for chrome, Cascadia Code for data fields (hashes, paths, editor)
- Sync status dot moved to its own row below title, aligned with path
- Dashboard cards: "X unsynced" → "X uncommitted", counts modified + untracked files
- Detail page title: 24 Bold → 22 SemiBold
- Section headers: 15 → 14
- Form labels (Type/Status/etc): 11 → 12
- Body text: 12.5 → 13

## [1.8.0] - 2026-03-29

### Added
- Note prefix icons on dashboard cards: TASK (checkbox, blue), BUG (bug, red), WAIT (clock, amber) with counts, right-justified on sync status row
- Icon-prefixed notes rendering on detail page with Edit/Done toggle
- NoteLine model: parses TASK, BUG, WAIT, PLAN, INFO prefixes with Fluent icons and colors
- Sync Now button in Settings (force refresh, bypasses cache)

### Changed
- Dropped TODO prefix support (use TASK instead)
- Local repo visibility badge: red warning (#C0392B) instead of gray
- Notes editor: white text and caret for dark mode readability
- Detail page icons: 3px top margin for baseline alignment with text
- Tasks summary badge now counts projects with any actionable prefix (TASK/BUG/WAIT)

### Removed
- Orange "N tasks" text badge from card badge row (replaced by icon row)

## [1.7.0] - 2026-03-29

### Added
- Description field in project-manifest.json, shown on cards and detail header
- Tasks badge on cards: orange `[N tasks]` tag, hidden when zero
- Pane collapse state persisted across restarts

### Changed
- Cards show Description instead of Notes (cleaner, purpose-built)
- Sync dot: green (synced), yellow (unsynced), red (no remote)
- Sync label: "Synced" / "X unsynced" / "No remote"
- Private visibility badge color: purple (#7B68EE)
- Task filter recognizes both TODO: and TASK: prefixes
- Description style matches "no version" (FontSize 12, TertiaryBrush)

## [1.6.0] - 2026-03-29

### Added
- Hidden projects: filter badge in summary bar, sidebar nav item, right-click Hide/Unhide
- Repo visibility badges on cards: public (green), private (brown), local (gray)
- New Project auto-refresh (git ops moved to background thread)

### Fixed
- Context menu commands (Border.Tag relay for popup visual tree binding)
- Sidebar project navigation (Click event + Dispatcher.BeginInvoke)
- Dashboard navigation after sidebar project view (Navigate state reset)
- Hidden count excludes non-git directories

### Changed
- "TODO" renamed to "Tasks" in summary bar
- All summary labels SemiBold with uniform 86px badge widths
- "New Project" button shortened to "New"

## [1.5.0] - 2026-03-29

### Added
- New Project button: prompts for name, creates folder with README, CHANGELOG, .gitignore, project-manifest.json, git init + initial commit

### Fixed
- Sidebar project navigation: follows WPF-UI gallery pattern (TargetPageType + SelectionChanged)
- ProjectDetailPage: Transient registration (was Singleton, showed stale data)
- Scroll crash on RichTextBox content (VisualTreeHelper fallback to LogicalTreeHelper)

## [1.4.0] - 2026-03-29

### Added
- Fenced code blocks with monospace font and background highlight
- Numbered lists (1. 2. 3.) with proper indentation
- *italic* and ~~strikethrough~~ inline formatting
- #### h4 header support
- Clickable markdown links (open in browser)
- Inline formatting inside headers
- Image support: local files and remote URLs
- Global error handler (shows dialog instead of crashing)
- Read limit increased from 80 to 500 lines for README/CHANGELOG

### Fixed
- Detail page crash: SetProjectAsync returns Task (was async void race condition)
- Image rendering crash: BitmapImage.Freeze() for cross-thread access
- Markdown rendering wrapped in Dispatcher.Invoke with plain-text fallback

## [1.3.0] - 2026-03-28

### Added
- Sort dropdown: Name, Last Commit, Status, Dirty First, Category
- Window state persistence (position, size, maximized saved/restored across sessions)

### Fixed
- Sidebar project icons now update after refresh (CollectionChanged listener)

## [1.2.0] - 2026-03-28

### Added
- TODO filter badge (counts projects with TODO: in notes)
- All summary badges clickable: Total (show all), Dirty, TODO, Issues

### Fixed
- Detail page loading from sidebar (always refresh from disk, cache had sparse data)
- Window size set to 1621x823 (4 columns, no gap)
- Sidebar project navigation (SelectionChanged instead of Click)

## [1.1.0] - 2026-03-28

### Added
- Sidebar: expandable Projects list with direct navigation to detail
- Discovery cache (`%LOCALAPPDATA%\ProjectDashboard\discovery-cache.json`) for instant relaunch
- Right-click context menu on cards: Open Details, Refresh Status, Open on GitHub, Open Folder
- Clickable commit hashes and issue numbers (open on GitHub in browser)
- Mouse back button (XButton1) navigates back
- Markdown rendering in README/CHANGELOG: headers, bold, code, bullets, images, tables
- Detail page restructured: manifest + notes on top, commits second, README/CHANGELOG collapsed

### Changed
- Default refresh interval: 2 hours (was 5 minutes)
- Sidebar: Left mode with persistent text labels (no icon-only LeftFluent mode)
- Detail page: notes field is monospace multi-line editor
- Global mouse wheel scroll fix on MainWindow

## [1.0.0] - 2026-03-28

### Added
- Initial release
- Fluent 2 WPF shell with Mica backdrop, dark/light theme, NavigationView sidebar
- Dashboard view: card grid with version, git status, category, last commit, open issues
- Project Detail view: README/CHANGELOG display, commit history, GitHub issues, manifest editor
- Settings view: projects root path, refresh interval, excluded directories, theme toggle, GitHub auth status
- Git integration via CLI: status, tags, commits, remote URL, ahead/behind
- GitHub integration via `gh` CLI: open issues, graceful offline degradation
- Markdown parsing: title, description, version extraction from README/CHANGELOG
- Per-project `project-manifest.json` for human-authored metadata
- Category and search filtering on dashboard
- `AppSettings` persisted to `%LOCALAPPDATA%\ProjectDashboard\settings.json`
