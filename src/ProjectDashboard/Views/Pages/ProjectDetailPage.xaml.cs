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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var project = DashboardViewModel.SelectedProject;
        if (project is not null)
        {
            _viewModel.SetProject(project);
            RenderMarkdown(ReadmeRichText, _viewModel.ReadmeText, project.FullPath);
            RenderMarkdown(ChangelogRichText, _viewModel.ChangelogText, project.FullPath);
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

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Blank line = end current paragraph
            if (string.IsNullOrWhiteSpace(line))
            {
                currentParagraph = null;
                continue;
            }

            // Image: ![alt](path)
            var imgMatch = Regex.Match(line.Trim(), @"^!\[([^\]]*)\]\(([^)]+)\)$");
            if (imgMatch.Success)
            {
                var imgPath = imgMatch.Groups[2].Value;
                if (!Path.IsPathRooted(imgPath))
                    imgPath = Path.Combine(basePath, imgPath);

                if (File.Exists(imgPath))
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(imgPath, UriKind.Absolute);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.DecodePixelWidth = 800;
                        bitmap.EndInit();

                        var img = new System.Windows.Controls.Image
                        {
                            Source = bitmap,
                            MaxWidth = 800,
                            Stretch = Stretch.Uniform,
                            Margin = new Thickness(0, 8, 0, 8)
                        };

                        var container = new BlockUIContainer(img);
                        doc.Blocks.Add(container);
                        currentParagraph = null;
                        continue;
                    }
                    catch { }
                }
                // If image not found, render as text
            }

            // Headers
            if (line.StartsWith("### "))
            {
                var p = new Paragraph(new Run(line[4..]) { FontWeight = FontWeights.SemiBold, FontSize = 15 })
                { Margin = new Thickness(0, 12, 0, 4) };
                doc.Blocks.Add(p);
                currentParagraph = null;
                continue;
            }
            if (line.StartsWith("## "))
            {
                var p = new Paragraph(new Run(line[3..]) { FontWeight = FontWeights.Bold, FontSize = 17 })
                { Margin = new Thickness(0, 16, 0, 4) };
                doc.Blocks.Add(p);
                currentParagraph = null;
                continue;
            }
            if (line.StartsWith("# "))
            {
                var p = new Paragraph(new Run(line[2..]) { FontWeight = FontWeights.Bold, FontSize = 20 })
                { Margin = new Thickness(0, 8, 0, 8) };
                doc.Blocks.Add(p);
                currentParagraph = null;
                continue;
            }

            // Horizontal rule
            if (line.Trim() == "---" || line.Trim() == "***")
            {
                doc.Blocks.Add(new Paragraph(new Run("")) { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 8, 0, 8) });
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
                p.Inlines.Add(new Run(string.Join("  |  ", cells.Select(c => c.Trim()))) { FontSize = 12 });
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

        rtb.Document = doc;
    }

    /// <summary>
    /// Adds inline formatting: **bold**, `code`, [links](url)
    /// </summary>
    private static void AddFormattedInlines(InlineCollection inlines, string text)
    {
        // Process **bold**, `code`, and [text](url) inline
        var pattern = @"(\*\*(.+?)\*\*)|(`(.+?)`)|(\[([^\]]+)\]\(([^)]+)\))";
        int lastIndex = 0;

        foreach (Match match in Regex.Matches(text, pattern))
        {
            if (match.Index > lastIndex)
                inlines.Add(new Run(text[lastIndex..match.Index]));

            if (match.Groups[2].Success) // **bold**
                inlines.Add(new Run(match.Groups[2].Value) { FontWeight = FontWeights.Bold });
            else if (match.Groups[4].Success) // `code`
                inlines.Add(new Run(match.Groups[4].Value) { FontFamily = new FontFamily("Cascadia Code,Consolas"), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)) });
            else if (match.Groups[6].Success) // [text](url)
                inlines.Add(new Run(match.Groups[6].Value) { Foreground = new SolidColorBrush(Color.FromRgb(108, 164, 217)), TextDecorations = TextDecorations.Underline });

            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < text.Length)
            inlines.Add(new Run(text[lastIndex..]));
    }
}
