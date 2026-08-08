# Changelog

## [2.0.0] - 2026-08-08

Full repository management -- deliberate, guarded history rewriting and commit surgery, GitHub administration in-app, and the local git depth earlier releases left to the terminal.

### Added
- **History rewrite wizard** -- replace text across history (literal or regex), purge a file or folder from every commit, rewrite commit and tag messages, and correct an author/committer identity. Scope a run to all history, a glob, explicit paths, specific commits, or a commit range. Built on a native `git fast-export` → transform → `git fast-import` engine: no new runtime dependencies, binary payloads left untouched, and shared blobs split so a scoped edit changes only the paths it names
- **Dry run before every rewrite** -- the run is rehearsed in a scratch copy and reports the commits, files, and match counts it would change; the wizard stays disarmed until a dry run has passed
- **Honest verification report** -- after a rewrite the result is re-read from the rewritten objects and reported as clean only where coverage was total. A search that could not run, a message that would not decode, or a scope the check could not answer for is reported as a gap, never as "clean"
- **Backups, undo, and crash recovery** -- a verified `git bundle` of every ref is taken before any history-altering operation, kept under a retention limit, and browsable; undo is offered the moment the rewrite lands, and a run interrupted by a crash is detected on the next launch and offers the same restore
- **Plan a history edit** -- reorder, drop, squash, and reword commits across a range as one replay, with a preview of the resulting history and an explicit refusal, with the reason, for any plan git cannot produce
- **Commit surgery from the History tab** -- reword any commit (not only `HEAD`), squash into the previous commit, drop a commit, inject staged changes into an older one, and reset (soft / mixed / hard), revert, or cherry-pick
- **Force push with lease** -- publish a rewritten branch per branch with `--force-with-lease`, at the object id the plan produced, after reviewing exactly what diverged and typing the repository name. Branches that are only behind are excluded and named. The app never pushes on its own
- **Reflog viewer and deep clean** -- browse every position the refs have held as restore points, then optionally expire the reflogs and prune the object store so replaced history stops being reachable locally. Its own typed confirmation, the backup bundle retained, and the reclaimed size reported (or reported as unmeasured)
- **Actions tab** -- workflow runs with status, conclusion, branch, event, and elapsed time; per-run jobs and steps; re-run all or only failed jobs and cancel a running run (both confirmed); open on GitHub
- **Releases tab** -- releases with draft/prerelease state, date, and asset count; release notes rendered natively; create a release from an existing tag (title, notes, draft/prerelease); delete a release (confirmed); download an asset to a location you choose
- **Repo tab** -- description, homepage, and topics; issues / wiki / projects toggles; default-branch change (confirmed) and visibility change (confirmed by typing the full `owner/name`); and this repository's unread notifications with explicit mark-read
- **Danger zone** -- deleting the repository on GitHub, reachable only after a Settings opt-in and then only behind a typed `owner/name` confirmation. Local files are never touched
- **Tags** -- annotated and lightweight tags listed with their messages, created, deleted, and pushed one at a time or all at once, with the name validated by git before the write
- **Remotes** -- add, rename, remove, and re-point remotes from the Branches tab
- **Branch extras** -- rename a branch, set or clear its upstream, compare two branches, and delete a branch on the remote behind a typed confirmation
- **File history and blame** -- per-file history and a blame view with jump-through to the commit, reachable from a file row in Changes or from the History tab
- **Hunk staging** -- stage, unstage, or discard an individual hunk from the diff viewer, including the unstaged side of a staged rename
- **Stash depth** -- stash with a message and optionally include untracked files, and read a stash's diff before applying it
- **Changes follows the working tree** -- an external edit reaches the open detail page: the file lists refresh when the watcher reports a change (held while an operation runs, never interrupting one), and a Refresh button and `F5` re-read on demand
- **Internals tab** -- worktrees (add, remove, prune stale entries), submodules (init, update, sync, deinitialize), and a `.gitignore` editor with a path tester that says which rule decides a path
- **Commit graph** -- branch-lane visualization of history, paged without the lanes shifting under you
- **History paging** -- load older commits beyond the first page
- **Side-by-side diff** -- a two-column layout with word-level intra-line highlights, toggled from the diff viewer and remembered across sessions; both layouts share one hunk numbering and one selection
- **Multi-select file operations** -- stage, unstage, or discard several files at once in Changes, with the file count named in the confirmation and the outcome reported per action
- **Commit message helper** -- live subject and body counters against the 50/72 guides, reported and never enforced: no message is truncated or refused
- **Project templates** -- New Project offers empty, documentation, PowerShell script, .NET console app, and .NET class library layouts, each naming exactly the files it will create
- **Portfolio export** -- export every discovered project as CSV, JSON, or a standalone HTML page, written atomically to a path you choose
- **Dashboard pinning, density, and quick actions** -- pin projects to the top of the grid, switch card density, run Fetch / Pull / Push inline on a card, and deep-link from a card chip to the matching detail tab
- **Command palette verbs and cross-repo search** -- per-project Fetch / Pull / Push / Open actions from `Ctrl+K`, plus a bounded `git grep` fan-out that finds text and filenames across every repository
- **Shortcut cheat sheet** -- `?` lists every keyboard gesture the app registers, from one table that is also what the app binds
- **First-run empty states** -- distinct guidance for a missing projects folder, a folder with no repositories, and a filter that matches nothing
- **Live-apply settings** -- theme, refresh interval, watcher toggle, and root path take effect on save instead of on the next launch, and a rescan a refresh request overtook is re-queued and reported
- **Portable archive** -- a zip alongside the installer. A `portable.marker` beside the executable keeps all app state in a `data` folder next to it; when that folder is not writable the app says so and falls back to the per-user locations for that session
- **Safety rails under every destructive operation** -- a clean-tree gate that offers to stash first, typed confirmation for whole-history rewrites / force pushes / repository deletion / remote-branch deletion, a crash journal, and a repository lease that keeps the file watcher, refresh timer, discovery scan, and Sync All out of a repository while a long operation runs
- **MIT license** and a third-party notices file covering every binary the installer and the portable archive redistribute; the packaging step fails when a redistributed component is unlisted
- Build-and-test on every push and pull request, and a release workflow that publishes the installer and the portable archive for a tag

### Changed
- The per-repository work area is eleven tabs -- Overview, Changes, History, Branches, Issues, Pull Requests, Stashes, Actions, Releases, Repo, Internals -- with `Ctrl+1`–`Ctrl+9` and `Ctrl+0` switching the first ten
- **Accessibility** -- every list row, outcome, and state is announced: cards are named list items in a grid with no decorative chrome in the keyboard path, a file's status is announced as a word rather than a letter, bulk-operation tallies are announced separately from polite progress milestones, and every status color holds a 4.5:1 contrast floor (the large-text floor in dark theme)
- **Performance** -- a card's state now costs four git processes instead of seven, repository triage and the hidden-repository count run off the UI thread, the projects root is read once per scan, each repository spends one timeout budget rather than one per git call, and the rewrite engine streams to disk rather than holding a repository in memory
- Every git invocation runs through one non-interactive environment with the message locale pinned, so git's output is parsed as the app expects on any machine
- Every operation reports what it did; a silent list refresh is no longer how success or failure is communicated

### Fixed
- A repository with no remote no longer reports "No remote" over uncommitted changes -- both states are shown
- Window position and size restore correctly on mixed-DPI setups: the rect is saved and clamped in device pixels, re-asserted when a DPI change overwrites it, and captured from whatever state the window is closed in
- A confirmed operation is bound to the repository the confirmation named, so switching projects mid-confirmation can no longer apply it elsewhere, and a dropped operation says so
- A repository whose history cannot be walked reports that instead of reading as a repository with no commits
- Links in rendered README and CHANGELOG markdown are clickable and reachable from the keyboard, and open the parsed target after disclosing its real host
- A repository's `commit.cleanup` setting no longer rewrites a commit message typed in the app
- Issues and Pull Requests answer honestly: a repository with no GitHub remote says so instead of showing an empty list with enabled compose buttons, and a failed fetch leaves the list already on screen standing instead of reading as "none open"
- The command palette keeps its selection when search results arrive late

## [1.2.0] - 2026-07-17

The desktop client release — the dashboard becomes a full local git client.

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
- **Remote discovery** -- your GitHub repos with no local clone appear as one-click-cloneable "Cloud" cards (toggle in Settings)
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

## [1.2.0-legacy] - 2026-03-28

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
