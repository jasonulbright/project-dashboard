using System.IO;
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
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, System.Windows.Media.Color accent)
    {
        RenderDocuments();
        RenderIssueConversation();
        RenderPullRequestConversation();
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
        var basePath = _viewModel.Project?.FullPath ?? "";
        try
        {
            IssueConversationRich.Document =
                BuildConversationDocument(d.Author, d.CreatedAt, d.Body, d.Comments, basePath);
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
        var basePath = _viewModel.Project?.FullPath ?? "";
        try
        {
            PullRequestConversationRich.Document =
                BuildConversationDocument(d.Author, d.CreatedAt, d.Body, d.Comments, basePath);
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
        IReadOnlyList<Models.IssueComment> comments, string basePath)
    {
        var doc = NewFlowDocument();
        AppendConversationEntry(doc, ConversationHeader(author, created), body, basePath);
        foreach (var c in comments)
            AppendConversationEntry(doc, ConversationHeader(c.Author, c.CreatedAt), c.Body, basePath);
        return doc;
    }

    /// <summary>
    /// Appends one entry: an author header block followed by that entry's rendered body.
    /// </summary>
    internal static void AppendConversationEntry(FlowDocument doc, string headerText, string body, string basePath)
    {
        doc.Blocks.Add(ConversationHeaderBlock(headerText));
        AppendMarkdown(doc, string.IsNullOrWhiteSpace(body) ? "(no content)" : body, basePath);
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
        var project = DashboardViewModel.SelectedProject;
        if (project is null) return;

        try
        {
            await _viewModel.SetProjectAsync(project);
        }
        catch { }

        RenderDocuments();

        // Project switched while a lazy tab was active: its data was reset, reload it.
        LoadDataForActiveTab();

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

    /// <summary>Lazy-loads tab data the first time a surface is opened for the current project.</summary>
    private void WorkTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, WorkTabs)) return;
        LoadDataForActiveTab();
    }

    private void LoadDataForActiveTab()
    {
        if (WorkTabs.SelectedItem is not TabItem { Tag: Models.DetailTab tab }) return;
        var load = ProjectDetailTabs.LoadForTab(
            tab, _viewModel.Branches.Count > 0, _viewModel.StashesLoaded, _viewModel.PullRequestsLoaded);
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

    /// <summary>
    /// Renders basic markdown into a RichTextBox FlowDocument.
    /// Supports: # headers, **bold**, `code`, - bullets, ![images](path), blank line = paragraph break.
    /// </summary>
    private static void RenderMarkdown(System.Windows.Controls.RichTextBox rtb, string markdown, string basePath)
    {
        var doc = NewFlowDocument();
        AppendMarkdown(doc, markdown, basePath);
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
    /// neighbouring entry's markup.
    /// </summary>
    private static void AppendMarkdown(FlowDocument doc, string markdown, string basePath)
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
                var rendered = false;
                try
                {
                    BitmapImage? bitmap = null;
                    if (imgSrc.StartsWith("http://") || imgSrc.StartsWith("https://"))
                    {
                        bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(imgSrc, UriKind.Absolute);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 800;
                        bitmap.EndInit();
                    }
                    else
                    {
                        var imgPath = Path.IsPathRooted(imgSrc) ? imgSrc : Path.Combine(basePath, imgSrc);
                        if (File.Exists(imgPath))
                        {
                            bitmap = new BitmapImage();
                            bitmap.BeginInit();
                            bitmap.UriSource = new Uri(imgPath, UriKind.Absolute);
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.DecodePixelWidth = 800;
                            bitmap.EndInit();
                            if (bitmap.CanFreeze) bitmap.Freeze();
                        }
                    }

                    if (bitmap is not null)
                    {
                        if (bitmap.CanFreeze) bitmap.Freeze();
                        var img = new System.Windows.Controls.Image
                        {
                            Source = bitmap,
                            MaxWidth = 800,
                            Stretch = Stretch.Uniform,
                            Margin = new Thickness(0, 8, 0, 8)
                        };
                        doc.Blocks.Add(new BlockUIContainer(img));
                        currentParagraph = null;
                        rendered = true;
                    }
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
    /// Link targets allowed to become clickable. Rendered bodies include third-party
    /// issue and comment text, and the click path is ShellExecute — file://, UNC,
    /// data:, javascript: and any registered protocol handler would launch a local
    /// program or leak credentials from a single click, so only http/https navigate.
    /// </summary>
    internal static bool IsNavigableLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

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
                if (!IsNavigableLink(linkUrl))
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
                        ToolTip = linkUrl
                    };
                    hyperlink.Click += (_, _) =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(linkUrl) { UseShellExecute = true }); }
                        catch { }
                    };
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
}
