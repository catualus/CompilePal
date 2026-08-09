using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Documents;
using System.Windows.Media;

namespace CompilePalX.Compiling
{
    /// <summary>
    /// Find-in-output over the compile log's FlowDocument: locating matches and painting them.
    ///
    /// Separate from MainWindow so it can be tested. The two behaviours here are both ones that
    /// look fine by inspection and fail in practice, so they are worth having under test:
    /// matches that span run boundaries, and the fact that painting a highlight fragments the
    /// document the next search has to run against.
    /// </summary>
    public sealed class OutputSearch
    {
        /// <summary>Guards against pathological queries (a single space on a 200k-line log).</summary>
        public const int MaxMatches = 5000;

        // Painted directly onto the text rather than using the RichTextBox selection. Search runs
        // with focus in the search box, so a selection only ever renders as an *inactive* one and
        // stays effectively invisible; it could also only ever mark a single match, where a find
        // bar is expected to show all of them.
        public static readonly Brush MatchBrush = Frozen(0x66, 0xFF, 0xD5, 0x4F);
        public static readonly Brush CurrentMatchBrush = Frozen(0xCC, 0xFF, 0x8C, 0x00);

        private static Brush Frozen(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }

        private readonly FlowDocument document;
        private readonly List<TextRange> highlighted = [];

        /// <summary>One contiguous run of text, and where it sits in the flattened document text.</summary>
        private readonly record struct Segment(TextPointer Start, int Length, int GlobalOffset);

        private string flatText = "";
        private List<Segment> segments = [];
        private bool dirty = true;

        public OutputSearch(FlowDocument document)
        {
            this.document = document ?? throw new ArgumentNullException(nameof(document));
        }

        /// <summary>
        /// Marks the cached text/pointer map stale. Must be called whenever the document changes -
        /// including after highlighting, because painting a background splits Runs.
        /// </summary>
        public void Invalidate() => dirty = true;

        /// <summary>Forgets highlight bookkeeping without touching the document (used after a clear).</summary>
        public void Reset()
        {
            highlighted.Clear();
            dirty = true;
        }

        /// <summary>
        /// Rebuilds the flattened text and its map back to TextPointers.
        ///
        /// TextRange.Text offsets cannot be fed to GetPositionAtOffset: those offsets count every
        /// element boundary (each Run/Hyperlink open and close) as a symbol, so they desynchronise
        /// from plain text almost immediately in a document built from thousands of small inlines.
        /// Recording each run's start pointer and its offset within the concatenated text sidesteps
        /// that, because an offset INSIDE a single run is safe to resolve.
        /// </summary>
        private void Rebuild()
        {
            var text = new StringBuilder();
            var segs = new List<Segment>();

            var pointer = document.ContentStart;
            while (pointer != null)
            {
                if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
                {
                    string runText = pointer.GetTextInRun(LogicalDirection.Forward);
                    if (runText.Length > 0)
                    {
                        segs.Add(new Segment(pointer, runText.Length, text.Length));
                        text.Append(runText);
                    }

                    pointer = pointer.GetPositionAtOffset(runText.Length);
                }
                else
                {
                    pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
                }
            }

            flatText = text.ToString();
            segments = segs;
            dirty = false;
        }

        /// <summary>Flattened plain text of the whole document. Rebuilt on demand.</summary>
        public string Text
        {
            get
            {
                if (dirty) Rebuild();
                return flatText;
            }
        }

        private TextPointer? PointerAtOffset(int globalOffset)
        {
            int lo = 0, hi = segments.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                var seg = segments[mid];

                if (globalOffset < seg.GlobalOffset)
                    hi = mid - 1;
                else if (globalOffset > seg.GlobalOffset + seg.Length)
                    lo = mid + 1;
                else
                    // <= end of segment on purpose: a match's END offset legitimately lands on the
                    // boundary just past the last character of a run.
                    return seg.Start.GetPositionAtOffset(globalOffset - seg.GlobalOffset);
            }

            return null;
        }

        /// <summary>All case-insensitive matches for <paramref name="query"/>, in document order.</summary>
        public List<TextRange> FindAll(string query)
        {
            var matches = new List<TextRange>();
            if (string.IsNullOrEmpty(query))
                return matches;

            if (dirty) Rebuild();

            int searchFrom = 0;
            int found;
            while ((found = flatText.IndexOf(query, searchFrom, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var start = PointerAtOffset(found);
                var end = PointerAtOffset(found + query.Length);
                if (start != null && end != null)
                    matches.Add(new TextRange(start, end));

                if (matches.Count >= MaxMatches)
                    break;

                searchFrom = found + 1;
            }

            return matches;
        }

        public void ClearHighlights()
        {
            if (highlighted.Count == 0)
                return;

            foreach (var range in highlighted)
                range.ApplyPropertyValue(TextElement.BackgroundProperty, null);

            highlighted.Clear();
            Invalidate();
        }

        /// <summary>
        /// Paints every match, with <paramref name="currentIndex"/> in the stronger colour.
        /// Pass -1 for no current match.
        /// </summary>
        public void Highlight(IReadOnlyList<TextRange> matches, int currentIndex)
        {
            ClearHighlights();

            for (int i = 0; i < matches.Count; i++)
            {
                var range = matches[i];
                range.ApplyPropertyValue(TextElement.BackgroundProperty,
                    i == currentIndex ? CurrentMatchBrush : MatchBrush);
                highlighted.Add(range);
            }

            Invalidate();
        }

        /// <summary>Paints a single arbitrary range as the "current" marker (used by error navigation).</summary>
        public void HighlightSingle(TextRange range)
        {
            ClearHighlights();
            range.ApplyPropertyValue(TextElement.BackgroundProperty, CurrentMatchBrush);
            highlighted.Add(range);
            Invalidate();
        }
    }
}
