# Project Dashboard

A Fluent 2 WPF desktop application for managing local git repositories — a GitHub Desktop-style client for your whole projects folder. Scans a configurable root directory, reads git status, changelogs, readmes, and GitHub issues/PRs, and gives you staging, commits, branches, history, stashes, and one-click clone across every repo in one window.

Built with WPF-UI (Fluent 2 design system) on .NET 10. No database, no cloud dependencies, no telemetry. Git and GitHub access are delegated to `git.exe` and the `gh` CLI as subprocesses — the app never reads, stores, or transmits tokens. Works fully offline with graceful GitHub degradation.

![Project Dashboard](screenshot.png)

## Features

### Dashboard
- **Card grid** — description, version, sync status, current branch and ahead/behind, category, project type, validation schedule, visibility, and note prefix icons (TASK/BUG/WAIT with counts)
  - Sync glyph: checkmark = clean, pencil = uncommitted changes, cloud-off = no remote, warning = needs attention (conflict / mid-merge / rebase / detached), question = status unavailable
  - Visibility glyph: globe = public, lock = private, desktop = local (no remote), question = unknown
- **Sidebar navigation** — Dashboard / Public / Private / Non-Local / Hidden / Cloud filters, plus an expandable project list with direct click-to-detail
- **Summary chips** (clickable filters) — Total, Dirty, Tasks, Issues, Cloud, plus Remote-mismatch and Needs-metadata when relevant
- **Sorting & search** — by name, last commit, status, dirty-first, category; text search
- **Command palette** — `Ctrl+K` fuzzy-jumps to any project or action (refresh, new, clone, sync all, settings, filters)
- **Clone** — pick from your GitHub repositories (type-to-filter) or paste any URL; clones into the projects root
- **Sync All** — fetches every clean repo, fast-forwards the ones behind and pushes the ones ahead; dirty, diverged, detached, and conflicted repos are skipped and reported (never a surprise merge)
- **New Project** — creates a folder with README + CHANGELOG and runs git init + first commit (metadata stored out-of-source)
- **Auto-refresh** — a debounced file watcher updates a card within a couple of seconds of an on-disk edit, commit, or branch switch

### Per-repository work area (detail view)
- **Overview** — manifest editor, icon-prefixed notes with Edit/Done toggle, collapsible README/CHANGELOG with native markdown rendering
- **Changes** — staged / unstaged / conflicted file lists, per-file native diff viewer (parsed from `git diff`, no web view), stage/unstage per file or all, discard (confirmed) and untracked-delete (confirmed), a commit box with amend (prefills the last message), `Ctrl+Enter` to commit
- **History** — recent commits, per-commit changed-file list and diff, and a click-through to the commit on GitHub
- **Branches** — local branches with upstream tracking and ahead/behind, create, switch, and safe delete (refuses unmerged)
- **Issues** — open issues as a full list (number, title, author, labels, updated); Enter or double-click opens on GitHub
- **Pull Requests** — open PRs with draft state and an aggregated checks verdict (passing / failing / pending); opens on GitHub
- **Stashes** — list, apply, pop, and drop (drop is confirmed)
- **Branch bar** — current branch, ahead/behind, Fetch / Pull (fast-forward only) / Push (auto-sets upstream)
- **State banner** — surfaces merge / rebase / cherry-pick / revert / bisect / detached-HEAD / conflicts loudly, with an "Open in Terminal" escape hatch (the app does not build a merge tool)

### Platform
- **GitHub integration** via the `gh` CLI — repo visibility and open issue/PR counts fetched in one batched GraphQL call per ~25 repos; clickable commit/issue/PR links; in-app bug/feature filing (pre-filled, labeled new-issue page). A dashboard banner offers in-app sign-in when gh is missing or signed out
- **Remote discovery** (ROADMAP v1.1) — GitHub repos with no local clone appear as "Cloud" cards you can clone in one click (toggle in Settings)
- **Keyboard accessible** — full no-mouse operation: `Ctrl+K` palette, arrow-key pane navigation, Tab/arrows/Enter through the card grid, `Ctrl+1..7` for detail tabs, Alt+Left / Backspace to go back, keyboard-activatable chips and rows, visible focus rings
- **Window state** — size, position, and pane collapse state persisted across restarts
- **Discovery cache** — instant relaunch from cached data; manual Refresh and Settings → Sync Now bypass the cache
- **Error resilience** — global handlers show a dialog or banner instead of crashing; failures logged to `%LOCALAPPDATA%\ProjectDashboard\log.txt`

## Requirements

| Requirement | Details |
|---|---|
| OS | Windows 10 21H2+ or Windows 11 |
| .NET | 10.0 Desktop Runtime |
| Git | `git.exe` on PATH (or a standard install location) |
| GitHub CLI | `gh` on PATH (optional — GitHub features degrade gracefully) |

## Install

Download `ProjectDashboard-Setup-*.exe` from [Releases](https://github.com/jasonulbright/project-dashboard/releases) and run it. Per-user install (no admin, no signing). Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) — the installer checks for it and links the download if missing.

## Build and Run

```bash
git clone https://github.com/jasonulbright/project-dashboard.git
cd project-dashboard
dotnet build
dotnet run --project src/ProjectDashboard/ProjectDashboard.csproj
```

## Configuration

On first launch, the app scans `C:\projects` for git repositories. Change the root path in Settings.

Settings also has: theme (light/dark), refresh interval, excluded directories, a `gh.exe` path picker, and toggles for GitHub discovery (Cloud cards) and on-disk auto-refresh.

### Data storage

All app state lives outside your repositories, so source trees stay source-only:

| Path | Contents |
|---|---|
| `%LOCALAPPDATA%\ProjectDashboard\settings.json` | User preferences and window state |
| `%LOCALAPPDATA%\ProjectDashboard\discovery-cache.json` | Project scan cache (may include private repo names — never committed) |
| `%LOCALAPPDATA%\ProjectDashboard\log.txt` | Diagnostic log |
| `%APPDATA%\ProjectDashboard\manifests.json` | Per-project metadata index (roams with the user profile) |

Setting the `PD_DATA_DIR` environment variable relocates all of the above under one directory (portable mode).

### Project metadata

Per-project metadata that can't be derived from git is stored in the path-keyed `manifests.json` index above and edited in the detail view. Each entry:

```json
{
  "Description": "MECM application packaging automation with WinForms GUI",
  "ProjectType": "mecm-tool",
  "Status": "active",
  "Category": "MECM",
  "ValidationSchedule": "weekly",
  "Notes": "TASK: PSADT scaffolding\nINFO: 115 packagers, schema v2"
}
```

| Field | Values |
|---|---|
| Description | Short one-liner (under 80 chars), shown on cards and detail header |
| ProjectType | mecm-tool, powershell-script, web-app, game, framework, library, dashboard, unknown |
| Status | active, maintenance, archived, experimental |
| Category | MECM, Web, Games, Infrastructure, Utilities, Uncategorized |
| ValidationSchedule | daily, weekly, monthly, none |
| Notes | Newline-separated entries with prefixes: TASK:, BUG:, WAIT:, PLAN:, INFO: |

> Legacy `project-manifest.json` files at a repo root are auto-imported into the index on first scan, then no longer needed.

## Architecture

```
src/ProjectDashboard/
    App.xaml(.cs)              # DI host, global error handlers, theme resources
    Models/                    # ProjectInfo, GitStatus, WorkingState, FileDiff, BranchInfo, GitRemote, ...
    Services/                  # ProcessRunner, GitService, GitHubService, ProjectDiscoveryService,
                               #   ProjectWatcherService, ManifestStore, MarkdownService, AppPaths, Log
    ViewModels/                # MVVM ViewModels (CommunityToolkit.Mvvm)
    Views/Windows/             # FluentWindow with NavigationView + command palette
    Views/Pages/               # Dashboard, ProjectDetail (tabbed work area), Settings
    Helpers/                   # Value converters
```

Every subprocess goes through one `ProcessRunner`: both pipes drained concurrently (no deadlocks), UTF-8 decoding (no mojibake on unicode paths/authors), `ArgumentList` quoting, timeout + cancellation, and non-zero exits surfaced rather than swallowed.

### Stack

- **WPF-UI** (lepoco/wpfui) — Fluent 2 controls, Mica backdrop, dark/light theming
- **CommunityToolkit.Mvvm** — source-generated ObservableObject, RelayCommand
- **Microsoft.Extensions.Hosting** — DI container, hosted services
- **System.Text.Json** — settings and manifest serialization (built into .NET 10)

4 NuGet packages. No database. No native dependencies. Git and GitHub go through `git.exe` and `gh` — no libgit2, no REST tokens.

## License

This project is provided as-is for personal and educational use.
