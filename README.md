# Project Dashboard

A Fluent 2 WPF desktop application for managing and monitoring local git repositories. Scans a configurable root directory, reads git status, changelogs, readmes, and GitHub issues, and presents everything in a unified dashboard.

Built with WPF-UI (Fluent 2 design system) on .NET 10. No database, no cloud dependencies, no telemetry. Works fully offline with graceful GitHub degradation.

## Features

- **Dashboard view** -- card grid with description, version, sync status (green/yellow/red), category, project type, validation schedule, visibility (public/private/local), and tasks badge
- **Project detail view** -- manifest editor, notes, git commit history, GitHub issues, and collapsible README/CHANGELOG with markdown rendering (headers, bold, italic, code blocks, bullets, numbered lists, images, clickable links, tables)
- **Sidebar navigation** -- expandable project list with direct click-to-detail, hidden projects folder
- **New Project** -- creates folder with README, CHANGELOG, .gitignore, manifest, git init
- **Hidden projects** -- right-click hide/unhide, filter badge, sidebar nav item
- **Sorting** -- by name, last commit, status, dirty first, category
- **Filtering** -- by category, search text, and clickable summary badges (Total, Dirty, Tasks, Issues, Hidden)
- **Manifest system** -- per-project `project-manifest.json` with description, project type, status, category, validation schedule, and notes (TODO/TASK/INFO/BUG prefixes)
- **GitHub integration** -- repo visibility, open issues, clickable commit hashes and issue numbers (open in browser), via `gh` CLI
- **Window state** -- size, position, and pane collapse state persisted across restarts
- **Discovery cache** -- instant relaunch from cached data, manual refresh bypasses cache
- **Error resilience** -- global error handler shows dialog instead of crashing

## Requirements

| Requirement | Details |
|---|---|
| OS | Windows 10 21H2+ or Windows 11 |
| .NET | 10.0 runtime |
| Git | `git.exe` on PATH |
| GitHub CLI | `gh` on PATH (optional -- GitHub features degrade gracefully) |

## Build and Run

```bash
git clone https://github.com/jasonulbright/project-dashboard.git
cd project-dashboard
dotnet build
dotnet run --project src/ProjectDashboard/ProjectDashboard.csproj
```

## Configuration

On first launch, the app scans `C:\projects` for git repositories. Change the root path in Settings.

### project-manifest.json

Each repo can optionally contain a `project-manifest.json` at its root. The dashboard reads this for metadata that can't be derived from git:

```json
{
  "Description": "MECM application packaging automation with WinForms GUI",
  "ProjectType": "mecm-tool",
  "Status": "active",
  "Category": "MECM",
  "ValidationSchedule": "weekly",
  "Notes": "TODO: PSADT scaffolding\nINFO: 115 packagers, schema v2"
}
```

| Field | Values |
|---|---|
| Description | Short one-liner (under 80 chars), shown on cards and detail header |
| ProjectType | mecm-tool, powershell-script, web-app, game, framework, library, dashboard, unknown |
| Status | active, maintenance, archived, experimental |
| Category | MECM, Web, Games, Infrastructure, Utilities, Uncategorized |
| ValidationSchedule | daily, weekly, monthly, none |
| Notes | Newline-separated entries with prefixes: TODO:, TASK:, BUG:, INFO: |

Edit manifests directly in the Project Detail view.

## Architecture

```
src/ProjectDashboard/
    App.xaml(.cs)              # DI host, theme resources
    Models/                    # ProjectInfo, GitStatus, ProjectManifest, AppSettings
    Services/                  # GitService, GitHubService, ProjectDiscoveryService, MarkdownService
    ViewModels/                # MVVM ViewModels (CommunityToolkit.Mvvm)
    Views/Windows/             # FluentWindow with NavigationView
    Views/Pages/               # Dashboard, ProjectDetail, Settings
    Helpers/                   # Value converters
```

### Stack

- **WPF-UI** (lepoco/wpfui) -- Fluent 2 controls, Mica backdrop, dark/light theming
- **CommunityToolkit.Mvvm** -- source-generated ObservableObject, RelayCommand
- **Microsoft.Extensions.Hosting** -- DI container, hosted services
- **System.Text.Json** -- settings and manifest serialization (built into .NET 10)

4 NuGet packages. No database. No native dependencies.

### Template Reuse

This project is designed as a reusable Fluent UI template. To convert a future app:
1. Copy the template shell (App.xaml, MainWindow, ApplicationHostService, SettingsPage)
2. Create new Models, Services, ViewModels, Views for the app's domain
3. Update navigation items in MainWindow
4. Done -- full Fluent 2 app with zero WPF-UI boilerplate to figure out

## License

This project is provided as-is for personal and educational use.
