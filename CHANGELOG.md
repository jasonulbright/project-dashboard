# Changelog

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
