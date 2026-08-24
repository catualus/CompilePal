using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CompilePalX.Compiling
{
    /// <summary>
    /// A VMF held as its original lines, with an index of where each entity's key/values live.
    ///
    /// Deliberately NOT a parse-and-reserialise model. These files run to tens of megabytes of
    /// mapper-authored data, and rewriting one wholesale would mean reproducing Hammer's exact
    /// formatting - every quirk of indentation, ordering and float formatting - or silently
    /// rewriting the map. Editing lines in place means the output is byte-identical apart from the
    /// values we deliberately change.
    /// </summary>
    public sealed class VmfDocument
    {
        private readonly List<string> lines;
        private readonly string newline;
        private readonly HashSet<int> removedLines = [];

        public IReadOnlyList<VmfEntity> Entities { get; }

        /// <summary>True once any value has actually been changed.</summary>
        public bool Modified { get; private set; }

        private VmfDocument(List<string> lines, string newline, List<VmfEntity> entities)
        {
            this.lines = lines;
            this.newline = newline;
            Entities = entities;
        }

        public static VmfDocument Load(string path)
        {
            string raw = File.ReadAllText(path);
            // Hammer writes CRLF; preserve whatever this file actually uses.
            string nl = raw.Contains("\r\n") ? "\r\n" : "\n";
            var lines = raw.Split(new[] { nl }, StringSplitOptions.None).ToList();

            return new VmfDocument(lines, nl, IndexEntities(lines));
        }

        /// <summary>
        /// Finds each top-level <c>entity { ... }</c> and records its direct key/values.
        ///
        /// Only depth-1 keys are recorded: an entity also contains nested <c>editor</c>,
        /// <c>connections</c> and (for brush entities) <c>solid</c> blocks, whose keys must not be
        /// confused with the entity's own. A solid's "material" and an entity's "model" living at
        /// different depths is exactly the sort of thing a naive regex sweep gets wrong.
        /// </summary>
        private static List<VmfEntity> IndexEntities(List<string> lines)
        {
            var entities = new List<VmfEntity>();

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Trim() != "entity")
                    continue;

                // The brace is on the following line in every VMF Hammer writes.
                int open = i + 1;
                while (open < lines.Count && lines[open].Trim().Length == 0) open++;
                if (open >= lines.Count || lines[open].Trim() != "{")
                    continue;

                var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var children = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string lastToken = "";
                int depth = 1;
                int j = open + 1;

                for (; j < lines.Count && depth > 0; j++)
                {
                    string t = lines[j].Trim();

                    if (t == "{")
                    {
                        depth++;
                        // A block's name is the bare word on the line before its brace, so a "solid"
                        // opening here is one of this entity's own child blocks. Recorded because
                        // "brush entity with no brushes" is visible only as an ABSENCE, and an absence
                        // cannot be found by looking at the keys that are present.
                        if (depth == 2 && lastToken.Length > 0)
                            children.Add(lastToken);
                        continue;
                    }
                    if (t == "}") { depth--; continue; }

                    if (depth == 1 && TryReadKey(t, out string key))
                        keys[key] = j;   // last wins, matching how the engine reads duplicates
                    else if (depth == 1 && t.Length > 0 && t[0] != '"')
                        lastToken = t;
                }

                entities.Add(new VmfEntity(i, j - 1, keys, children));
                i = j - 1;
            }

            return entities;
        }

        private static bool TryReadKey(string trimmed, out string key)
        {
            key = "";
            if (trimmed.Length < 2 || trimmed[0] != '"') return false;

            int close = trimmed.IndexOf('"', 1);
            if (close < 0) return false;

            key = trimmed.Substring(1, close - 1);
            return true;
        }

        public string? GetValue(VmfEntity entity, string key)
        {
            if (!entity.Keys.TryGetValue(key, out int line))
                return null;

            string t = lines[line].Trim();
            int firstClose = t.IndexOf('"', 1);
            if (firstClose < 0) return null;

            int valueOpen = t.IndexOf('"', firstClose + 1);
            if (valueOpen < 0) return null;

            int valueClose = t.LastIndexOf('"');
            if (valueClose <= valueOpen) return null;

            return t.Substring(valueOpen + 1, valueClose - valueOpen - 1);
        }

        public string? Classname(VmfEntity entity) => GetValue(entity, "classname");

        /// <summary>Rewrites one key's value, preserving the line's original indentation.</summary>
        public bool SetValue(VmfEntity entity, string key, string value)
        {
            if (!entity.Keys.TryGetValue(key, out int line))
                return false;

            string original = lines[line];
            string indent = original.Substring(0, original.Length - original.TrimStart().Length);

            lines[line] = $"{indent}\"{key}\" \"{value}\"";
            Modified = true;
            return true;
        }

        /// <summary>
        /// Drops an entity from the file.
        ///
        /// Recorded as a set of suppressed line numbers rather than by removing them from the list,
        /// because every VmfEntity holds absolute line indices into it. Deleting outright would shift
        /// every entity after this one and silently corrupt the next edit - the second removal in a
        /// run would cut the wrong lines.
        /// </summary>
        public void RemoveEntity(VmfEntity entity)
        {
            for (int i = entity.StartLine; i <= entity.EndLine && i < lines.Count; i++)
                removedLines.Add(i);

            Modified = true;
        }

        public void Save(string path)
        {
            var kept = removedLines.Count == 0
                ? lines
                : lines.Where((_, i) => !removedLines.Contains(i)).ToList();

            File.WriteAllText(path, string.Join(newline, kept), new UTF8Encoding(false));
        }

        /// <summary>
        /// All distinct brush face materials in the file.
        ///
        /// These live inside solid/side blocks rather than on entities, and the same handful of
        /// materials repeats tens of thousands of times, so this is a flat scan into a set rather
        /// than anything structural.
        /// </summary>
        public HashSet<string> CollectMaterials()
        {
            var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string line in lines)
            {
                string t = line.TrimStart();
                if (!t.StartsWith("\"material\"", StringComparison.OrdinalIgnoreCase))
                    continue;

                int valueOpen = t.IndexOf('"', 10);
                if (valueOpen < 0) continue;
                int valueClose = t.LastIndexOf('"');
                if (valueClose <= valueOpen) continue;

                string mat = t.Substring(valueOpen + 1, valueClose - valueOpen - 1).Trim();
                if (mat.Length > 0)
                    materials.Add(mat.Replace('\\', '/'));
            }

            return materials;
        }
    }

    /// <summary>One top-level entity: where it sits in the file, and where its own keys are.</summary>
    public sealed class VmfEntity
    {
        public int StartLine { get; }
        public int EndLine { get; }
        public IReadOnlyDictionary<string, int> Keys { get; }

        /// <summary>Names of this entity's own child blocks - "solid", "editor", "connections".</summary>
        public IReadOnlySet<string> Children { get; }

        public VmfEntity(int startLine, int endLine, Dictionary<string, int> keys, HashSet<string> children)
        {
            StartLine = startLine;
            EndLine = endLine;
            Keys = keys;
            Children = children;
        }
    }
}
