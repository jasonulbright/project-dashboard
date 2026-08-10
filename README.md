# Project Dashboard

[![CI](https://github.com/jasonulbright/project-dashboard/actions/workflows/ci.yml/badge.svg)](https://github.com/jasonulbright/project-dashboard/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/jasonulbright/project-dashboard?label=release)](https://github.com/jasonulbright/project-dashboard/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/jasonulbright/project-dashboard/total?label=downloads)](https://github.com/jasonulbright/project-dashboard/releases)
[![Platform](https://img.shields.io/badge/platform-Windows-0078D4)](#requirements)
[![License](https://img.shields.io/github/license/jasonulbright/project-dashboard)](LICENSE)

A Fluent 2 WPF desktop application for managing local git repositories — a full desktop git client for your whole projects folder. Scans a configurable root directory, reads git status, changelogs, readmes, and GitHub issues/PRs, and gives you staging, commits, branches, history, stashes, tags, remotes, worktrees, and one-click clone across every repo in one window. History rewriting, commit surgery, and GitHub repository administration are in the app too — each one behind a backup, a preview, and a confirmation.

Built with WPF-UI (Fluent 2 design system) on .NET 10. No database, no cloud dependencies, no telemetry. Git and GitHub access are delegated to `git.exe` and the `gh` CLI as subprocesses — the app never reads, stores, or transmits tokens. Works fully offline with graceful GitHub degradation.

![Project Dashboard](screenshot.png)

## Features

### Dashboard
- **Card grid** — description, version, sync status, current branch and ahead/behind, category, project type, validation schedule, visibility, and note prefix icons (TASK/BUG/WAIT with counts)
  - Sync glyph: checkmark = clean, pencil = uncommitted changes, cloud-off = no remote, warning = needs attention (conflict / mid-merge / rebase / detached), question = status unavailable
  - Visibility glyph: globe = public, lock = private, desktop = local (no remote), question = unknown
- **Sidebar navigation** — Dashboard / Public / Private / Non-Local / Hidden filters, plus an expandable project list with direct click-to-detail
- **Summary chips** (clickable filters) — Total, Dirty, Tasks, Issues, Cloud, and Hidden, plus Remote-mismatch and Needs-metadata when relevant
- **Sorting & search** — by name, last commit, status, dirty-first, category; text search
- **Pinning and density** — pin the repos you live in to the top of the grid, and switch the cards between comfortable and compact
- **Card quick actions** — Fetch / Pull / Push inline on a card, plus click-through from a card chip straight to the matching detail tab
- **Command palette** — `Ctrl+K` fuzzy-jumps to any project, runs a global action (refresh, new, clone, sync all, settings, filters), runs a verb on a named project without navigating to it (Fetch, Pull, Push, open the folder, open a terminal, copy the path, jump to Changes), and searches text and filenames across every repository
- **Search scope** — the palette's file search reads tracked files by default and switches to tracked + untracked, or to every file including the ones your ignore rules exclude (`Alt+1`/`Alt+2`/`Alt+3`, or `Ctrl+Shift+S` to step through them). Every row says what its file is, the results header names the scope in force, and the scope returns to tracked each time the palette opens — the widest one reads build output. `Ctrl+F` on a project runs the same search against that one repository
- **Clone** — pick from your GitHub repositories (type-to-filter) or paste any URL; clones into the projects folder you picked as the default. The picker holds your 200 most recently updated repositories and says so when there are more, and a list that could not be read says that rather than showing an empty picker
- **Sync All** — fetches every clean repo, fast-forwards the ones behind and pushes the ones ahead; dirty, diverged, detached, and conflicted repos are skipped and reported (never a surprise merge)
- **New Project** — a template picker (empty, documentation, PowerShell script, .NET console app, .NET class library) that names exactly the files it will create, then git init + first commit (metadata stored out-of-source)
- **Export** — write the whole inventory to CSV, JSON, or a standalone HTML page
- **Auto-refresh** — a debounced file watcher updates a card within a couple of seconds of an on-disk edit, commit, or branch switch
- **Shortcut cheat sheet** — `?` lists every keyboard gesture the app registers
- **Empty states** — no projects folder configured yet, every configured folder unreachable, folders with no repositories, and a filter that matches nothing each get their own explanation and next step

### Per-repository work area (detail view)
- **Overview** — manifest editor, icon-prefixed notes with Edit/Done toggle, collapsible README/CHANGELOG with native markdown rendering
- **Changes** — staged / unstaged / conflicted file lists, per-file native diff viewer (parsed from `git diff`, no web view) in unified or side-by-side layout with word-level intra-line highlights, stage/unstage per file or all, multi-select for batch stage/unstage/discard, per-hunk stage/unstage/discard, discard (confirmed) and untracked-delete (confirmed), ignore a file or its whole extension straight from its row — written to this repository's own `.gitignore`, and refused with the reason when the path is tracked or a rule already covers it — a commit box with amend (prefills the last message) and live 50/72 subject/body counters, `Ctrl+Enter` to commit. A repository configured to sign its commits is marked as such in the commit box, and the first commit of the session asks whether to sign as configured — accepting that an uncached passphrase can leave git waiting on a prompt no window here shows — or to commit without signing for as long as the project is open. The answer is never persisted and the repository's configuration is never written, so nothing is ever signed or left unsigned without being asked. Tags carry their own separate answer, because git's two settings are independent
- **History** — commits with paging beyond the first page, per-commit changed-file list and diff, per-file history and blame with jump-through to the commit, a click-through to the commit on GitHub, and the entry points for tags, the reflog, the commit graph, backups, deep clean, and history rewriting
- **Branches** — local branches with upstream tracking and ahead/behind, create, switch, and safe delete (refuses unmerged); rename a branch, set or clear its upstream, compare two branches, check a branch out from a remote under a name you choose, delete a branch on the remote (typed confirmation); and a remotes panel to add, rename, remove, or re-point a remote, and to prune the tracking refs a remote no longer has after showing you exactly which ones would go
- **Issues** — issues as a full list (number, title, state, author, labels, updated) with an open/closed/all filter, a milestone filter, and a search box that takes GitHub search syntax; all three are applied by GitHub, not to the rows already read. A milestone in force is named in every line describing the list — `All 12 open issues in milestone “v2.0” shown.` — beside that milestone's own progress (`8 of 10 closed`), and a search that names a state or a milestone of its own says which picker it overruled. The list says how deep it has read — `All 12 open issues shown.` when that is the whole answer, and how many are shown with a load-more button when there may be more; new issues can be filed into a milestone from the compose panel; Enter or double-click opens on GitHub
- **Pull Requests** — PRs with draft, closed and merged state and an aggregated checks verdict (passing / failing / pending), the same open/closed/all filter, search, depth line and load-more as Issues; opens on GitHub
- **Stashes** — list, apply, pop, and drop (drop is confirmed); stash with a message and optionally include untracked files, and read a stash's diff before applying it
- **Actions** — workflow runs with status/conclusion, branch, event and elapsed time, filtered by workflow, branch and status — all three applied by GitHub, not to the rows already read — with the same depth line and load-more as Issues (`All 12 runs of “CI” on main shown.`). Per-run jobs and steps, with the pane saying how much of a large matrix it holds (`Showing the first 100 of 137 jobs`); re-run all or failed jobs, cancel a running run (both confirmed), and open on GitHub. A run's whole log opens in a read-only pane with find-next/previous, copy, and save-to-a-file-you-choose; a log too large to hold is cut off at a stated size and says so on screen, in the copy, and in the saved file rather than passing a prefix off as the whole run
- **Releases** — releases with draft/prerelease state, date and asset count; release notes rendered natively; create a release from an existing tag (title, notes, draft/prerelease); delete a release (confirmed); download an asset to a location you choose
- **Repo** — description, homepage and topics; feature toggles (issues, wiki, projects); default branch (confirmed) and visibility (confirmed by typing the full `owner/name`); this repository's unread notifications with explicit mark-read; and, only when switched on in Settings, a danger zone that can delete the repository on GitHub — typed `owner/name` confirmation, local files untouched
- **Internals** — the worktrees sharing this repository (add, remove, prune stale entries), the submodules it declares (init, update, sync, deinitialize) with the selected one's distance from the commit this repository records (`2 commits ahead, 0 commits behind` — or *divergence unknown* when the comparison cannot be made, never a zero standing in for it) shown above the buttons that would close that gap, and a `.gitignore` editor with a path tester that says which rule decides a path
- **Health** — what this app can tell you about one repository, with the cost of each answer stated. Opening the tab runs the local checks: the git it found, every lock file under the git directory (reported, never removed), the object store's size, the signing configuration as configuration, the hooks that would run and where they live, whether LFS is set up and whether its filter is installed, the remotes, and the backups on disk. Nothing else runs until you press its own button — an object connectivity walk, a full read of every object, a reachability probe per remote, verification of every backup bundle, and a ranking of the largest objects with a hand-off into the rewrite wizard's purge field. A clean connectivity pass reports *connectivity clean; object contents not verified*, never *healthy*; a check nobody ran reads as *not run*, never as clear; and every deep result carries the moment it was taken. Every read is read-only, refuses while another operation holds the repository, is cancellable, and stops when you leave the page
- **Branch bar** — current branch, ahead/behind, Fetch / Pull (fast-forward only) / Push (auto-sets upstream)
- **State banner** — surfaces merge / rebase / cherry-pick / revert / bisect / detached-HEAD / conflicts loudly, with an "Open in Terminal" escape hatch (the app does not build a merge tool)

![Changes tab with the native diff viewer](docs/screenshots/changes.png)

### History editing
- **Rewrite wizard** — replace text in file contents, remove a path from history, rewrite commit and tag messages, or rewrite an author/committer identity; scoped to all history, glob patterns, exact paths, specific commits, or a commit range. A dry run into a scratch copy reports what would change and verifies the result before anything is applied, and a verified backup is taken before the real run. The verification report calls a scrub clean only where its coverage was total — a check that could not run is reported as a gap, not as a pass. Nothing is pushed.
- **Plan a history edit** — reorder, drop, squash, and reword commits over a range as one replay, with a live preview of the resulting history and a refusal (with the reason) for any plan git cannot produce
- **Commit surgery** — reword any commit (not only the tip), squash into the previous commit, drop a commit, inject staged changes into an older one, and reset (soft / mixed / hard), revert, or cherry-pick from the commit list
- **Tags** — every tag with its kind, target commit and date; create a lightweight or annotated tag on the commit the surface names, delete one (confirmed, and reported as the local-only thing it is), check one out as a new branch, and push one tag or all of them to a remote you pick — a push adds the tag there and removes nothing, and a tag a remote's protection refuses says so rather than failing without explanation. A repository configured to sign its tags is marked as such, and the first tag of the session asks the same question the commit box does — with the lightweight case named for what it is, since git will not create a lightweight tag while tag signing is on
- **Backups, reflog, and force push** — browse and restore the backups taken before each history operation, take one on demand as a standard or a deep capture, check that a bundle still reads back without restoring it, delete one you no longer want (confirmed, and with what it holds and what it occupies named before you agree), inspect every position the refs have held, and push a rewritten branch only after reviewing exactly what diverged (`--force-with-lease`, at the object id the plan produced, behind a typed confirmation). Every row says which tier took it and what that tier does not hold, and the restore says the same before and after it runs
- **Deep clean** — after a scrub, expire the reflogs and prune the object store so the replaced commits stop being reachable locally. Its own typed confirmation; the backup bundle is kept
- **Recovery** — a history operation interrupted by a crash is detected on the next launch and offers the same one-click restore
- **Operation history** — a per-repository record of what the app attempted and how each attempt ended: rewrites, commit surgery, force pushes, deep cleans, restores, the everyday working, branch, remote, and tag operations, and the fetch/pull/push run from a dashboard card or from Sync All — refusals included. A repository Sync All skipped without attempting anything is not recorded, because nothing was run against it. Each entry keeps the verbatim message the operation reported, links to the backup it took while that bundle is still on disk, and says so when retention has pruned it. The list states its own limits — where the records begin, that older ones rotate out, and that operations run from a terminal were never recorded. Local only; nothing is transmitted

### Safety rollup
- **Safety page** — a portfolio-wide list of findings, reached from the footer beside Settings, grouped by signal: repositories with an interrupted operation, ones git could not read, diverged branches, repositories with no remote, backups on disk, commits that live only in a reflog, project data older than its refresh interval, and uncommitted work. Every row links into the surface that already carries the gates for it — the Backups browser, the reflog viewer, a work-area tab — and the page itself restores, deletes, and rewrites nothing
- **Three costs, stated** — the free signals are computed from the project list already on screen and spawn no git process. Branch and backup listing is one extra read per repository, on an explicit ask. Checking a backup bundle and walking for reflog-only commits reads the object store and runs per repository, on an explicit ask, never on a timer and never as part of a scan. The header always says which of the three have run, so an absence of findings is never mistaken for a clean bill of health
- **Honest about its limits** — a repository nobody has checked reads as *not checked*, never as clear. A repository git could not read is its own finding rather than a silent pass. A count that skipped repositories busy with another operation says how many. A backup check runs the same command a restore runs first, so the row says what the restore would say — and states that it reads the bundle's header and prerequisites, not the packed objects. Reporting nothing interrupted carries the caveat that an unreadable recovery journal also reports nothing. No badge, no toast, no startup prompt: the footer item is inert until you open it

![Rewrite wizard dry run](docs/screenshots/history-rewrite.png)

![Planning a reorder, drop, and squash](docs/screenshots/commit-surgery.png)

### Platform
- **GitHub integration** via the `gh` CLI — repo visibility and open issue/PR counts fetched in one batched GraphQL call per ~25 repos; clickable commit/issue/PR links; in-app bug/feature filing (pre-filled, labeled new-issue page). A dashboard banner offers in-app sign-in when gh is missing or signed out
- **Account and host identity** — Settings lists every account the `gh` CLI holds, per host, with the active one marked and the scopes gh reports; no token is ever requested, shown, or stored. A repository's page names the account its GitHub actions would run as, and says so when the answer is not the obvious one: a remote on a host gh has no session for, a sign-in that failed its own check, or a repository owned by another account you are signed in to while a different one is active. Switching accounts is machine-wide state, so the app never does it — it shows the exact `gh` command for you to run. The read happens when a page opens and on the Re-check buttons; nothing polls
- **Remote discovery** — GitHub repos with no local clone appear as "Cloud" cards you can clone in one click (toggle in Settings); the read behind them is capped, and a scan that hit the cap says so beside the grid rather than letting the Cloud count read as your whole account
- **Safety rails** — every destructive operation stands on an automatic backup, a preview, and a confirmation. Whole-history rewrites, force pushes, repository deletion, and remote-branch deletion require the repository name typed out. While a repo is under a long operation the file watcher, refresh timer, discovery scan, and Sync All leave it alone and catch up afterward
- **Keyboard and screen reader** — full no-mouse operation: `Ctrl+K` palette, `?` cheat sheet, arrow-key pane navigation, Tab/arrows/Enter through the card grid, `Ctrl+1`–`Ctrl+9` and `Ctrl+0` for detail tabs, `Ctrl+F` to find in a repository, Alt+Left / Backspace to go back, keyboard-activatable chips and rows, visible focus rings. Every list row, outcome, and state is named for a screen reader, and status colors hold a 4.5:1 contrast floor
- **Update check** — on launch, at most once a day, the app asks GitHub for this project's latest published release and compares it with the running build. A newer one shows a dismissible notice with a link to its release page; nothing is downloaded, installed, or run. The request carries no account, repository, or usage data, and a Settings toggle turns it off so no request is made at all
- **Live-apply settings** — theme, refresh interval, watcher toggle, and the projects-folder list (paths, order, skip lists, scan depth) take effect when you save, not on the next launch
- **Window state** — size, position, and pane collapse state persisted across restarts, in device pixels so a mixed-DPI setup restores where you left it
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

### Portable

`ProjectDashboard-Portable-*.zip` needs no installation: extract it anywhere and run `ProjectDashboard.exe`. The `portable.marker` file in the extracted folder keeps all app state in a `data` folder beside the executable, so the whole thing moves with a USB stick and leaves no Project Dashboard state in your user profile. Delete the marker to use the standard per-user locations instead. If the extracted folder is not writable, the app says so at startup and stores its state in the standard per-user locations for that session. The .NET 10 Desktop Runtime is still required.

### Verifying a download

Every release carries a `checksums.txt` listing the SHA-256 of both assets, in `sha256sum` layout:

```powershell
(Get-FileHash .\ProjectDashboard-Setup-1.0.0.exe -Algorithm SHA256).Hash.ToLower()
```

Compare the result with the matching line in `checksums.txt`.

## Build and Run

```bash
git clone https://github.com/jasonulbright/project-dashboard.git
cd project-dashboard
dotnet build
dotnet run --project src/ProjectDashboard/ProjectDashboard.csproj
dotnet test
```

The test project lives in `tests/ProjectDashboard.Tests` and is excluded from the installer and the portable archive.

To build the portable archive (publishes into `installer\payload`, then zips it with the marker):

```powershell
pwsh -File installer\build-portable.ps1
```

## Configuration

On first launch, the app scans `C:\projects` for git repositories. Settings holds the full list of projects folders: add as many as you like, reorder them (order breaks the tie when two folders hold a repository of the same name), switch one off without losing it, and choose which one new projects and clones land in. Each folder carries its own skip list and its own scan depth — top level only by default, up to four levels down. A scan stops at each repository and does not look inside it, and it never follows a junction or symlink out of the folder. A folder that is missing or unreadable says so on the dashboard by name; the repositories in the folders that were read still appear.

Settings also has: theme (light/dark), refresh interval, a `gh.exe` path picker, toggles for GitHub discovery (Cloud cards) and on-disk auto-refresh, the update check with a "Check now" button and the last check's outcome, the opt-in that puts the danger zone on a project's Repo tab, and the backups block below. Saving applies every one of them to the running app.

The backups block sets how many backups each repository keeps, whether backups are deep, and shows what every repository's backups occupy right now. Lowering the count takes effect the next time each repository is backed up — "Prune now" applies the saved count to every repository straight away, after a confirmation naming how many backups would go and roughly how much that frees. A standard backup holds every ref and the newest stash entry; a deep backup also keeps commits that only a reflog reaches and every stash entry below the newest, so a later cleanup cannot make them unrecoverable — larger and slower bundles for it. Deep backups preserve those objects; restoring one puts the objects back and does not rebuild the reflog or the stash stack. A project's Backups browser shows which tier each backup was and can override the setting for a single capture.

The update check is the only request the app makes on its own initiative. It is an anonymous public read of `https://api.github.com/repos/jasonulbright/project-dashboard/releases/latest` — no token, no account, no repository or usage data, and no telemetry of any kind. GitHub sees an address, a time, and the app's name and version, the same as visiting the releases page in a browser. Turning the toggle off stops it entirely, on launch and from the button.

### Data storage

All app state lives outside your repositories, so source trees stay source-only:

| Path | Contents |
|---|---|
| `%LOCALAPPDATA%\ProjectDashboard\settings.json` | User preferences and window state |
| `%LOCALAPPDATA%\ProjectDashboard\discovery-cache.json` | Project scan cache (may include private repo names — never committed) |
| `%LOCALAPPDATA%\ProjectDashboard\log.txt` | Diagnostic log |
| `%LOCALAPPDATA%\ProjectDashboard\history\` | Per-repository operation records, one append-only JSONL file each (holds repository paths and verbatim git output — never committed, never transmitted) |
| `%APPDATA%\ProjectDashboard\manifests.json` | Per-project metadata index (roams with the user profile) |

Two things relocate all of the above under a single directory. In the portable build, the `portable.marker` file beside the executable puts them in `data\` next to it. Setting the `PD_DATA_DIR` environment variable points them anywhere you like, and takes precedence over the marker.

### Project metadata

Per-project metadata that can't be derived from git is stored in the path-keyed `manifests.json` index above and edited in the detail view. Each entry carries the metadata itself plus what the repository was when a scan last met it:

```json
{
  "SchemaVersion": 2,
  "Entries": {
    "C:\\projects\\packaging-tool": {
      "Manifest": {
        "Description": "MECM application packaging automation with WinForms GUI",
        "ProjectType": "mecm-tool",
        "Status": "active",
        "Category": "MECM",
        "ValidationSchedule": "weekly",
        "Notes": "TASK: PSADT scaffolding\nINFO: 115 packagers, schema v2"
      },
      "Fingerprint": {
        "RootCommitOids": ["9f1c…"],
        "RemoteUrl": "github.com/owner/packaging-tool",
        "FolderName": "packaging-tool"
      },
      "FirstSeenUtc": "2026-06-01T09:00:00+00:00",
      "LastSeenUtc": "2026-08-09T18:20:00+00:00"
    }
  }
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

> Legacy `project-manifest.json` files at a repo root are auto-imported into the index on first scan, then no longer needed. An index written before `SchemaVersion` — a bare path-to-metadata map — is read as-is and carried up to the shape above by the next write, with every field intact.

#### Metadata follows a repository that moves

Nothing is ever written into your repositories. Instead, each scan records what a repository *is* — its root-commit IDs and its normalized remote URL — beside the metadata. Move or rename a repository, including into a different projects folder, and the next scan recognizes it and carries the description, category, status, validation schedule, and notes across; the pin moves with it. The dashboard says so when it happens.

Adoption is deliberately conservative, because putting one project's notes on another is not a cosmetic mistake:

- A record is re-keyed only when it matches **exactly one** repository and that repository has **no metadata of its own**. Two clones of one upstream, a fork, or two records matching one repository all mean no adoption, and the dashboard says which record it could not place.
- A repository with no commits and no remote carries nothing that identifies it. Folder names are never matched on.
- A repository under a projects folder that is missing, unreadable, or switched off is not gone — an unplugged drive never orphans anything.
- Metadata is never deleted automatically. Records naming folders that are no longer there are listed under **Settings → Project Metadata**, with the description they carry and when they were last seen, and are dropped only by pressing Forget.

The index also records where a record went when its repository moved, so an edit saved from a page opened before the move reaches the record rather than the folder it was opened on. That trail is followed only by the repository the record belongs to — a different repository that later occupies the vacated folder keeps its own metadata — and it is dropped once the record it points at is forgotten.

## Architecture

```
src/ProjectDashboard/
    App.xaml(.cs)              # DI host, global error handlers, theme resources
    Models/                    # ProjectInfo, GitStatus, WorkingState, FileDiff, BranchInfo, GitRemote,
                               #   SideBySideDiff, CommitGraph, ReflogEntry, TagInfo, WorktreeEntry, ...
    Services/                  # ProcessRunner, GitService, GitHubService, ProjectDiscoveryService,
                               #   ProjectWatcherService, ManifestStore, MarkdownService, SettingsService,
                               #   RepoSearchService, CommitGraphService, SubmoduleService,
                               #   ProjectTemplates, PortfolioExport, AppPaths, Log
        History/               #   fast-export/import rewrite engine, path scoping, scrub verification
        Rewrite/               #   coordinator, atomic ref swap, force-push-with-lease
        Surgery/               #   rebase driver, plan compiler, commit injection
        Safety/                #   backup bundles, crash journal, recovery, repo-busy leases, deep clean
    ViewModels/                # MVVM ViewModels (CommunityToolkit.Mvvm); the detail view-model is
                               #   split into one partial per work-area surface
    Views/Windows/             # FluentWindow with NavigationView, command palette, prompt windows
    Views/Pages/               # Dashboard, ProjectDetail (tabbed work area), Settings, and the
                               #   rewrite wizard, backups, reflog, tags, graph, file-history overlays
    Helpers/                   # Value converters, diff row rendering, list focus/selection helpers
tests/ProjectDashboard.Tests/  # xUnit suite (never shipped in an installer or archive)
```

Every subprocess goes through one `ProcessRunner`: both pipes drained concurrently (no deadlocks), UTF-8 decoding (no mojibake on unicode paths/authors), `ArgumentList` quoting, timeout + cancellation, and non-zero exits surfaced rather than swallowed.

### Stack

- **WPF-UI** (lepoco/wpfui) — Fluent 2 controls, Mica backdrop, dark/light theming
- **CommunityToolkit.Mvvm** — source-generated ObservableObject, RelayCommand
- **Microsoft.Extensions.Hosting** — DI container, hosted services
- **System.Text.Json** — settings and manifest serialization (built into .NET 10)

4 NuGet packages. No database. No native dependencies. Git and GitHub go through `git.exe` and `gh` — no libgit2, no REST tokens.

## License

MIT — see [LICENSE](LICENSE).

The binaries redistributed in the installer and portable archive are listed with their
licenses in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
