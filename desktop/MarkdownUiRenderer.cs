using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace LocalAIHub;

/// <summary>
/// Lightweight native WPF Markdown renderer for AI Assistant &amp; Director chat bubbles.
/// Supports headings (#/##/###), bullet &amp; numbered lists, fenced code blocks,
/// **bold**, __bold__, *italic*, ***bold italic***, and `inline code`.
/// </summary>
public static class MarkdownUiRenderer
{
    private static readonly Regex InlineTokenRegex = new(
        @"(`[^`\r\n]+`|\*\*\*[^*\r\n]+?\*\*\*|\*\*[^*\r\n]+?\*\*|__[^_\r\n]+?__|(?<!\*)\*[^*\r\n]+?\*(?!\*))",
        RegexOptions.Compiled);

    private static readonly Regex HeadingRegex = new(@"^(#{1,4})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletRegex = new(@"^(\s*)[-*•]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberedRegex = new(@"^(\s*)(\d+[.)])\s+(.*)$", RegexOptions.Compiled);

    private static SolidColorBrush Brush(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;

    public static UIElement Build(string markdown, double fontSize = 13, string foregroundHex = "#E4E4E8")
    {
        var root = new StackPanel();
        if (string.IsNullOrWhiteSpace(markdown))
            return root;

        var fg = Brush(foregroundHex);
        var headingFg = Brush("#F4F4F6");
        var bulletFg = Brush("#FF521B");
        var codeFg = Brush("#F2F2F5");
        var codeBg = Brush("#18181D");

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        var paraBuf = new List<string>();

        void FlushParagraph()
        {
            if (paraBuf.Count == 0) return;
            string joined = string.Join("\n", paraBuf);
            paraBuf.Clear();
            var tb = CreateBaseTextBlock(fontSize, fg);
            tb.Margin = new Thickness(0, 0, 0, 5);
            AppendInlineMarkdown(tb.Inlines, joined, codeFg, codeBg);
            root.Children.Add(tb);
        }

        while (i < lines.Length)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // Fenced code block ```
            if (trimmed.StartsWith("```"))
            {
                FlushParagraph();
                i++;
                var codeLines = new List<string>();
                while (i < lines.Length && !lines[i].Trim().StartsWith("```"))
                {
                    codeLines.Add(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++; // skip closing ```

                var codeBox = new System.Windows.Controls.TextBox
                {
                    Text = string.Join(Environment.NewLine, codeLines),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = Math.Max(11, fontSize - 1),
                    Foreground = codeFg,
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(0)
                };
                root.Children.Add(new Border
                {
                    Background = Brush("#09090B"),
                    BorderBrush = Brush("#26262D"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(9, 6, 9, 6),
                    Margin = new Thickness(0, 2, 0, 6),
                    Child = codeBox
                });
                continue;
            }

            // Blank line separates paragraphs
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                FlushParagraph();
                i++;
                continue;
            }

            // Horizontal rule --- / ***
            if (trimmed is "---" or "***" or "___")
            {
                FlushParagraph();
                root.Children.Add(new Border
                {
                    Height = 1,
                    Background = Brush("#26262D"),
                    Margin = new Thickness(0, 4, 0, 6)
                });
                i++;
                continue;
            }

            // Heading (# .. ####)
            var hm = HeadingRegex.Match(trimmed);
            if (hm.Success)
            {
                FlushParagraph();
                int level = hm.Groups[1].Value.Length;
                double hSize = level switch { 1 => fontSize + 3, 2 => fontSize + 2, _ => fontSize + 0.5 };
                var tb = CreateBaseTextBlock(hSize, headingFg);
                tb.FontWeight = FontWeights.SemiBold;
                tb.Margin = new Thickness(0, root.Children.Count > 0 ? 4 : 0, 0, 4);
                AppendInlineMarkdown(tb.Inlines, hm.Groups[2].Value.Trim(), codeFg, codeBg);
                root.Children.Add(tb);
                i++;
                continue;
            }

            // Bullet list item (- / * / •)
            var bm = BulletRegex.Match(line);
            if (bm.Success)
            {
                FlushParagraph();
                int indentSpaces = bm.Groups[1].Value.Length;
                double leftPad = 4 + Math.Min(indentSpaces, 6) * 5;
                var row = new Grid { Margin = new Thickness(leftPad, 0, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var dot = new TextBlock
                {
                    Text = indentSpaces >= 2 ? "◦" : "•",
                    Foreground = bulletFg,
                    FontSize = fontSize,
                    FontWeight = FontWeights.Bold,
                    LineHeight = fontSize + 6
                };
                Grid.SetColumn(dot, 0);

                var tb = CreateBaseTextBlock(fontSize, fg);
                AppendInlineMarkdown(tb.Inlines, bm.Groups[2].Value.Trim(), codeFg, codeBg);
                Grid.SetColumn(tb, 1);

                row.Children.Add(dot);
                row.Children.Add(tb);
                root.Children.Add(row);
                i++;
                continue;
            }

            // Numbered list item (1. / 2))
            var nm = NumberedRegex.Match(line);
            if (nm.Success)
            {
                FlushParagraph();
                int indentSpaces = nm.Groups[1].Value.Length;
                double leftPad = 4 + Math.Min(indentSpaces, 6) * 5;
                var row = new Grid { Margin = new Thickness(leftPad, 0, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var num = new TextBlock
                {
                    Text = nm.Groups[2].Value + " ",
                    Foreground = bulletFg,
                    FontSize = fontSize,
                    FontWeight = FontWeights.SemiBold,
                    LineHeight = fontSize + 6,
                    Margin = new Thickness(0, 0, 3, 0)
                };
                Grid.SetColumn(num, 0);

                var tb = CreateBaseTextBlock(fontSize, fg);
                AppendInlineMarkdown(tb.Inlines, nm.Groups[3].Value.Trim(), codeFg, codeBg);
                Grid.SetColumn(tb, 1);

                row.Children.Add(num);
                row.Children.Add(tb);
                root.Children.Add(row);
                i++;
                continue;
            }

            paraBuf.Add(trimmed);
            i++;
        }

        FlushParagraph();

        // Trim bottom margin of the very last element so bubbles look crisp
        if (root.Children.Count > 0 && root.Children[^1] is FrameworkElement last)
        {
            var m = last.Margin;
            last.Margin = new Thickness(m.Left, m.Top, m.Right, 0);
        }

        return root;
    }

    public static System.Windows.Controls.ContextMenu CreateCopyContextMenu(string rawText)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        var item = new System.Windows.Controls.MenuItem { Header = "Копировать текст" };
        item.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(rawText); } catch { }
        };
        menu.Items.Add(item);
        return menu;
    }

    private static TextBlock CreateBaseTextBlock(double fontSize, SolidColorBrush fg) => new()
    {
        Foreground = fg,
        FontSize = fontSize,
        LineHeight = fontSize + 6,
        TextWrapping = TextWrapping.Wrap
    };

    private static void AppendInlineMarkdown(InlineCollection inlines, string text, SolidColorBrush codeFg, SolidColorBrush codeBg)
    {
        int pos = 0;
        foreach (Match m in InlineTokenRegex.Matches(text))
        {
            if (m.Index > pos)
                inlines.Add(new Run(CleanDanglingMarkers(text[pos..m.Index])));

            string tok = m.Value;
            if (tok.StartsWith('`') && tok.EndsWith('`') && tok.Length >= 2)
            {
                var run = new Run(" " + tok[1..^1] + " ")
                {
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = codeFg,
                    Background = codeBg
                };
                inlines.Add(run);
            }
            else if (tok.StartsWith("***") && tok.EndsWith("***") && tok.Length >= 6)
            {
                inlines.Add(new Bold(new Italic(new Run(tok[3..^3]))));
            }
            else if ((tok.StartsWith("**") && tok.EndsWith("**") && tok.Length >= 4) ||
                     (tok.StartsWith("__") && tok.EndsWith("__") && tok.Length >= 4))
            {
                inlines.Add(new Bold(new Run(tok[2..^2])) { Foreground = Brush("#FFFFFF") });
            }
            else if (tok.StartsWith('*') && tok.EndsWith('*') && tok.Length >= 2)
            {
                inlines.Add(new Italic(new Run(tok[1..^1])));
            }
            else
            {
                inlines.Add(new Run(tok));
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            inlines.Add(new Run(CleanDanglingMarkers(text[pos..])));
    }

    private static string CleanDanglingMarkers(string s)
    {
        // Strip stray unclosed ** markers if a small LLM forgot to close bold formatting
        return s.Replace("**", "");
    }
}
