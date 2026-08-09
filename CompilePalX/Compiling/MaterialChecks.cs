using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CompilePalX.Compiling
{
    /// <summary>
    /// Reports the two vbsp material warnings that have a real cause in a VMT, as opposed to
    /// content simply being absent.
    ///
    /// Report-only on purpose. Both faults live in the material rather than the map, and a map's
    /// materials are routinely someone else's content - shared across maps, mounted from another
    /// game, or inside a VPK. Rewriting those silently during a compile would edit files the mapper
    /// did not think they were changing, and the fix is a one-line manual edit. So this names the
    /// exact file and line instead of guessing.
    /// </summary>
    public static class MaterialChecks
    {
        private static readonly Regex SurfacePropRegex =
            new(@"^\s*\$?surfaceprop\d?\s+""?([^""\r\n]+)""?", RegexOptions.IgnoreCase | RegexOptions.Multiline);

        /// <summary>Names appearing in <c>scripts/surfaceproperties*.txt</c> as `name` { ... }.</summary>
        private static readonly Regex SurfaceDefRegex =
            new(@"^\s*""?([A-Za-z0-9_]+)""?\s*$", RegexOptions.Multiline);

        public static void Report(VmfDocument vmf, List<string> contentDirs)
        {
            if (contentDirs.Count == 0)
            {
                CompilePalLogger.LogLineDebug("No content directories resolved; skipping material checks.");
                return;
            }

            var known = LoadSurfaceProperties(contentDirs);
            var materials = vmf.CollectMaterials();

            var badSurfaceProps = new List<string>();
            var badWater = new List<string>();

            foreach (string material in materials)
            {
                string? vmtPath = Resolve($"materials/{material}.vmt", contentDirs);
                if (vmtPath == null)
                    continue;   // in a VPK or genuinely missing - not something we can inspect

                string text;
                try { text = File.ReadAllText(vmtPath); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                // Only meaningful when we managed to read a manifest; otherwise every name looks unknown.
                if (known.Count > 0)
                {
                    foreach (Match m in SurfacePropRegex.Matches(text))
                    {
                        string prop = m.Groups[1].Value.Trim();
                        if (prop.Length > 0 && !known.Contains(prop))
                            badSurfaceProps.Add($"  {material}: $surfaceprop \"{prop}\" is not defined  ({vmtPath})");
                    }
                }

                bool hasLightmapWaterFog = text.IndexOf("$lightmapwaterfog", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasFlowMap = text.IndexOf("$flowmap", StringComparison.OrdinalIgnoreCase) >= 0;
                if (hasLightmapWaterFog && !hasFlowMap)
                    badWater.Add($"  {material}: $lightmapwaterfog has no effect without $flowmap  ({vmtPath})");
            }

            if (badSurfaceProps.Count > 0)
            {
                CompilePalLogger.LogLine($"\n{badSurfaceProps.Count} material(s) reference an undefined $surfaceprop:");
                foreach (string line in badSurfaceProps.Distinct().Take(50))
                    CompilePalLogger.LogLine(line);
                CompilePalLogger.LogLine("  vbsp falls back to the default surface, so footsteps and impacts will be wrong.");
                CompilePalLogger.LogLine("  Fix by editing the VMT to a name from scripts/surfaceproperties*.txt.");
            }

            if (badWater.Count > 0)
            {
                CompilePalLogger.LogLine($"\n{badWater.Count} water material(s) with an ineffective $lightmapwaterfog:");
                foreach (string line in badWater.Distinct().Take(50))
                    CompilePalLogger.LogLine(line);
                CompilePalLogger.LogLine("  Either add $flowmap or remove $lightmapwaterfog from the VMT.");
            }

            if (badSurfaceProps.Count == 0 && badWater.Count == 0)
                CompilePalLogger.LogLine($"Checked {materials.Count} material(s); no VMT issues found.");
        }

        private static string? Resolve(string relative, List<string> contentDirs)
        {
            foreach (string dir in contentDirs)
            {
                string full;
                try { full = Path.Combine(dir, relative); }
                catch (ArgumentException) { continue; }

                if (File.Exists(full))
                    return full;
            }
            return null;
        }

        /// <summary>
        /// Collects every surface name the game defines. These files are KeyValues where each
        /// top-level block is the surface name, so the names are the lines standing alone before a
        /// brace.
        /// </summary>
        private static HashSet<string> LoadSurfaceProperties(List<string> contentDirs)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in contentDirs)
            {
                string scripts = Path.Combine(dir, "scripts");
                if (!Directory.Exists(scripts))
                    continue;

                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(scripts, "surfaceproperties*.txt"); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (string file in files)
                {
                    try
                    {
                        foreach (Match m in SurfaceDefRegex.Matches(File.ReadAllText(file)))
                            names.Add(m.Groups[1].Value);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            return names;
        }
    }
}
