using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Runtime.InteropServices;

namespace CompilePalX.Compiling
{
    internal delegate Run? LogWrite(string s, Brush? b, int? fontWeight);
    internal delegate Run? LogWriteURL(string s, string url, int? fontWeight);
    internal delegate void LogBacktrack(List<Run> l);
    internal delegate void CompileErrorLogWrite(string errorText, Error e);

    internal delegate void CompileErrorFound(Error e);


    static class CompilePalLogger
    {
        private static readonly string logFile = "./debug.log";
        static CompilePalLogger()
        {
            File.Delete(logFile);

            // print debug information
            LogLine($"--- Compile Pal {UpdateManager.CurrentVersion} ---");
            LogLine($"Runtime: {RuntimeInformation.RuntimeIdentifier}");
            LogLine($"Locale: {CultureInfo.CurrentCulture.Name}");
        }
        public static event LogWrite OnWrite;
        public static event LogWriteURL OnWriteURL;
        public static event LogBacktrack OnBacktrack;
        public static event CompileErrorLogWrite OnErrorLog;

        public static event CompileErrorFound OnErrorFound;


        public static Run LogColor(string s, Brush? b, int? fontWeight, params object[] formatStrings)
        {
            string text = s;
            if (formatStrings.Length != 0)
            {
                text = string.Format(s, formatStrings);
            }

            try
            {
                File.AppendAllText(logFile, text);

            }
            catch { }

            return OnWrite?.Invoke(text, b, fontWeight);
        }


        public static Run LogLineColor(string s, Brush b, params object[] formatStrings)
        {
            return LogColor(s + Environment.NewLine, b, null, formatStrings);
        }

        public static Run? Log(string s = "", params object[] formatStrings)
        {
            return Log(s, null, formatStrings);
        }

        public static Run? Log(string s = "", int? fontWeight = null, params object[] formatStrings)
        {
            // listen for variable updates for plugins
            if (s.StartsWith("COMPILE_PAL_SET"))
            {
                GameConfigurationManager.ModifyCurrentContext(s);
                return null;

            }

            return LogColor(s, null, fontWeight, formatStrings);
        }

        public static Run? LogLine(string s, int fontWeight, params object[] formatStrings)
        {
            return Log(s + Environment.NewLine, fontWeight, formatStrings);
        }
        public static Run? LogLine(string s = "", params object[] formatStrings)
        {
            return Log(s + Environment.NewLine, formatStrings);
        }

        public static Run? LogLineFileLocation(string s, string url)
        {
            return OnWriteURL.Invoke(s + Environment.NewLine, url, 600);
        }

        public static void LogDebug(string s)
        {
            // log in debug, no op in release
#if DEBUG
            try
            {
                File.AppendAllText(logFile, s);
            } catch { }
#endif
        }

        public static void LogLineDebug(string s)
        {
            Trace.WriteLine(s);
            LogDebug(s + Environment.NewLine);
        }


        public static void LogCompileError(string errorText, Error e)
        {
            if (errorsFound.ContainsKey(e))
                errorsFound[e]++;
            else
                errorsFound.Add(e, 1);

            if (errorsFound[e] < 128)
                OnErrorLog(errorText, e);
            else
                Log(errorText); //Stop hyperlinking errors if we see over 128 of them
            
            File.AppendAllText(logFile, errorText);
            OnErrorFound(e);
        }
        public static void LogLineCompileError(string errorText, Error e)
        {
            LogCompileError(errorText + Environment.NewLine, e);
        }

        private static Dictionary<Error, int> errorsFound = [];

        private static StringBuilder lineBuffer = new ();
        private static List<Run> tempText = [];

        /// <summary>
        /// Drops everything left over from the previous compile. Called as a run starts, once the
        /// output document has been cleared.
        ///
        /// All three of these are process-wide and none of them used to be reset, which is what put
        /// the tail of the last compile at the top of the next one's output. A compile tool's stdout
        /// arrives in fixed-size chunks, so <see cref="LogProgressive"/> almost always ends a run
        /// holding a partial line in <see cref="lineBuffer"/> - and cancelling makes that certain,
        /// because the reader returns the moment the token trips, mid-line by definition. The next
        /// compile's first chunk was then appended to that remnant and the whole thing emitted as one
        /// line: the previous run's trailing text, leading the new run's first.
        ///
        /// <see cref="tempText"/> is the same problem one step further on - it holds Run objects that
        /// belong to a FlowDocument that has since been cleared, and the first backtrack of the new
        /// compile would blank those instead of anything on screen. <see cref="errorsFound"/> is the
        /// per-error occurrence count behind the 128-hyperlink cap; carried across runs, a long
        /// compile could exhaust the cap for an error that the *new* run had barely reported.
        /// </summary>
        public static void ResetOutputState()
        {
            lineBuffer.Clear();
            tempText.Clear();
            errorsFound.Clear();
        }

        /// <summary>
        /// Labels a step prints in the column ahead of a message to say what kind of line it is.
        /// Meshwright writes "bsp", "nav", "out" and "check" this way, alongside the "warn" and
        /// "error:" that the error catalogue recognises as a warning and an error.
        ///
        /// These four are informational, so they are coloured and nothing else. Recognising them
        /// through the catalogue - which is how the other two get their colour - would also file each
        /// one as an issue, and so put four entries in the issues list, the footer's warning counter
        /// and the map card on a compile where nothing at all was wrong.
        ///
        /// A fixed set rather than a shape. "Short lowercase word at the start of a line" also
        /// describes vbsp's lump usage table - planes, vertexes, pakfile - which is data, not a level,
        /// and "done" begins a line every time a vbsp step finishes.
        /// </summary>
        private static readonly Regex InfoLabel = new(@"^(?:bsp|nav|out|check)\s", RegexOptions.Compiled);

        /// <summary>
        /// The Info brush, resolved once and reused.
        ///
        /// <see cref="Error.GetSeverityBrush"/> reads Application.Resources, which belongs to the UI
        /// thread, while this runs on the compile thread for every line of output - so it is resolved
        /// through the dispatcher, the way every other severity colour in the app is. The theme
        /// freezes its brushes, so the one instance is safe to hand back to any thread afterwards.
        /// Null if there is no window yet, which logs the line plainly rather than failing.
        /// </summary>
        private static readonly Lazy<Brush?> infoBrush = new(() =>
        {
            try
            {
                return MainWindow.ActiveDispatcher.Invoke(() => Error.GetSeverityBrush(1));
            }
            catch
            {
                return null;
            }
        });

        /// <summary>
        /// Logs one finished line, separating progress text from a diagnostic printed onto the end of
        /// it.
        ///
        /// The Source tools announce a step as "Building Faces..." and leave the line open, so the
        /// "done (0)" that follows lands on the same line. Anything else printed in between lands
        /// there too, and a warning raised while a step is running therefore reaches us glued to the
        /// step's own text:
        ///
        ///     Building Faces...Water: $LightMapWaterFog doesn't work without $FlowMap
        ///
        /// Splitting on line endings cannot separate these - there is no line ending between them.
        /// The message is still recognised, because the catalogue matches unanchored, but the whole
        /// line is then coloured and hyperlinked as the warning, and the issues list takes its summary
        /// from the whole line as well - listing the warning with an unrelated progress message stuck
        /// to its front.
        ///
        /// So when a message is recognised part-way into a line, look back for the trailing dots the
        /// tools use to hold a line open. Those dots are the boundary: what precedes them is progress
        /// text and is logged plainly, and what follows is re-examined on its own, which is what puts
        /// the colour and the hyperlink on the warning alone. With no such dots - the usual case of a
        /// message that simply does not begin at column zero, "Light at (x y z) has ..." - the line is
        /// left exactly as it was, since chopping it there would throw away the part that identifies
        /// which light it means.
        /// </summary>
        private static void LogCompletedLine(string line)
        {
            Error? error = ErrorFinder.GetError(line, out int matchIndex);

            if (error == null)
            {
                if (InfoLabel.IsMatch(line) && infoBrush.Value is { } brush)
                    LogLineColor(line, brush);
                else
                    LogLine(line);
                return;
            }

            if (matchIndex > 0)
            {
                int dots = line[..matchIndex].LastIndexOf("...", StringComparison.Ordinal);
                if (dots >= 0)
                {
                    LogLine(line[..(dots + 3)]);

                    // the remainder may itself be more than one message, so run it through again
                    LogCompletedLine(line[(dots + 3)..]);
                    return;
                }
            }

            LogLineCompileError(line, error);
        }

        public static void LogProgressive(string s)
        {
            lineBuffer.Append(s);

            // Any line ending at all, matching the split below. With only "\n" tested here a
            // chunk ending in a bare "\r" was treated as still-in-progress text and echoed to the
            // live run, then split into a finished line a moment later - printing it twice.
            if (s.IndexOfAny(['\n', '\r']) < 0)
            {
                Run? log = Log(s);
                if (log != null)
                    tempText.Add(log);
            }

            // Log has completed at least 1 line, process it further
            /*
             * Split on every line ending the compile tools actually emit, not just CRLF.
             *
             * This was Split("\r\n") alone. The Source tools are not consistent: vbsp and vrad write
             * "\r\n" through printf, but messages routed via their Msg()/Warning() paths, and much of
             * what ficool2's rebuilt tools print, arrive as a bare "\n". Those never terminated a line
             * here, so the text stayed in lineBuffer and was emitted glued to whatever came next -
             *
             *     Building Faces...Water: $LightMapWaterFog doesn't work without $FlowMap
             *
             * which is two messages from two subsystems on one line. Not merely ugly: GetError matches
             * unanchored so the warning was still recognised, but the issues list takes its summary from
             * the whole line, and so listed it with an unrelated progress message stuck to its front.
             *
             * A lone "\r" is a carriage return with no line feed, which the tools use to overwrite a
             * counter in place. It ends a line for our purposes - the text before it is complete and
             * will never be appended to - even though a console would redraw over it.
             */
            List<string> lines = lineBuffer.ToString()
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .ToList();

            string suffixText = lines.Last();

            lineBuffer = new StringBuilder(suffixText);

            OnBacktrack(tempText);

            for (int i = 0; i < lines.Count - 1; i++)
                LogCompletedLine(lines[i]);

            if (suffixText.Length > 0)
            {
                Run? log = Log(suffixText);
                if (log != null)
                    tempText = [log];
            }
        }
    }
}
