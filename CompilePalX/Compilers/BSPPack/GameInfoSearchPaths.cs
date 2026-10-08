using System;
using System.Collections.Generic;

namespace CompilePalX.Compilers.BSPPack
{
    /// <summary>
    /// Reads the FileSystem > SearchPaths block of a gameinfo.txt a line at a time, for when the file is
    /// not valid KeyValues.
    ///
    /// The usual way to break it is a path with a space in it and no quotes:
    ///
    ///     game+mod    garrysmod/my addons/*
    ///
    /// KeyValues splits unquoted text on whitespace, so that line is three tokens, and every key and
    /// value after it is off by one until the closing brace finds a key with no value. ValveKeyValue
    /// throws there - "Attempted to finalize object while in state InObjectBetweenKeyAndValue" - which
    /// names the brace, not the line at fault, and used to take the whole compile down with it. The
    /// engine's own reader shrugs and carries on, so the game still starts and nothing tells the user
    /// their gameinfo.txt is wrong until Compile Pal refuses it.
    ///
    /// The search paths are laid out one to a line, so here a line is a key followed by its path: the
    /// first token is the key and the rest of the line, quotes and trailing conditional removed, is the
    /// path. That is what whoever wrote the line meant, which is the most useful reading. Lines that
    /// only parse this way are reported, so they can be fixed at the source.
    /// </summary>
    internal static class GameInfoSearchPaths
    {
        internal sealed record Problem(int Line, string Text, string Reason);

        /// <summary>
        /// The path of every entry in FileSystem > SearchPaths, in order. Entries that strict KeyValues
        /// would have misread are added to <paramref name="problems"/>.
        /// </summary>
        public static List<string> Read(string text, List<Problem> problems)
        {
            var paths = new List<string>();
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            // Block names seen so far, outermost first, so SearchPaths is only recognised inside
            // FileSystem, as the engine looks for it.
            var blocks = new List<string>();
            string? lastKey = null;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = StripComment(lines[i]).Trim();

                while (line.Length > 0)
                {
                    if (line[0] == '{')
                    {
                        blocks.Add(lastKey ?? "");
                        lastKey = null;
                        line = line[1..].TrimStart();
                        continue;
                    }

                    if (line[0] == '}')
                    {
                        if (blocks.Count > 0)
                            blocks.RemoveAt(blocks.Count - 1);
                        lastKey = null;
                        line = line[1..].TrimStart();
                        continue;
                    }

                    (string key, string rest) = SplitFirstToken(line);

                    // "Name {" on one line: the brace opens a block named by the key before it.
                    if (rest.StartsWith('{'))
                    {
                        lastKey = key;
                        line = rest;
                        continue;
                    }

                    if (!InSearchPaths(blocks))
                    {
                        // a key on its own line names the block whose brace comes next
                        lastKey = rest.Length == 0 ? key : null;
                        break;
                    }

                    string path = StripCondition(rest).Trim();
                    if (path.Length == 0)
                    {
                        problems.Add(new Problem(i + 1, line, "has no path"));
                        break;
                    }

                    if (path[0] == '"')
                    {
                        int close = path.IndexOf('"', 1);
                        path = close < 0 ? path[1..] : path[1..close];
                    }
                    else if (path.AsSpan().IndexOfAny(" \t") >= 0)
                    {
                        problems.Add(new Problem(i + 1, line, "has a space in its path but no quotes around it"));
                    }

                    paths.Add(path);
                    break;
                }
            }

            return paths;
        }

        private static bool InSearchPaths(List<string> blocks) =>
            blocks.Count >= 2
            && blocks[^1].Equals("SearchPaths", StringComparison.OrdinalIgnoreCase)
            && blocks[^2].Equals("FileSystem", StringComparison.OrdinalIgnoreCase);

        /// <summary>The first token, unquoted, and everything after it.</summary>
        private static (string Token, string Remainder) SplitFirstToken(string line)
        {
            if (line[0] == '"')
            {
                int close = line.IndexOf('"', 1);
                if (close < 0)
                    return (line[1..], "");
                return (line[1..close], line[(close + 1)..].TrimStart());
            }

            int end = 0;
            while (end < line.Length && !char.IsWhiteSpace(line[end]) && line[end] != '{' && line[end] != '}')
                end++;
            return (line[..end], line[end..].TrimStart());
        }

        /// <summary>Drops a trailing [$WIN32]-style platform condition.</summary>
        private static string StripCondition(string rest)
        {
            rest = rest.TrimEnd();
            if (rest.EndsWith(']'))
            {
                int open = rest.LastIndexOf('[');
                if (open >= 0 && (open == 0 || char.IsWhiteSpace(rest[open - 1])))
                    return rest[..open];
            }
            return rest;
        }

        /// <summary>Removes a // comment, but not one inside quotes - a URL value is not a comment.</summary>
        private static string StripComment(string line)
        {
            bool quoted = false;
            for (int i = 0; i < line.Length - 1; i++)
            {
                if (line[i] == '"')
                    quoted = !quoted;
                else if (!quoted && line[i] == '/' && line[i + 1] == '/')
                    return line[..i];
            }
            return line;
        }
    }
}
