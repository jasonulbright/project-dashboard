# Project Dashboard

A Fluent 2 WPF desktop application for managing and monitoring local git repositories. Scans a configurable root directory, reads git status, changelogs, readmes, and GitHub issues, and presents everything in a unified dashboard.

Built with WPF-UI (Fluent 2 design system) on .NET 10. No database, no cloud dependencies, no telemetry. Works fully offline with graceful GitHub degradation.

## Features

- **Dashboard view** -- card grid showing all projects with version, git status (clean/dirty), category, last commit time, and open GitHub issue count
- **Project detail view** -- README and CHANGELOG display, git commit history, GitHub issues, and an inline manifest editor for project metadata
- **Settings** -- configurable projects root path, refresh interval, excluded directories, dark/light theme toggle, GitHub CLI auth status
- **Manifest system** -- per-project `project-manifest.json` stores human-authored metadata (project type, status, category, validation schedule, notes) that can't be derived from git
- **GitHub integration** -- open issue counts on dashboard, full issue list in detail view, via `gh` CLI (no API keys needed)

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
  "ProjectType": "mecm-tool",
  "Status": "active",
  "Category": "MECM",
  "ValidationSchedule": "weekly",
  "Notes": "Browser packagers need daily validation"
}
```

| Field | Values |
|---|---|
| ProjectType | mecm-tool, powershell-script, web-app, game, framework, library, unknown |
| Status | active, maintenance, archived, experimental |
| Category | MECM, Web, Games, Infrastructure, Utilities, Uncategorized |
| ValidationSchedule | daily, weekly, monthly, none |

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
