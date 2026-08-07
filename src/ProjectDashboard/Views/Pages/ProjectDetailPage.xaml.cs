using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>Remote images kept per session, and the count at which the set is dropped.</summary>
    private const int MaxCachedRemoteImages = 64;

    /// <summary>Wall-clock budget for one remote image fetch, headers and body together.</summary>
    private static readonly TimeSpan ImageFetchTimeout = TimeSpan.FromSeconds(15);

    private static readonly HttpClient ImageClient = new() { Timeout = ImageFetchTimeout };

    /// <summary>
    /// Decoded remote images by URL. A theme flip re-renders every open document, so
    /// without this each flip re-fetches every badge in the README. Frozen bitmaps are
    /// shared across documents; the whole set is dropped at the cap rather than evicted
    /// entry by entry, which costs one refetch.
    /// </summary>
    private static readonly ConcurrentDictionary<string, BitmapImage> RemoteImages = new();

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
    /// the block for the alt-text line instead of leaving a gap. Runs only where images are allowed — issue and pull
    /// request bodies never reach it.
    /// </summary>
    internal static async Task FillRemoteImageAsync(
        FlowDocument doc, BlockUIContainer block, string url, string alt, TimeSpan? timeout = null)
    {
        if (RemoteImages.TryGetValue(url, out var cached))
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

        if (bitmap is not null)
        {
            if (RemoteImages.Count >= MaxCachedRemoteImages) RemoteImages.Clear();
            RemoteImages[url] = bitmap;
        }
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
    /// The exact string handed to ShellExecute for a navigable link. The raw capture is
    /// not it: a target such as https://host/&lt;CR&gt;foo, an embedded tab, or one padded
    /// with spaces passes the allow-list and would otherwise reach the shell with those
    /// characters intact. The parsed form percent-encodes them and drops the padding.
    /// </summary>
    internal static string NavigationTarget(Uri uri) => uri.AbsoluteUri;

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
                        ToolTip = LinkDisclosure(target)
                    };
                    var launch = NavigationTarget(target);
                    hyperlink.Click += (_, _) =>
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launch) { UseShellExecute = true }); }
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
