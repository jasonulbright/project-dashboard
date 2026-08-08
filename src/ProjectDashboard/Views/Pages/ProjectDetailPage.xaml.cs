using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProjectDashboard.ViewModels.Pages;

namespace ProjectDashboard.Views.Pages;

public partial class ProjectDetailPage
{
    private readonly ProjectDetailViewModel _viewModel;
    private readonly Helpers.RelativeTimeConverter _relativeTime = new();

    public ProjectDetailPage(ProjectDetailViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;

        // Rendered FlowDocuments bake brushes at render time; re-render on theme
        // flips so code blocks don't keep the old theme. Unloaded unsubscribes —
        // this page is transient and must not be pinned by a static event.
        Loaded += (_, _) => Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnThemeChanged;

        // Issue/PR bodies render natively into their FlowDocuments when the fetched
        // detail lands. Unloaded unsubscribes — this page is transient.
        Unloaded += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        RenderDocuments();
        RenderIssueConversation();
        RenderPullRequestConversation();
        RenderReleaseNotes();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProjectDetailViewModel.IssueDetail):
                RenderIssueConversation();
                break;
            case nameof(ProjectDetailViewModel.PullRequestDetail):
                RenderPullRequestConversation();
                break;
            case nameof(ProjectDetailViewModel.SelectedRelease):
                RenderReleaseNotes();
                break;
        }
    }

    /// <summary>
    /// Release notes render with images disabled, like issue and PR bodies: the notes of
    /// a repository the reader only browses are third-party text, and a fetch would hand
    /// their author the reader's IP and a read receipt.
    /// </summary>
    private void RenderReleaseNotes()
    {
        var release = _viewModel.SelectedRelease;
        if (release is null)
        {
            ReleaseNotesRich.Document = new FlowDocument();
            return;
        }
        try
        {
            var doc = NewFlowDocument();
            AppendMarkdown(doc, string.IsNullOrWhiteSpace(release.Body) ? "(no release notes)" : release.Body,
                "", allowImages: false);
            ReleaseNotesRich.Document = doc;
        }
        catch
        {
            ReleaseNotesRich.Document = new FlowDocument(new Paragraph(new Run(release.Body) { FontSize = 12 }));
        }
    }

    private void RenderIssueConversation()
    {
        var d = _viewModel.IssueDetail;
        if (d is null)
        {
            IssueConversationRich.Document = new FlowDocument();
            return;
        }
        try
        {
            IssueConversationRich.Document =
                BuildConversationDocument(d.Author, d.CreatedAt, d.Body, d.Comments);
        }
        catch
        {
            IssueConversationRich.Document = new FlowDocument(new Paragraph(new Run(d.Body) { FontSize = 12 }));
        }
    }

    private void RenderPullRequestConversation()
    {
        var d = _viewModel.PullRequestDetail;
        if (d is null)
        {
            PullRequestConversationRich.Document = new FlowDocument();
            return;
        }
        try
        {
            PullRequestConversationRich.Document =
                BuildConversationDocument(d.Author, d.CreatedAt, d.Body, d.Comments);
        }
        catch
        {
            PullRequestConversationRich.Document = new FlowDocument(new Paragraph(new Run(d.Body) { FontSize = 12 }));
        }
    }

    /// <summary>
    /// Renders an issue/PR body and its comment thread entry by entry: each header is
    /// built as a block in code and each body is parsed on its own, so no body text is
    /// ever concatenated into markup the parser reads as a neighbouring entry's header.
    /// </summary>
    private FlowDocument BuildConversationDocument(string author, DateTimeOffset created, string body,
        IReadOnlyList<Models.IssueComment> comments)
    {
        var doc = NewFlowDocument();
        AppendConversationEntry(doc, ConversationHeader(author, created), body);
        foreach (var c in comments)
            AppendConversationEntry(doc, ConversationHeader(c.Author, c.CreatedAt), c.Body);
        return doc;
    }

    /// <summary>
    /// Appends one entry: an author header block followed by that entry's rendered body.
    /// No image loads here, so the body carries no local base path to resolve against.
    /// </summary>
    internal static void AppendConversationEntry(FlowDocument doc, string headerText, string body)
    {
        doc.Blocks.Add(ConversationHeaderBlock(headerText));
        AppendMarkdown(doc, string.IsNullOrWhiteSpace(body) ? "(no content)" : body, "", allowImages: false);
    }

    /// <summary>
    /// The app's own comment header. Its left accent bar is chrome the markdown
    /// renderer never emits — a body line such as "### maintainer • 2 hours ago"
    /// above a "---" rule produces bold text and a bottom border, never this block,
    /// so a forged header cannot pass for a real one in the pane that informs a merge.
    /// </summary>
    internal static Paragraph ConversationHeaderBlock(string headerText)
        => new(new Run(headerText) { FontWeight = FontWeights.SemiBold, FontSize = 15 })
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(108, 164, 217)),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(8, 2, 0, 2),
            Margin = new Thickness(0, 16, 0, 4)
        };

    private string ConversationHeader(string author, DateTimeOffset when)
    {
        var name = string.IsNullOrWhiteSpace(author) ? "(unknown)" : author;
        var rel = _relativeTime.Convert(when, typeof(string), null!, System.Globalization.CultureInfo.CurrentCulture) as string;
        return string.IsNullOrEmpty(rel) ? name : $"{name} • {rel}";
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Paired with the Unloaded unsubscribe: a page re-shown after Unloaded must
        // resubscribe, and the detail already on the view model must render now
        // because no further change notification is coming for it.
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        RenderIssueConversation();
        RenderPullRequestConversation();
        RenderReleaseNotes();

        var project = DashboardViewModel.SelectedProject;
        if (project is null) return;

        ReleaseRemoteImagesForProject(project.FullPath);

        try
        {
            await _viewModel.SetProjectAsync(project);
        }
        catch { }

        RenderDocuments();

        var requested = RequestedTab;
        RequestedTab = null;

        // The deep-linked surface is the one whose data is fetched, rather than the
        // default tab's. Without a deep link the load stands on its own: a project
        // switched to while a lazy tab was active reset that tab's data.
        ApplyPendingTab(WorkTabs, requested, LoadDataForActiveTab);

        // Take keyboard focus so the tab hotkeys (Ctrl+digit) and page key handling work
        // immediately — navigation from a card, the sidebar, or the palette leaves focus
        // on the nav item, outside this page's tunnel.
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => Focus());
    }

    private void RenderDocuments()
    {
        var project = _viewModel.Project;
        if (project is null) return;

        try
        {
            RenderMarkdown(ReadmeRichText, _viewModel.ReadmeText ?? "", project.FullPath);
        }
        catch
        {
            ReadmeRichText.Document = new FlowDocument(new Paragraph(new Run(_viewModel.ReadmeText ?? "(error rendering)") { FontSize = 12 }));
        }

        try
        {
            RenderMarkdown(ChangelogRichText, _viewModel.ChangelogText ?? "", project.FullPath);
        }
        catch
        {
            ChangelogRichText.Document = new FlowDocument(new Paragraph(new Run(_viewModel.ChangelogText ?? "(error rendering)") { FontSize = 12 }));
        }
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    // Keyboard activation for focusable commit/issue rows: Enter/Space fires the focused
    // row's existing left-click command (open on GitHub) without a mouse.
    private void Page_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Each safety pane is modal over the whole page: Esc closes it, and the tab hotkeys stay
        // inert so a digit cannot move the surface behind one. Ordered topmost-first, matching the
        // Grid: the push pane opens over the wizard's result screen, so an Esc there must close
        // the pane and leave the wizard — and its Undo — standing.
        if (TopmostOverlayClose() is { } close)
        {
            if (e.Key == Key.Escape)
            {
                close.Execute(null);
                e.Handled = true;
            }
            else if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key is >= Key.D0 and <= Key.D9)
            {
                e.Handled = true;
            }
            return;
        }

        // Ctrl+1..9,0 jumps between work-area tabs (D0 = 10th). Digits past the live
        // tab count are inert, so this scales as tabs are added without renumbering.
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key is >= Key.D0 and <= Key.D9)
        {
            if (ProjectDetailTabs.TabIndexForDigit(e.Key, WorkTabs.Items.Count) is { } index)
            {
                WorkTabs.SelectedIndex = index;
                if (WorkTabs.SelectedItem is TabItem tab) tab.Focus();
                e.Handled = true;
            }
            return;
        }

        if (e.Key != Key.Enter && e.Key != Key.Space) return;
        if (Keyboard.FocusedElement is not Border border) return;

        var mouseBinding = border.InputBindings.OfType<MouseBinding>().FirstOrDefault();
        if (mouseBinding?.Command?.CanExecute(mouseBinding.CommandParameter) == true)
        {
            mouseBinding.Command.Execute(mouseBinding.CommandParameter);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The close command of the pane currently drawn on top, or null when none is up. The Backups
    /// browser is absent because nothing draws over it and its own key binding closes it.
    /// </summary>
    private System.Windows.Input.ICommand? TopmostOverlayClose() =>
        _viewModel.ForcePushVisible ? _viewModel.CloseForcePushCommand
        : _viewModel.TagsVisible ? _viewModel.CloseTagsCommand
        : _viewModel.ReflogVisible ? _viewModel.CloseReflogCommand
        : _viewModel.RewriteWizardVisible ? _viewModel.CloseRewriteWizardCommand
        : null;

    /// <summary>
    /// The work-area tab a deep link asked for, consumed by the next page to load. A
    /// navigation does not attach the page's content before it returns, so the shell
    /// cannot hold the page it is opening; the tab travels the same way the project it
    /// belongs to does. Consumed once — a later navigation carrying no tab lands on the
    /// page's own default surface.
    /// </summary>
    public static Models.DetailTab? RequestedTab { get; set; }

    /// <summary>
    /// Selects the work-area tab tagged <paramref name="tab"/>, reporting whether the
    /// selection moved. A tab the page does not host, and a tab already selected, leave
    /// the selection where it is.
    /// </summary>
    internal static bool SelectTab(TabControl tabs, Models.DetailTab tab)
    {
        var tags = tabs.Items.OfType<TabItem>().Select(item => item.Tag as Models.DetailTab?);
        if (ProjectDetailTabs.IndexOfTab(tags, tab) is not { } index || index == tabs.SelectedIndex)
            return false;
        tabs.SelectedIndex = index;
        return true;
    }

    /// <summary>
    /// Applies a deep-linked tab and loads the active surface exactly once. A selection
    /// that moves raises SelectionChanged, whose handler already loads the surface it
    /// moved to; loading again spawns a second read of that surface, and the later reply
    /// replaces the collection the first one filled, dropping the selection in it.
    /// </summary>
    internal static void ApplyPendingTab(TabControl tabs, Models.DetailTab? requested, Action loadActiveTab)
    {
        if (requested is { } tab && SelectTab(tabs, tab)) return;
        loadActiveTab();
    }

    /// <summary>Lazy-loads tab data the first time a surface is opened for the current project.</summary>
    private void WorkTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WorkTabs)) return;
        LoadDataForActiveTab();
    }

    private void LoadDataForActiveTab()
    {
        if (WorkTabs.SelectedItem is not TabItem { Tag: Models.DetailTab tab }) return;
        var load = ProjectDetailTabs.LoadForTab(tab, new DetailTabLoadState(
            Branches: _viewModel.Branches.Count > 0,
            Stashes: _viewModel.StashesLoaded,
            PullRequests: _viewModel.PullRequestsLoaded,
            WorkflowRuns: _viewModel.WorkflowRunsLoaded,
            Releases: _viewModel.ReleasesLoaded,
            RepoTab: _viewModel.RepoSettingsLoaded));
        switch (load)
        {
            case DetailTabLoad.Branches:
                _viewModel.LoadBranchesCommand.Execute(null);
                break;
            case DetailTabLoad.Stashes:
                _viewModel.LoadStashesCommand.Execute(null);
                break;
            case DetailTabLoad.PullRequests:
                _viewModel.LoadPullRequestsCommand.Execute(null);
                break;
            case DetailTabLoad.WorkflowRuns:
                _viewModel.LoadWorkflowRunsCommand.Execute(null);
                break;
            case DetailTabLoad.Releases:
                _viewModel.LoadReleasesCommand.Execute(null);
                break;
            case DetailTabLoad.RepoTab:
                _viewModel.LoadRepoTabCommand.Execute(null);
                break;
        }
    }

    // Issue / PR rows: double-click or Enter opens the row on GitHub.
    private void IssueRow_Open(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Models.GitHubIssue issue })
            _viewModel.OpenIssueCommand.Execute(issue);
    }

    private void IssueRow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && sender is ListBoxItem { DataContext: Models.GitHubIssue issue })
        {
            _viewModel.OpenIssueCommand.Execute(issue);
            e.Handled = true;
        }
    }

    private void PrRow_Open(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Models.GitHubPullRequest pr })
            _viewModel.OpenPullRequestCommand.Execute(pr);
    }

    private void PrRow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && sender is ListBoxItem { DataContext: Models.GitHubPullRequest pr })
        {
            _viewModel.OpenPullRequestCommand.Execute(pr);
            e.Handled = true;
        }
    }

    // Workflow run / release / notification rows: double-click or Enter opens the row
    // on GitHub, matching the issue and PR lists.
    private void RunRow_Open(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Models.WorkflowRun run })
            _viewModel.OpenWorkflowRunCommand.Execute(run);
    }

    private void RunRow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && sender is ListBoxItem { DataContext: Models.WorkflowRun run })
        {
            _viewModel.OpenWorkflowRunCommand.Execute(run);
            e.Handled = true;
        }
    }

    private void ReleaseRow_Open(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Models.Release release })
            _viewModel.OpenReleaseCommand.Execute(release);
    }

    private void ReleaseRow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && sender is ListBoxItem { DataContext: Models.Release release })
        {
            _viewModel.OpenReleaseCommand.Execute(release);
            e.Handled = true;
        }
    }

    private void NotificationRow_Open(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: Models.GitHubNotification notification })
            _viewModel.OpenNotificationCommand.Execute(notification);
    }

    private void NotificationRow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space && sender is ListBoxItem { DataContext: Models.GitHubNotification notification })
        {
            _viewModel.OpenNotificationCommand.Execute(notification);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Renders basic markdown into a RichTextBox FlowDocument.
    /// Supports: # headers, **bold**, `code`, - bullets, ![images](path), blank line = paragraph break.
    /// </summary>
    private static void RenderMarkdown(System.Windows.Controls.RichTextBox rtb, string markdown, string basePath)
    {
        var doc = NewFlowDocument();
        AppendMarkdown(doc, markdown, basePath, allowImages: true);
        rtb.Document = doc;
    }

    private static FlowDocument NewFlowDocument() => new()
    {
        PagePadding = new Thickness(0),
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 13,
        LineHeight = 20
    };

    /// <summary>
    /// Appends one markdown fragment as its own run of blocks. Conversation rendering
    /// calls this once per entry so a body can never be concatenated into a
    /// neighbouring entry's markup, and with <paramref name="allowImages"/>
    /// false so a third-party body cannot make the app fetch anything.
    /// </summary>
    private static void AppendMarkdown(FlowDocument doc, string markdown, string basePath, bool allowImages)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            doc.Blocks.Add(new Paragraph(new Run("(empty)") { Foreground = Brushes.Gray }));
            return;
        }

        var lines = markdown.Split('\n');
        Paragraph? currentParagraph = null;
        bool inCodeBlock = false;
        var codeBlockLines = new List<string>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Fenced code block (``` or ```)
            if (line.TrimStart().StartsWith("```"))
            {
                if (inCodeBlock)
                {
                    // End code block — render accumulated lines
                    var codeText = string.Join("\n", codeBlockLines);
                    var p = new Paragraph(new Run(codeText)
                    {
                        FontFamily = new FontFamily("Cascadia Code,Consolas"),
                        FontSize = 12,
                        Foreground = ThemeTextBrush()
                    })
                    {
                        Background = CodeBlockBackground(),
                        Padding = new Thickness(12, 8, 12, 8),
                        Margin = new Thickness(0, 6, 0, 6)
                    };
                    doc.Blocks.Add(p);
                    codeBlockLines.Clear();
                    inCodeBlock = false;
                }
                else
                {
                    inCodeBlock = true;
                }
                currentParagraph = null;
                continue;
            }

            if (inCodeBlock)
            {
                codeBlockLines.Add(line);
                continue;
            }

            // Blank line = end current paragraph
            if (string.IsNullOrWhiteSpace(line))
            {
                currentParagraph = null;
                continue;
            }

            // Image: ![alt](path) — local file or URL
            var imgMatch = Regex.Match(line.Trim(), @"!\[([^\]]*)\]\(([^)]+)\)");
            if (imgMatch.Success)
            {
                var imgSrc = imgMatch.Groups[2].Value;
                var alt = imgMatch.Groups[1].Value;
                if (!allowImages)
                {
                    // A fetch here would hand the body's author the reader's IP and a
                    // read receipt for opening the thread, and any decode is attacker-
                    // sized; third-party bodies get the alt text and no load at all.
                    doc.Blocks.Add(ImageUnavailable(alt));
                    currentParagraph = null;
                    continue;
                }
                var rendered = false;
                try
                {
                    if (imgSrc.StartsWith("http://") || imgSrc.StartsWith("https://"))
                    {
                        // The block goes in now and the bytes arrive later: a badge row
                        // would otherwise stall the render thread one round trip per image.
                        var block = ImageBlock(null);
                        doc.Blocks.Add(block);
                        _ = FillRemoteImageAsync(doc, block, imgSrc, alt);
                        rendered = true;
                    }
                    else
                    {
                        var imgPath = Path.IsPathRooted(imgSrc) ? imgSrc : Path.Combine(basePath, imgSrc);
                        if (File.Exists(imgPath))
                        {
                            // Streamed, not read into a byte array first: the pixel budget
                            // is the bound on the decode, and nothing should have to hold a
                            // whole file in memory to find out the source is too large.
                            using var data = File.OpenRead(imgPath);
                            var bitmap = DecodeBounded(data);
                            doc.Blocks.Add(bitmap is null ? ImageUnavailable(alt) : ImageBlock(bitmap));
                            rendered = true;
                        }
                    }

                    if (rendered) currentParagraph = null;
                }
                catch { }
                if (rendered) continue;
            }

            // Headers
            if (line.StartsWith("#### "))
            {
                var p = new Paragraph { Margin = new Thickness(0, 10, 0, 4) };
                AddFormattedInlines(p.Inlines, line[5..]);
                foreach (var inline in p.Inlines) { inline.FontWeight = FontWeights.SemiBold; inline.FontSize = 14; }
                doc.Blocks.Add(p);
                currentParagraph = null;
                continue;
            }
            if (line.StartsWith("### "))
            {
                var p = new Paragraph { Margin = new Thickness(0, 12, 0, 4) };
                AddFormattedInlines(p.Inlines, line[4..]);
                foreach (var inline in p.Inlines) { inline.FontWeight = FontWeights.SemiBold; inline.FontSize = 15; }
                doc.Blocks.Add(p);
                currentParagraph = null;
                continue;
            }
            if (line.StartsWith("## "))
            {
                var p = new Paragraph { Margin = new Thickness(0, 16, 0, 4) };
                AddFormattedInlines(p.Inlines, line[3..]);
                foreach (var inline in p.Inlines) { inline.FontWeight = FontWeights.Bold; inline.FontSize = 17; }
                doc.Blocks.Add(p);
                currentParagraph = null;
                continue;
            }
            if (line.StartsWith("# "))
            {
                var p = new Paragraph { Margin = new Thickness(0, 8, 0, 8) };
                AddFormattedInlines(p.Inlines, line[2..]);
                foreach (var inline in p.Inlines) { inline.FontWeight = FontWeights.Bold; inline.FontSize = 20; }
                doc.Blocks.Add(p);
                currentParagraph = null;
                continue;
            }

            // Horizontal rule
            if (line.Trim() is "---" or "***" or "___")
            {
                doc.Blocks.Add(new Paragraph(new Run(""))
                {
                    BorderBrush = Brushes.Gray,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 8, 0, 8)
                });
                currentParagraph = null;
                continue;
            }

            // Numbered list (1. , 2. , etc.)
            var numberedMatch = Regex.Match(line, @"^(\s*)(\d+)\.\s+(.+)$");
            if (numberedMatch.Success)
            {
                var indent = numberedMatch.Groups[1].Value.Length;
                var number = numberedMatch.Groups[2].Value;
                var content = numberedMatch.Groups[3].Value;
                var p = new Paragraph { Margin = new Thickness(12 + indent * 8, 2, 0, 2), TextIndent = -16 };
                p.Inlines.Add(new Run($"{number}. ") { Foreground = Brushes.Gray });
                AddFormattedInlines(p.Inlines, content);
                doc.Blocks.Add(p);
                currentParagraph = null;
                continue;
            }

            // Bullet points
            if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
            {
                var indent = line.Length - line.TrimStart().Length;
                var bulletText = line.TrimStart()[2..];
                var p = new Paragraph { Margin = new Thickness(12 + indent * 8, 2, 0, 2), TextIndent = -12 };
                p.Inlines.Add(new Run("\u2022 ") { Foreground = Brushes.Gray });
                AddFormattedInlines(p.Inlines, bulletText);
                doc.Blocks.Add(p);
                currentParagraph = null;
                continue;
            }

            // Table header separator (skip)
            if (Regex.IsMatch(line.Trim(), @"^\|[\s\-:|]+\|$"))
                continue;

            // Table rows
            if (line.TrimStart().StartsWith('|') && line.TrimEnd().EndsWith('|'))
            {
                var cells = line.Trim('|').Split('|');
                var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1), FontFamily = new FontFamily("Cascadia Code,Consolas") };
                p.Inlines.Add(new Run(string.Join("  \u2502  ", cells.Select(c => c.Trim()))) { FontSize = 12 });
                doc.Blocks.Add(p);
                currentParagraph = null;
                continue;
            }

            // Regular text — accumulate into paragraph
            if (currentParagraph == null)
            {
                currentParagraph = new Paragraph { Margin = new Thickness(0, 4, 0, 4) };
                doc.Blocks.Add(currentParagraph);
            }
            else
            {
                currentParagraph.Inlines.Add(new Run(" "));
            }
            AddFormattedInlines(currentParagraph.Inlines, line);
        }

        // Close unclosed code block
        if (inCodeBlock && codeBlockLines.Count > 0)
        {
            var codeText = string.Join("\n", codeBlockLines);
            var p = new Paragraph(new Run(codeText)
            {
                FontFamily = new FontFamily("Cascadia Code,Consolas"),
                FontSize = 12,
                Foreground = ThemeTextBrush()
            })
            {
                Background = CodeBlockBackground(),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 6, 0, 6)
            };
            doc.Blocks.Add(p);
        }
    }

    // ── Markdown images ─────────────────────────────────────────────────────────

    /// <summary>Longest decoded edge, in pixels. The shorter edge follows the source ratio.</summary>
    private const int MaxImageEdge = 800;

    /// <summary>
    /// Source pixel count refused outright. The bound is on decoder WORK, not on peak
    /// memory: the scaling decoder does not materialize the source at full size, but its
    /// running time tracks the source, not the capped output. A local file decodes inline
    /// on the render thread, so a declared 40000×40000 stalls the UI for the whole decode.
    /// </summary>
    private const long MaxImagePixels = 50_000_000;

    /// <summary>Bytes read from a remote image before the fetch is abandoned.</summary>
    private const long MaxImageBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Decoded remote image bytes held at once. A count cap does not bound memory: an
    /// entry is between a few KB (a badge) and <see cref="MaxImageEdge"/> squared times
    /// its pixel stride, so the same count spans three orders of magnitude of retention.
    /// </summary>
    internal const long MaxCachedRemoteImageBytes = 32L * 1024 * 1024;

    /// <summary>Wall-clock budget for one remote image fetch, headers and body together.</summary>
    private static readonly TimeSpan ImageFetchTimeout = TimeSpan.FromSeconds(15);

    private static readonly HttpClient ImageClient = new() { Timeout = ImageFetchTimeout };

    private sealed record RemoteImage(string Url, BitmapImage Bitmap, long Bytes);

    /// <summary>
    /// Decoded remote images by URL, with <see cref="RemoteImageOrder"/> holding the same
    /// entries most-recently-used first. A theme flip re-renders every open document, so
    /// without this each flip re-fetches every badge in the README. Frozen bitmaps are
    /// shared across documents, so an evicted entry costs one refetch and never a torn
    /// image. Every read and write of the three fields takes <see cref="RemoteImageLock"/>:
    /// a fetch completes on a pool thread while a render reads on the UI thread.
    /// </summary>
    private static readonly Dictionary<string, LinkedListNode<RemoteImage>> RemoteImages =
        new(StringComparer.Ordinal);

    private static readonly LinkedList<RemoteImage> RemoteImageOrder = new();
    private static long _remoteImageBytes;
    private static readonly object RemoteImageLock = new();

    /// <summary>Retained decoded bytes currently in the cache.</summary>
    internal static long RemoteImageCacheBytes
    {
        get { lock (RemoteImageLock) return _remoteImageBytes; }
    }

    /// <summary>Entries currently in the cache.</summary>
    internal static int RemoteImageCacheCount
    {
        get { lock (RemoteImageLock) return RemoteImages.Count; }
    }

    /// <summary>Decoded size of a frozen bitmap: its stride times its height.</summary>
    private static long DecodedBytes(BitmapImage bitmap) =>
        (long)bitmap.PixelHeight * ((bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8);

    /// <summary>Cached bitmap for <paramref name="url"/>, promoted to most recently used.</summary>
    internal static BitmapImage? TakeCachedRemoteImage(string url)
    {
        lock (RemoteImageLock)
        {
            if (!RemoteImages.TryGetValue(url, out var node)) return null;
            RemoteImageOrder.Remove(node);
            RemoteImageOrder.AddFirst(node);
            return node.Value.Bitmap;
        }
    }

    /// <summary>
    /// Caches a decoded image and evicts least-recently-used entries until the byte cap
    /// holds. An entry larger than the cap on its own is evicted immediately after it is
    /// added, which leaves the cache empty rather than looping.
    /// </summary>
    internal static void CacheRemoteImage(string url, BitmapImage bitmap)
    {
        var entry = new RemoteImage(url, bitmap, DecodedBytes(bitmap));
        lock (RemoteImageLock)
        {
            if (RemoteImages.TryGetValue(url, out var existing))
            {
                RemoteImageOrder.Remove(existing);
                _remoteImageBytes -= existing.Value.Bytes;
            }
            var node = RemoteImageOrder.AddFirst(entry);
            RemoteImages[url] = node;
            _remoteImageBytes += entry.Bytes;

            while (_remoteImageBytes > MaxCachedRemoteImageBytes && RemoteImageOrder.Last is { } oldest)
            {
                RemoteImageOrder.RemoveLast();
                RemoteImages.Remove(oldest.Value.Url);
                _remoteImageBytes -= oldest.Value.Bytes;
            }
        }
    }

    /// <summary>Drops every cached image and the bytes they held.</summary>
    internal static void ClearRemoteImageCache()
    {
        lock (RemoteImageLock)
        {
            RemoteImages.Clear();
            RemoteImageOrder.Clear();
            _remoteImageBytes = 0;
        }
    }

    /// <summary>The project whose documents filled the cache; "" before the first render.</summary>
    private static string _remoteImageProject = "";

    /// <summary>
    /// Drops the cached images when the page moves to a different project. The images
    /// belong to one project's README and CHANGELOG and are never rendered again once it
    /// is left, so holding them to the byte cap would retain a project no longer open.
    /// </summary>
    internal static void ReleaseRemoteImagesForProject(string projectPath)
    {
        if (string.Equals(_remoteImageProject, projectPath, StringComparison.OrdinalIgnoreCase)) return;
        _remoteImageProject = projectPath;
        ClearRemoteImageCache();
    }

    /// <summary>
    /// Fetches that have started and not yet reached the cache, keyed by URL. Renders run
    /// on the UI thread, so an entry is registered before the next occurrence of the same
    /// URL is reached; each entry is removed as its fetch completes.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Task<BitmapImage?>> RemoteImageFetches = new();

    /// <summary>The alt-text line shown wherever an image is refused or fails to decode.</summary>
    private static Paragraph ImageUnavailable(string alt) =>
        new(new Run(alt.Length > 0 ? $"[image not loaded: {alt}]" : "[image not loaded]")
        { Foreground = Brushes.Gray, FontStyle = FontStyles.Italic });

    /// <summary>
    /// Host block for a rendered image. DownOnly keeps a source smaller than the page at
    /// its natural size — Uniform on its own stretches a 200×20 badge across the width.
    /// </summary>
    private static BlockUIContainer ImageBlock(ImageSource? source) =>
        new(new System.Windows.Controls.Image
        {
            Source = source,
            MaxWidth = MaxImageEdge,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            Margin = new Thickness(0, 8, 0, 8)
        });

    /// <summary>
    /// Decodes image bytes with the LONGER edge capped and the source ratio preserved.
    /// Capping both axes squares the image and decodes ~160× the pixels of an 800-wide
    /// badge row; capping width alone lets a 1×2000 source decode to 800×1600000, which
    /// throws or attempts a multi-gigabyte allocation. Returns null on anything the
    /// caller must render as alt text instead.
    /// </summary>
    internal static BitmapImage? DecodeBounded(Stream data)
    {
        try
        {
            data.Position = 0;
            var probe = BitmapFrame.Create(data, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            int w = probe.PixelWidth, h = probe.PixelHeight;
            if (w <= 0 || h <= 0 || (long)w * h > MaxImagePixels) return null;

            data.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = data;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // Only the longer edge is set: WPF derives the other from the source ratio.
            // Setting both makes it ignore the ratio, and a small source is not upscaled.
            if (w >= h)
            {
                if (w > MaxImageEdge) bitmap.DecodePixelWidth = MaxImageEdge;
            }
            else if (h > MaxImageEdge)
            {
                bitmap.DecodePixelHeight = MaxImageEdge;
            }
            bitmap.EndInit();
            if (bitmap.CanFreeze) bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fills a README/CHANGELOG image block from the network. A failed fetch, one
    /// abandoned at the time budget, an oversized body, or an out-of-bounds decode swaps
    /// the block for the alt-text line instead of leaving a gap. Runs only where images
    /// are allowed — issue and pull request bodies never reach it.
    /// </summary>
    internal static async Task FillRemoteImageAsync(
        FlowDocument doc, BlockUIContainer block, string url, string alt, TimeSpan? timeout = null)
    {
        if (TakeCachedRemoteImage(url) is { } cached)
        {
            // Before the first await, so a cached badge is present on the first layout.
            ApplyRemoteImage(doc, block, cached, alt);
            return;
        }

        // The cache is written only once a fetch completes, so a README that repeats one
        // badge URL would issue a GET per occurrence on first render. Occurrences after
        // the first join the pending fetch.
        var bitmap = await RemoteImageFetches
            .GetOrAdd(url, u => FetchAndCacheAsync(u, timeout)).ConfigureAwait(false);

        try
        {
            await doc.Dispatcher.InvokeAsync(() => ApplyRemoteImage(doc, block, bitmap, alt));
        }
        catch { }
    }

    /// <summary>
    /// One fetch-and-decode per URL, caching the result and clearing its own in-flight
    /// entry before it completes. Returns null for every failure — the caller renders the
    /// alt-text line and never sees an exception.
    /// </summary>
    private static async Task<BitmapImage?> FetchAndCacheAsync(string url, TimeSpan? timeout)
    {
        BitmapImage? bitmap = null;
        try
        {
            using var data = await FetchBoundedAsync(url, timeout).ConfigureAwait(false);
            if (data is not null) bitmap = DecodeBounded(data);
        }
        catch { }

        if (bitmap is not null) CacheRemoteImage(url, bitmap);
        RemoteImageFetches.TryRemove(url, out _);
        return bitmap;
    }

    private static void ApplyRemoteImage(FlowDocument doc, BlockUIContainer block, BitmapImage? bitmap, string alt)
    {
        if (bitmap is not null && block.Child is System.Windows.Controls.Image image)
        {
            image.Source = bitmap;
            return;
        }
        // The document may have been re-rendered while the fetch was in flight.
        if (!doc.Blocks.Contains(block)) return;
        doc.Blocks.InsertAfter(block, ImageUnavailable(alt));
        doc.Blocks.Remove(block);
    }

    /// <summary>
    /// Reads a remote image into memory under a hard byte cap and a hard time budget. A
    /// declared Content-Length is checked first and the running total is checked again
    /// per chunk, because a server can declare one length and send another. The budget
    /// is a token threaded through every await, not HttpClient.Timeout: under
    /// ResponseHeadersRead that timeout ends at the response headers, so a server that
    /// answers 200 and then stalls mid-body — or dribbles bytes under the cap forever —
    /// pins a socket and a buffer for the life of the process.
    /// </summary>
    internal static async Task<MemoryStream?> FetchBoundedAsync(string url, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? ImageFetchTimeout);
        using var response = await ImageClient
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        if (response.Content.Headers.ContentLength > MaxImageBytes) return null;

        using var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxImageBytes)
            {
                buffer.Dispose();
                return null;
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer;
    }

    /// <summary>Theme-correct text brush at render time (hardcoded light gray was invisible in Light theme).</summary>
    private static Brush ThemeTextBrush() =>
        System.Windows.Application.Current?.Resources["TextFillColorPrimaryBrush"] as Brush
        ?? new SolidColorBrush(Color.FromRgb(200, 200, 200));

    /// <summary>Soft code background tinted for the ACTIVE theme (translucent white vanishes on light).</summary>
    private static SolidColorBrush CodeBlockBackground() =>
        Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Light
            ? new SolidColorBrush(Color.FromArgb(24, 0, 0, 0))
            : new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));

    /// <summary>
    /// Link targets allowed to become clickable, handing back the parsed Uri so the
    /// tooltip and the launch both describe what was actually parsed. Rendered bodies
    /// include third-party issue and comment text, and the click path is ShellExecute —
    /// file://, UNC, data:, javascript: and any registered protocol handler would launch
    /// a local program or leak credentials from a single click, so only http/https
    /// navigate.
    /// </summary>
    internal static bool TryGetNavigableUri(string url, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        uri = parsed;
        return true;
    }

    internal static bool IsNavigableLink(string url) => TryGetNavigableUri(url, out _);

    /// <summary>
    /// The disclosure shown beside a clickable link, with the host in PUNYCODE. The
    /// disclosure exists because the visible label is attacker-chosen, and a unicode
    /// host defeats it: a Cyrillic а renders identically to the Latin a, so a body
    /// labelled with a github.com URL can point at a lookalike domain and have the
    /// tooltip agree with the label. The punycode form differs visibly.
    /// </summary>
    internal static string LinkDisclosure(Uri uri)
    {
        var userInfo = uri.UserInfo.Length > 0 ? uri.UserInfo + "@" : "";
        var port = uri.IsDefaultPort ? "" : ":" + uri.Port;
        // IdnHost strips the brackets an IPv6 literal needs: without them the colons of
        // the address run into the port and the disclosure is not a URL at all.
        var host = uri.HostNameType == UriHostNameType.IPv6 ? $"[{uri.IdnHost}]" : uri.IdnHost;
        return $"{uri.Scheme}://{userInfo}{host}{port}{uri.PathAndQuery}{uri.Fragment}";
    }

    /// <summary>
    /// The disclosure a keyboard activation shows before it launches, or null when the
    /// link's own label already states where it goes. The mouse discloses on hover and a
    /// caret has no hover, so the label is what a keyboard reader has: a label that names
    /// a destination the launch does not keep must reach them first, and a label that
    /// names no destination has nothing to contradict. Comparison is against the punycode
    /// disclosure, so a label spelled in lookalike characters is never its own match, and
    /// is case-insensitive, so a host spelled in another case is not a mismatch.
    /// </summary>
    internal static string? KeyboardDisclosure(string linkText, Uri target)
    {
        var label = linkText.Trim();
        if (!NamesADestination(label)) return null;
        var disclosure = LinkDisclosure(target);
        var scheme = $"{target.Scheme}://";
        var bare = disclosure.StartsWith(scheme, StringComparison.Ordinal)
            ? disclosure[scheme.Length..]
            : disclosure;
        // A bare host is disclosed with the "/" that spells its empty path; a label
        // naming that host is the same destination without it.
        if (Same(label, disclosure) || Same(label, bare) || Same($"{label}/", disclosure) || Same($"{label}/", bare))
            return null;
        return disclosure;

        static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a link label reads as a destination rather than as prose. A run of
    /// host-shaped text with no whitespace, with or without a scheme, is a claim about
    /// where the link goes; a sentence, a file name, and a dotted version number are not.
    /// A label that names a file names no destination, so it opens the way prose does —
    /// only a host-shaped label owes a disclosure. The shape test runs on the normalized
    /// host token rather than the raw label: one adjacent character otherwise reclassifies
    /// a host claim as prose and opens an arbitrary target with nothing disclosed.
    /// </summary>
    private static bool NamesADestination(string label)
    {
        var visible = VisibleText(label);
        if (visible.Length == 0 || visible.Any(char.IsWhiteSpace)) return false;
        if (visible.Contains("://", StringComparison.Ordinal)) return true;

        var host = HostToken(visible);
        var segments = host.Split('.');
        // A dotted run of digits is a version unless it is a full dotted quad: the
        // framework reads three-segment shorthand such as 1.2.10 as an address too.
        if (segments.All(s => s.Length > 0 && s.All(char.IsAsciiDigit)))
            return segments.Length == 4 && Uri.CheckHostName(host) == UriHostNameType.IPv4;
        return HostShapedLabel.IsMatch(host) && !FileNameSuffixes.Contains(segments[^1]);
    }

    /// <summary>
    /// The label with its invisible characters removed. A Format-category character
    /// renders as nothing, so a zero-width space inside an otherwise host-shaped label
    /// leaves the reader seeing the host unchanged while the raw text no longer matches.
    /// </summary>
    private static string VisibleText(string label) =>
        string.Concat(label.Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format)).Trim();

    /// <summary>
    /// The authority a label claims, which is not the whole label: the resolved host is
    /// the text after the last "@" and before the first ":" or "/", and punctuation
    /// wrapping or trailing it is decoration rather than part of the name.
    /// </summary>
    private static string HostToken(string label)
    {
        var authority = label.Split('/', 2)[0];
        var host = authority[(authority.LastIndexOf('@') + 1)..].Split(':', 2)[0];
        var start = 0;
        var end = host.Length;
        while (start < end && !char.IsLetterOrDigit(host[start])) start++;
        while (end > start && !char.IsLetterOrDigit(host[end - 1])) end--;
        return host[start..end];
    }

    /// <summary>
    /// Dotted labels ending in a name, not a number: a release label such as v1.2.0 is
    /// not a host claim. Matches non-ASCII letters, which is where the lookalikes live.
    /// </summary>
    private static readonly Regex HostShapedLabel = new(@"^(?:[\w-]+\.)+\w{2,}$", RegexOptions.Compiled);

    /// <summary>
    /// Final segments that make a dotted label a file name rather than a host. Some are
    /// also delegated top-level domains; in a rendered issue body the file reading is the
    /// one a reader takes, and a label naming a file claims no destination at all.
    /// </summary>
    private static readonly HashSet<string> FileNameSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "md", "markdown", "txt", "rst", "log", "csv", "tsv",
        "json", "xml", "yml", "yaml", "toml", "ini", "cfg", "conf", "config", "lock",
        "html", "htm", "css", "scss", "js", "jsx", "mjs", "cjs", "ts", "tsx",
        "cs", "csproj", "sln", "slnx", "vb", "fs", "py", "rb", "go", "rs", "java", "kt",
        "swift", "php", "pl", "lua", "sql", "cpp", "hpp",
        "sh", "bash", "zsh", "ps1", "psm1", "bat", "cmd",
        "png", "jpg", "jpeg", "gif", "svg", "webp", "ico", "pdf",
        "zip", "tar", "gz", "bz2", "7z", "rar", "exe", "msi", "dll", "dylib",
    };

    /// <summary>
    /// The exact string handed to ShellExecute for a navigable link. The raw capture is
    /// not it: a target such as https://host/&lt;CR&gt;foo, an embedded tab, or one padded
    /// with spaces passes the allow-list and would otherwise reach the shell with those
    /// characters intact. The parsed form percent-encodes them and drops the padding.
    /// </summary>
    internal static string NavigationTarget(Uri uri) => uri.AbsoluteUri;

    /// <summary>
    /// What a clickable link's Click hands its target to, read at click time. The default
    /// hands it to the shell; a test exercising the click path substitutes its own, so
    /// clicking a rendered link never opens a browser.
    /// </summary>
    internal static Action<string> LaunchNavigable { get; set; } = ShellExecute;

    private static void ShellExecute(string target)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>
    /// Adds inline formatting: **bold**, *italic*, `code`, [links](url), ~~strikethrough~~
    /// </summary>
    internal static void AddFormattedInlines(InlineCollection inlines, string text)
    {
        var pattern = @"(\*\*(.+?)\*\*)|(\*(.+?)\*)|(`(.+?)`)|(\[([^\]]+)\]\(([^)]+)\))|(~~(.+?)~~)";
        int lastIndex = 0;

        foreach (Match match in Regex.Matches(text, pattern))
        {
            if (match.Index > lastIndex)
                inlines.Add(new Run(text[lastIndex..match.Index]));

            if (match.Groups[2].Success) // **bold**
                inlines.Add(new Run(match.Groups[2].Value) { FontWeight = FontWeights.Bold });
            else if (match.Groups[4].Success) // *italic*
                inlines.Add(new Run(match.Groups[4].Value) { FontStyle = FontStyles.Italic });
            else if (match.Groups[6].Success) // `code`
                inlines.Add(new Run(match.Groups[6].Value)
                {
                    FontFamily = new FontFamily("Cascadia Code,Consolas"),
                    Background = CodeBlockBackground(),
                    FontSize = 12
                });
            else if (match.Groups[8].Success) // [text](url)
            {
                var linkText = match.Groups[8].Value;
                var linkUrl = match.Groups[9].Value;
                if (!TryGetNavigableUri(linkUrl, out var target))
                {
                    // Inert text, and the real target printed beside it: the visible label
                    // is attacker-chosen and can name a URL the target is not.
                    inlines.Add(new Run($"{linkText} ({linkUrl})"));
                }
                else
                {
                    var hyperlink = new Hyperlink(new Run(linkText))
                    {
                        Foreground = new SolidColorBrush(Color.FromRgb(108, 164, 217)),
                        TextDecorations = TextDecorations.Underline,
                        ToolTip = LinkDisclosure(target),
                        // Read back by the keyboard path, which has no hover to disclose with.
                        Tag = KeyboardDisclosure(linkText, target)
                    };
                    var launch = NavigationTarget(target);
                    // Click is raised by the mouse and by Hyperlink.DoClick, so the keyboard
                    // path launches through this handler rather than a second copy of it.
                    hyperlink.Click += (_, _) => LaunchNavigable(launch);
                    inlines.Add(hyperlink);
                }
            }
            else if (match.Groups[11].Success) // ~~strikethrough~~
                inlines.Add(new Run(match.Groups[11].Value) { TextDecorations = TextDecorations.Strikethrough });

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
            inlines.Add(new Run(text[lastIndex..]));
    }

    /// <summary>
    /// The hyperlink a caret at <paramref name="position"/> addresses, or null when it
    /// addresses none. A caret resting on the boundary either side of a link has the
    /// enclosing paragraph as its parent, not the link, so both adjacent elements are
    /// considered — otherwise the link is only reachable from strictly inside its text.
    /// </summary>
    internal static Hyperlink? HyperlinkAt(TextPointer? position)
    {
        if (position is null) return null;
        return Enclosing(position.Parent)
            ?? Enclosing(position.GetAdjacentElement(LogicalDirection.Forward) as DependencyObject)
            ?? Enclosing(position.GetAdjacentElement(LogicalDirection.Backward) as DependencyObject);
    }

    private static Hyperlink? Enclosing(DependencyObject? node)
    {
        for (; node is not null; node = node switch
             {
                 FrameworkContentElement content => content.Parent,
                 FrameworkElement element => element.Parent,
                 _ => null,
             })
        {
            if (node is Hyperlink link) return link;
        }
        return null;
    }

    /// <summary>
    /// Launches a link the caret addresses, showing its disclosure first when it carries
    /// one. A link the reader declines is not launched; every other link opens on the one
    /// keystroke the mouse path costs one click.
    /// </summary>
    internal static async Task<bool> ActivateFromKeyboardAsync(Hyperlink link, Func<string, Task<bool>> confirm)
    {
        if (link.Tag is string disclosure && !await confirm(disclosure)) return false;
        link.DoClick();
        return true;
    }

    /// <summary>
    /// Enter opens the link the caret addresses. A Hyperlink inside a RichTextBox never
    /// takes keyboard focus — the text editor owns the keyboard and the caret is its
    /// cursor — so without this a rendered markdown link is reachable by mouse only.
    /// </summary>
    private async void RenderedText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        if (sender is not System.Windows.Controls.RichTextBox rtb) return;
        if (HyperlinkAt(rtb.CaretPosition) is not { } link) return;
        // Set before the await: the key is consumed whether or not the launch is confirmed.
        e.Handled = true;
        await ActivateFromKeyboardAsync(link, ConfirmLinkAsync);
    }

    /// <summary>
    /// Names the destination a link's own text does not. The label is attacker-chosen in a
    /// rendered issue body, so the disclosure is the punycode form and the reader answers
    /// before anything is launched. The wording states the destination without asserting a
    /// mismatch: a label reaches this dialog on shape alone, so a decorated spelling of an
    /// honest host arrives here too and a mismatch claim would be false.
    /// </summary>
    private Task<bool> ConfirmLinkAsync(string disclosure) =>
        _viewModel.ConfirmAsync(
            "Open this link?",
            $"This link opens:\n\n{disclosure}",
            "Open link");

    /// <summary>
    /// Selects the commit under a right-click before its context menu opens. Every surgery
    /// command acts on the selection, so without this the menu could name one commit and
    /// operate on another.
    /// </summary>
    private void OnCommitListRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list || e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(list, source) is ListBoxItem item) item.IsSelected = true;
    }
}
