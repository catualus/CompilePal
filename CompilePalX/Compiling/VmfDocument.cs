using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CompilePalX.Compiling
{
    /// <summary>
    /// A VMF held as its original lines, with an index of where each entity's key/values and child
    /// blocks live.
    ///
    /// Deliberately NOT a parse-and-reserialise model. These files run to tens of megabytes of
    /// mapper-authored data - the one this was last tested against is 53 MB - and rewriting one
    /// wholesale would mean reproducing Hammer's exact formatting, every quirk of indentation,
    /// ordering and float formatting, or silently rewriting the map. Editing lines in place means the
    /// output is byte-identical apart from what we deliberately change.
    /// </summary>
    public sealed class VmfDocument
    {
        private readonly List<string> lines;
        private readonly string newline;

        /// <summary>Lines suppressed by <see cref="RemoveEntity"/> / <see cref="RemoveBlock"/>.</summary>
        private readonly HashSet<int> removedLines = [];

        /// <summary>Lines to emit immediately BEFORE the keyed index.</summary>
        private readonly Dictionary<int, List<string>> insertions = [];

        public IReadOnlyList<VmfEntity> Entities { get; }

        /// <summary>
        /// The map's <c>world</c> block, or null in a malformed file.
        ///
        /// This is a top-level <c>world { }</c> block, NOT an <c>entity</c> - which is easy to get
        /// wrong, because its classname really is "worldspawn" and every reference to worldspawn in
        /// Valve's own documentation talks about it as though it were an entity like any other. A
        /// fixer that went looking for an entity whose classname is worldspawn found nothing on every
        /// real map ever made.
        /// </summary>
        public VmfEntity? World { get; }

        /// <summary>True once anything has actually been changed.</summary>
        public bool Modified { get; private set; }

        private VmfDocument(List<string> lines, string newline, List<VmfEntity> entities)
        {
            this.lines = lines;
            this.newline = newline;
            Entities = entities;
            World = entities.FirstOrDefault(e => e.IsWorld);
        }

        public static VmfDocument Load(string path)
        {
            string raw = File.ReadAllText(path);
            // Hammer writes CRLF; preserve whatever this file actually uses.
            string nl = raw.Contains("\r\n") ? "\r\n" : "\n";
            var lines = raw.Split(new[] { nl }, StringSplitOptions.None).ToList();

            return new VmfDocument(lines, nl, IndexBlocks(lines));
        }

        /// <summary>All lines in the file, for a fixer that needs to copy a block verbatim.</summary>
        public IReadOnlyList<string> Lines => lines;

        /// <summary>
        /// Finds each top-level <c>entity { ... }</c> and the single <c>world { ... }</c>, and records
        /// their direct key/values and child blocks.
        ///
        /// Only depth-1 keys are recorded: an entity also contains nested <c>editor</c>,
        /// <c>connections</c> and (for brush entities) <c>solid</c> blocks, whose keys must not be
        /// confused with the entity's own. A solid's "material" and an entity's "model" living at
        /// different depths is exactly the sort of thing a naive regex sweep gets wrong.
        /// </summary>
        private static List<VmfEntity> IndexBlocks(List<string> lines)
        {
            var entities = new List<VmfEntity>();

            for (int i = 0; i < lines.Count; i++)
            {
                string head = lines[i].Trim();
                bool isWorld = head == "world";
                if (!isWorld && head != "entity")
                    continue;

                // The brace is on the following line in every VMF Hammer writes.
                int open = i + 1;
                while (open < lines.Count && lines[open].Trim().Length == 0) open++;
                if (open >= lines.Count || lines[open].Trim() != "{")
                    continue;

                var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var blocks = new List<VmfBlock>();

                // Where each currently-open block began, so its range can be closed off exactly.
                var openBlocks = new Stack<(string Name, int Start)>();
                string lastToken = "";
                int depth = 1;
                int j = open + 1;

                for (; j < lines.Count && depth > 0; j++)
                {
                    string t = lines[j].Trim();

                    if (t == "{")
                    {
                        depth++;
                        openBlocks.Push((lastToken, j - 1));
                        lastToken = "";
                        continue;
                    }

                    if (t == "}")
                    {
                        depth--;
                        if (openBlocks.Count > 0)
                        {
                            var (name, start) = openBlocks.Pop();
                            // Only this entity's OWN children, not a side inside a solid.
                            if (depth == 1 && name.Length > 0)
                                blocks.Add(new VmfBlock(name, start, j));
                        }
                        continue;
                    }

                    if (depth == 1 && TryReadKey(t, out string key))
                        keys[key] = j;   // last wins, matching how the engine reads duplicates
                    else if (t.Length > 0 && t[0] != '"')
                        lastToken = t;
                }

                entities.Add(new VmfEntity(i, j - 1, keys, blocks, isWorld));
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

        /// <summary>
        /// Reads a key from a child block - a solid id, a side material.
        ///
        /// Scans the block rather than using an index, because these are not indexed: a large map has
        /// hundreds of thousands of them and indexing every one would cost far more than the handful
        /// of lookups anything actually does.
        /// </summary>
        public string? GetValue(VmfBlock block, string key)
        {
            string needle = $"\"{key}\"";

            for (int i = block.StartLine; i <= block.EndLine && i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (!t.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
                    continue;

                int valueOpen = t.IndexOf('"', needle.Length);
                if (valueOpen < 0) continue;

                int valueClose = t.LastIndexOf('"');
                if (valueClose <= valueOpen) continue;

                return t.Substring(valueOpen + 1, valueClose - valueOpen - 1);
            }

            return null;
        }

        /// <summary>Rewrites one key's value, preserving the line's original indentation.</summary>
        public bool SetValue(VmfEntity entity, string key, string value)
        {
            if (!entity.Keys.TryGetValue(key, out int line))
                return false;

            string original = lines[line];
            string indent = original[..(original.Length - original.TrimStart().Length)];

            lines[line] = $"{indent}\"{key}\" \"{value}\"";
            Modified = true;
            return true;
        }

        /// <summary>Whether a child block of this entity contains a nested block of the given name.</summary>
        public bool BlockContains(VmfBlock block, string nestedName)
        {
            for (int i = block.StartLine; i <= block.EndLine && i < lines.Count; i++)
                if (string.Equals(lines[i].Trim(), nestedName, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        /// <summary>
        /// Sides of this solid that carry a displacement but do not have four vertices.
        ///
        /// A displacement subdivides a quad, so vbsp refuses anything else ("Trying to create a
        /// non-quad displacement!"). Hammer will happily let you displace a triangle produced by a
        /// clip or a vertex edit, and says nothing until the compile.
        ///
        /// Counted from the side's own vertices_plus block, which lists one "v" per corner.
        /// </summary>
        public int CountNonQuadDisplacementSides(VmfBlock solid)
        {
            int bad = 0;
            int sideStart = -1;
            int verts = 0;
            bool inVertices = false;
            bool sideHasDisp = false;

            for (int i = solid.StartLine; i <= solid.EndLine && i < lines.Count; i++)
            {
                string t = lines[i].Trim();

                if (t == "side")
                {
                    // Close off the previous side before starting the next.
                    if (sideStart >= 0 && sideHasDisp && verts != 4)
                        bad++;

                    sideStart = i;
                    verts = 0;
                    sideHasDisp = false;
                    inVertices = false;
                    continue;
                }

                if (sideStart < 0) continue;

                if (t == "dispinfo") sideHasDisp = true;
                else if (t == "vertices_plus") inVertices = true;
                else if (t == "}") inVertices = false;
                else if (inVertices && t.StartsWith("\"v\"", StringComparison.Ordinal)) verts++;
            }

            if (sideStart >= 0 && sideHasDisp && verts != 4)
                bad++;

            return bad;
        }

        /// <summary>The verbatim lines of a child block, name line through closing brace.</summary>
        public List<string> BlockLines(VmfBlock block) =>
            lines.GetRange(block.StartLine, block.EndLine - block.StartLine + 1);

        /*
         * Structural edits are recorded rather than applied.
         *
         * Every VmfEntity and VmfBlock holds absolute line indices into one shared list, so deleting
         * or inserting lines outright would shift every range recorded after that point. The second
         * edit in a run would then act on the wrong lines - and on a 53 MB map, silently.
         */

        public void RemoveEntity(VmfEntity entity)
        {
            Suppress(entity.StartLine, entity.EndLine);
            Modified = true;
        }

        public void RemoveBlock(VmfBlock block)
        {
            Suppress(block.StartLine, block.EndLine);
            Modified = true;
        }

        private void Suppress(int from, int to)
        {
            for (int i = from; i <= to && i < lines.Count; i++)
                removedLines.Add(i);
        }

        /// <summary>Queues lines to be written immediately before <paramref name="line"/>.</summary>
        public void InsertBefore(int line, IEnumerable<string> newLines)
        {
            if (!insertions.TryGetValue(line, out var list))
                insertions[line] = list = [];

            list.AddRange(newLines);
            Modified = true;
        }

        /// <summary>
        /// Moves a solid into the world block, which is where displacements have to live.
        /// </summary>
        public bool MoveBlockToWorld(VmfBlock block)
        {
            if (World is null)
                return false;

            // Both an entity's solids and the world's sit one level inside a top-level block, so the
            // lines already carry the right indentation and can be re-used verbatim.
            InsertBefore(World.EndLine, BlockLines(block));
            RemoveBlock(block);
            return true;
        }

        public void Save(string path)
        {
            var output = new List<string>(lines.Count);

            for (int i = 0; i < lines.Count; i++)
            {
                if (insertions.TryGetValue(i, out var extra))
                    output.AddRange(extra);

                if (!removedLines.Contains(i))
                    output.Add(lines[i]);
            }

            if (insertions.TryGetValue(lines.Count, out var trailing))
                output.AddRange(trailing);

            File.WriteAllText(path, string.Join(newline, output), new UTF8Encoding(false));
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

    /// <summary>One child block of an entity: where its name line and closing brace are.</summary>
    public sealed class VmfBlock(string name, int startLine, int endLine)
    {
        public string Name { get; } = name;
        public int StartLine { get; } = startLine;
        public int EndLine { get; } = endLine;
    }

    /// <summary>One top-level block: where it sits, its own keys, and its own child blocks.</summary>
    public sealed class VmfEntity
    {
        public int StartLine { get; }
        public int EndLine { get; }
        public IReadOnlyDictionary<string, int> Keys { get; }

        /// <summary>This entity's own child blocks - "solid", "editor", "connections".</summary>
        public IReadOnlyList<VmfBlock> Blocks { get; }

        /// <summary>True for the map's single <c>world</c> block, which must never be removed.</summary>
        public bool IsWorld { get; }

        public IEnumerable<VmfBlock> Solids =>
            Blocks.Where(b => string.Equals(b.Name, "solid", StringComparison.OrdinalIgnoreCase));

        public bool HasChild(string name) =>
            Blocks.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

        public VmfEntity(int startLine, int endLine, Dictionary<string, int> keys,
                         List<VmfBlock> blocks, bool isWorld)
        {
            StartLine = startLine;
            EndLine = endLine;
            Keys = keys;
            Blocks = blocks;
            IsWorld = isWorld;
        }
    }
}
