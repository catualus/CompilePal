using System.IO;
using System.Text;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Guards the OUTPUT tab font against a plausible-looking "simplification".
    ///
    /// ApplyOutputFontSettings assigns FontFamily/FontSize to the FlowDocument as well as to the
    /// hosting RichTextBox, which reads like redundant belt-and-braces. It is not. A FlowDocument
    /// declared inline as RichTextBox.Document does not pick up text properties from that
    /// RichTextBox; the log rendered in WPF's document default (Georgia 12) while the control was
    /// set to a monospace family, which also made the "Output Font Size" setting look broken.
    /// Confirmed on the running app via UI Automation's TextPattern.FontNameAttribute.
    /// </summary>
    public class OutputFontTests
    {
        // Mirrors the shape MainWindow.xaml declares, including the font on the RichTextBox.
        private const string Markup = """
            <RichTextBox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         FontFamily="Consolas" FontSize="20">
              <FlowDocument>
                <Paragraph><Run>Loaded JSON metadata PACK</Run></Paragraph>
              </FlowDocument>
            </RichTextBox>
            """;

        private static (RichTextBox Box, Run First) LoadFromXaml()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Markup));
            var box = (RichTextBox)XamlReader.Load(stream);
            var para = (Paragraph)box.Document.Blocks.FirstBlock;
            return (box, (Run)para.Inlines.FirstInline);
        }

        [WpfFact]
        public void AssigningTheFontToTheDocumentReachesTheText()
        {
            var (box, run) = LoadFromXaml();

            box.Document.FontFamily = new FontFamily("Courier New");
            box.Document.FontSize = 27;

            Assert.Equal("Courier New", run.FontFamily.Source);
            Assert.Equal(27, run.FontSize);
        }

        /// <summary>
        /// Records the behaviour the fix exists for: whatever the control says, the text follows the
        /// document. If WPF ever changes so the control alone is enough, this fails and the extra
        /// assignment can be reconsidered - deliberately, rather than by someone assuming it.
        /// </summary>
        [WpfFact]
        public void DocumentFontWinsOverTheHostingControl()
        {
            var (box, run) = LoadFromXaml();

            box.Document.FontFamily = new FontFamily("Courier New");
            box.Document.FontSize = 27;

            // Now change only the control. The text must not follow it.
            box.FontFamily = new FontFamily("Segoe UI");
            box.FontSize = 11;

            Assert.Equal("Courier New", run.FontFamily.Source);
            Assert.Equal(27, run.FontSize);
        }

        [WpfFact]
        public void ConfiguredFallbackListResolvesToAMonospaceFace()
        {
            // Compile tool output is column-aligned with spaces, so a proportional font here
            // silently mangles VBSP's lump report and similar tables.
            var family = new FontFamily("Cascadia Mono, Cascadia Code, Consolas, Courier New");

            double narrow = MeasureWidth("iiiiiiii", family);
            double wide = MeasureWidth("MMMMMMMM", family);

            Assert.Equal(narrow, wide, 1);
        }

        private static double MeasureWidth(string text, FontFamily family)
        {
            var ft = new FormattedText(
                text,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface(family, System.Windows.FontStyles.Normal,
                             System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal),
                16,
                Brushes.Black,
                1.0);
            return ft.WidthIncludingTrailingWhitespace;
        }
    }
}
