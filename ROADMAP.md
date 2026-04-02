# Project Dashboard — Roadmap

**Product:** Project Dashboard
**Repo:** `c:\projects\projectdashboard\` (GitHub: `jasonulbright/project-dashboard`, public)
**Stack:** .NET 10, WPF-UI 4.2.0, CommunityToolkit.Mvvm

---

## v1.0 — Current (STABLE)

### Features
- Dashboard card grid with git status, category, visibility, note prefix icons
- Project detail view with manifest editor, git history, GitHub issues, markdown rendering
- Sidebar navigation with expandable project list
- New Project wizard (folder, README, CHANGELOG, .gitignore, manifest, git init)
- Hidden projects, context menus, sorting, filtering, search
- GitHub integration via `gh` CLI (visibility, issues, commit links)
- Discovery cache in `%LOCALAPPDATA%\ProjectDashboard\`
- Window state persistence

### Data Storage
- `%LOCALAPPDATA%\ProjectDashboard\settings.json` — user preferences
- `%LOCALAPPDATA%\ProjectDashboard\discovery-cache.json` — project scan cache
- `project-manifest.json` — per-repo, committed to version control

---

## v1.1 — GitHub Remote Discovery

### Problem
Dashboard currently only discovers projects from a local root directory scan. Repos that exist on GitHub but are not cloned locally are invisible. The project manifest files are now committed to repos, making them readable via GitHub API.

### Architecture

**Discovery flow (on launch):**
1. Scan local root directory for `project-manifest.json` files (existing behavior, instant, offline)
2. If `gh` is authenticated, call `gh api user/repos --paginate --json name,private,description,htmlUrl` to discover all user repos
3. For each remote repo not found locally, fetch manifest: `gh api repos/{owner}/{repo}/contents/project-manifest.json`
4. Merge: local repos get full git status + manifest. Remote-only repos get manifest + "Cloud" badge.
5. Cache merged results to `%LOCALAPPDATA%\ProjectDashboard\discovery-cache.json` (never committed, never in repo)

**Security:**
- GitHub username discovered at runtime via `gh api user --jq .login` — never hardcoded
- `repos.json` registry file does NOT exist — discovery is live + cached in AppData only
- Discovery cache contains repo names (including private) — stored in `%LOCALAPPDATA%` only
- Cache file is NOT in the project directory, NOT in any git repo, NOT committable
- `gh` CLI manages authentication — dashboard never reads, stores, or transmits tokens
- If `gh` is not authenticated, remote discovery silently skips — local-only mode

**UI changes:**
- New badge: "Cloud" (blue) for remote-only repos alongside existing Public/Private/Local
- Remote-only repos show: name, description, visibility, GitHub link. No git status (not cloned).
- "Clone" action on remote-only repo cards — clones to local root, then full local discovery
- Settings: toggle "Enable GitHub Discovery" (default: on)

### Data Storage (unchanged paths, new content)
All private data stays in `%LOCALAPPDATA%\ProjectDashboard\`:

| File | Contains | Committed to git? |
|------|----------|-------------------|
| `settings.json` | Root path, refresh interval, theme, GitHub discovery toggle | NO — AppData only |
| `discovery-cache.json` | All discovered projects (local + remote, includes private repo names) | NO — AppData only |

**For future packaged installs (MSI/MSIX):**
- Per-user config: `%LOCALAPPDATA%\ProjectDashboard\` (settings, cache)
- Per-machine shared config (if needed): `%PROGRAMDATA%\ProjectDashboard\`
- No data stored in the install directory or repo directory

---

## v2.0 — Packaging + Distribution

### Installer
- MSI or MSIX package
- Per-machine install to `%PROGRAMFILES%\ProjectDashboard\`
- Shortcut in Start Menu
- File association for `project-manifest.json` (open in dashboard)

### Auto-Update
- Check GitHub releases for new versions on launch
- Download and apply update (or notify user)

### Additional Features
- Dashboard widgets: total repos, dirty count, open issues across all repos
- Bulk operations: commit all dirty, push all, pull all
- Project templates: pre-configured manifests for common project types
- Export portfolio: generate HTML/PDF summary of all projects

---

## Testing Strategy

### v1.0
- Build validation: zero warnings, zero errors
- Manual testing against live repos

### v1.1
- GitHub API mock: test discovery with mock `gh` responses
- Offline mode: verify graceful degradation when `gh` unavailable
- Cache isolation: verify no cache files exist outside `%LOCALAPPDATA%`
- Security: verify no hardcoded usernames, no tokens in memory beyond `gh` process lifetime
