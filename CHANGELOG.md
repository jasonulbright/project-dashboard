# Changelog

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
