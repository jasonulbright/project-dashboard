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

    public ProjectDetailPage(ProjectDetailViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        Loaded += OnLoaded;
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

        Dispatcher.Invoke(() =>
        {
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
        });
    }

    private void ScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Renders basic markdown into a RichTextBox FlowDocument.
    /// Supports: # headers, **bold**, `code`, - bullets, ![images](path), blank line = paragraph break.
    /// </summary>
    private static void RenderMarkdown(System.Windows.Controls.RichTextBox rtb, string markdown, string basePath)
    {
        var doc = new FlowDocument
        {
            PagePadding = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            LineHeight = 20
        };

        if (string.IsNullOrWhiteSpace(markdown))
        {
            doc.Blocks.Add(new Paragraph(new Run("(empty)") { Foreground = Brushes.Gray }));
            rtb.Document = doc;
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
                        Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200))
                    })
                    {
                        Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
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
                FontSize = 12
            })
            {
                Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 6, 0, 6)
            };
            doc.Blocks.Add(p);
        }

        rtb.Document = doc;
    }

    /// <summary>
    /// Adds inline formatting: **bold**, *italic*, `code`, [links](url), ~~strikethrough~~
    /// </summary>
    private static void AddFormattedInlines(InlineCollection inlines, string text)
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
                    Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                    FontSize = 12
                });
            else if (match.Groups[8].Success) // [text](url)
            {
                var linkUrl = match.Groups[9].Value;
                var hyperlink = new Hyperlink(new Run(match.Groups[8].Value))
                {
                    Foreground = new SolidColorBrush(Color.FromRgb(108, 164, 217)),
                    TextDecorations = TextDecorations.Underline
                };
                hyperlink.Click += (_, _) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(linkUrl) { UseShellExecute = true }); }
                    catch { }
                };
                inlines.Add(hyperlink);
            }
            else if (match.Groups[11].Success) // ~~strikethrough~~
                inlines.Add(new Run(match.Groups[11].Value) { TextDecorations = TextDecorations.Strikethrough });

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
            inlines.Add(new Run(text[lastIndex..]));
    }
}
