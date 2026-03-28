# Changelog

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
