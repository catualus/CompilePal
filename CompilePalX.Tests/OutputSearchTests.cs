using System.Linq;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CompilePalX.Compiling;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Covers the two failure modes in find-in-output that look correct by inspection:
    /// matches spanning run boundaries, and the document fragmentation that highlighting
    /// itself causes.
    /// </summary>
    public class OutputSearchTests
    {
        /// <summary>
        /// Builds a document shaped like a real compile log: many small Runs, one per write,
        /// with words deliberately straddling the boundaries between them.
        /// </summary>
        private static (RichTextBox Box, Paragraph Para, OutputSearch Search) NewOutput(params string[] runs)
        {
            var para = new Paragraph();
            foreach (var r in runs)
                para.Inlines.Add(new Run(r));

            var doc = new FlowDocument(para);
            var box = new RichTextBox { Document = doc, IsReadOnly = true };
            return (box, para, new OutputSearch(doc));
        }

        private static readonly string[] LogLines =
        {
            "Loaded JSON metadata CUSTOM from ",
            "./Parameters\\CUSTOM\\meta.json at order 15\n",
            "Added preset Best for processes VBSP, VVIS, CUSTOM, SHUTDOWN\n",
        };

        [WpfFact]
        public void FindsEveryOccurrenceAcrossSeparateRuns()
        {
            var (_, _, search) = NewOutput(LogLines);

            var matches = search.FindAll("CUSTOM");

            Assert.Equal(3, matches.Count);
            Assert.All(matches, m => Assert.Equal("CUSTOM", m.Text));
        }

        [WpfFact]
        public void MatchIsCaseInsensitive()
        {
            var (_, _, search) = NewOutput(LogLines);

            Assert.Equal(3, search.FindAll("custom").Count);
        }

        /// <summary>
        /// The original implementation searched each Run in isolation, so a query straddling
        /// two runs was silently missed. "from ./Parameters" spans runs 0 and 1.
        /// </summary>
        [WpfFact]
        public void FindsMatchSpanningTwoRuns()
        {
            var (_, _, search) = NewOutput(LogLines);

            var matches = search.FindAll("from ./Parameters");

            Assert.Single(matches);
            Assert.Equal("from ./Parameters", matches[0].Text);
        }

        [WpfFact]
        public void EmptyQueryMatchesNothing()
        {
            var (_, _, search) = NewOutput(LogLines);

            Assert.Empty(search.FindAll(""));
        }

        [WpfFact]
        public void HighlightPaintsEveryMatchAndMarksTheCurrentOneDifferently()
        {
            var (_, _, search) = NewOutput(LogLines);
            var matches = search.FindAll("CUSTOM");

            search.Highlight(matches, currentIndex: 1);

            var first = matches[0].GetPropertyValue(TextElement.BackgroundProperty) as SolidColorBrush;
            var current = matches[1].GetPropertyValue(TextElement.BackgroundProperty) as SolidColorBrush;

            Assert.NotNull(first);
            Assert.NotNull(current);
            Assert.Equal(((SolidColorBrush)OutputSearch.MatchBrush).Color, first!.Color);
            Assert.Equal(((SolidColorBrush)OutputSearch.CurrentMatchBrush).Color, current!.Color);
            Assert.NotEqual(first.Color, current.Color);
        }

        [WpfFact]
        public void ClearingRemovesTheHighlight()
        {
            var (_, _, search) = NewOutput(LogLines);
            var matches = search.FindAll("CUSTOM");
            search.Highlight(matches, 0);

            search.ClearHighlights();

            var brush = matches[0].GetPropertyValue(TextElement.BackgroundProperty) as SolidColorBrush;
            Assert.True(brush == null || brush.Color.A == 0);
        }

        /// <summary>
        /// The regression that made background highlighting risky in the first place.
        ///
        /// ApplyPropertyValue splits Runs, so after highlighting "CUSTOM" the document has new
        /// boundaries the next query must survive. A per-run search fails here: extending the
        /// query to "CUSTOM from" would find nothing, because the text now straddles the split
        /// the previous highlight created.
        /// </summary>
        [WpfFact]
        public void SearchStillWorksAfterHighlightingHasSplitTheRuns()
        {
            var (_, para, search) = NewOutput(LogLines);
            int runsBefore = para.Inlines.Count;

            search.Highlight(search.FindAll("CUSTOM"), 0);

            Assert.True(para.Inlines.Count > runsBefore,
                "expected highlighting to fragment the document - if it no longer does, this test " +
                "is no longer exercising the regression it was written for");

            var matches = search.FindAll("CUSTOM from");

            Assert.Single(matches);
            Assert.Equal("CUSTOM from", matches[0].Text);
        }

        [WpfFact]
        public void TextIsRebuiltAfterTheDocumentChanges()
        {
            var (_, para, search) = NewOutput("first ");
            Assert.Equal("first ", search.Text);

            para.Inlines.Add(new Run("second"));
            search.Invalidate();

            Assert.Equal("first second", search.Text);
            Assert.Single(search.FindAll("first second"));
        }
    }
}
