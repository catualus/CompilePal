using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Where ENTLUMP sits in the compile order, pinned.
    ///
    /// Every one of these relationships is load-bearing, and getting one wrong produces a map that
    /// compiles without complaint and is broken in a way nothing reports. They are asserted from
    /// the shipped meta.json files rather than from a constant, so reordering a step in the data
    /// fails here rather than in somebody's map.
    /// </summary>
    public class EntityLumpOrderTests
    {
        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "CompilePalX", "CompilePalX.csproj")))
                    return dir.FullName;

                dir = dir.Parent;
            }

            throw new InvalidOperationException($"Could not find the repository above {AppContext.BaseDirectory}");
        }

        private sealed class Meta
        {
            public string Name { get; set; } = "";
            public double Order { get; set; }
            public bool DoRun { get; set; }
            public bool SupportsBSP { get; set; }
            public string Warning { get; set; } = "";
        }

        private static Meta Read(string step)
        {
            string path = Path.Combine(Root(), "CompilePalX", "Parameters", step, "meta.json");
            string json = Regex.Replace(File.ReadAllText(path), @"^\s*//.*$", "", RegexOptions.Multiline);
            return JsonConvert.DeserializeObject<Meta>(json)!;
        }

        private static double OrderOf(string step) => Read(step).Order;

        /// <summary>
        /// PACK walks the entity lump to find the models, materials and sounds a map references.
        /// Stripping first would produce a BSP packed with almost nothing, and the failure is
        /// silent: the map loads, and its custom content is missing for everyone else.
        /// </summary>
        [Fact]
        public void EntityExtractionRunsAfterPacking()
        {
            Assert.True(OrderOf("ENTLUMP") > OrderOf("PACK"),
                "ENTLUMP must run after PACK, which reads the entity lump to find map dependencies");
        }

        /// <summary>
        /// CUBEMAPS needs the env_cubemap entities to know where to build from, and writes its
        /// results back into the BSP afterwards.
        /// </summary>
        [Fact]
        public void EntityExtractionRunsAfterCubemaps()
        {
            Assert.True(OrderOf("ENTLUMP") > OrderOf("CUBEMAPS"),
                "ENTLUMP must run after CUBEMAPS, which needs env_cubemap entities and rewrites the BSP");
        }

        /// <summary>NAV generation loads the map, which needs its entities.</summary>
        [Fact]
        public void EntityExtractionRunsAfterNavGeneration()
        {
            Assert.True(OrderOf("ENTLUMP") > OrderOf("NAV"));
        }

        /// <summary>
        /// The BSP being edited is the one in the maps folder, which COPY puts there.
        /// </summary>
        [Fact]
        public void EntityExtractionRunsAfterTheCopyToTheMapsFolder()
        {
            Assert.True(OrderOf("ENTLUMP") > OrderOf("COPY"));
        }

        /// <summary>
        /// The one that is easy to get backwards, and the reason this file exists.
        ///
        /// bspzip's -compress LZMA-compresses lumps including the entity lump, and a compressed
        /// lump cannot be read as text - so running after REPACK fails on exactly the maps most
        /// likely to want this. Running before it is also strictly better: REPACK rebuilds the lump
        /// layout from the header, so the blanked region is dropped rather than carried as padding.
        /// Measured on a real map: 44,233 bytes reclaimed.
        /// </summary>
        [Fact]
        public void EntityExtractionRunsBeforeRepacking()
        {
            Assert.True(OrderOf("ENTLUMP") < OrderOf("REPACK"),
                "ENTLUMP must run before REPACK: a compressed entity lump cannot be extracted, " +
                "and repacking afterwards reclaims the blanked bytes");

            Assert.True(OrderOf("ENTLUMP") < OrderOf("BSPZIP"));
        }

        /// <summary>Launching the game must come after the pair exists.</summary>
        [Fact]
        public void EntityExtractionRunsBeforeTheGameLaunches()
        {
            Assert.True(OrderOf("ENTLUMP") < OrderOf("GAME"));
        }

        /// <summary>
        /// Off unless asked for. It changes what a shipped map contains, and a map distributed
        /// without its .lmp loads with no entities at all.
        /// </summary>
        [Fact]
        public void EntityExtractionIsOffByDefaultAndWarnsWhy()
        {
            var meta = Read("ENTLUMP");

            Assert.False(meta.DoRun);
            Assert.False(string.IsNullOrWhiteSpace(meta.Warning));
            Assert.Contains(".lmp", meta.Warning);
        }

        /// <summary>
        /// It operates on a compiled BSP, so it is valid on a map queued as a .bsp rather than
        /// compiled from a .vmf.
        /// </summary>
        [Fact]
        public void EntityExtractionWorksOnAMapQueuedAsABsp()
        {
            Assert.True(Read("ENTLUMP").SupportsBSP);
        }

        /// <summary>
        /// No two steps may share an order value, or which runs first is left to sort stability.
        /// </summary>
        [Fact]
        public void NoTwoStepsShareAnOrderValue()
        {
            string parameters = Path.Combine(Root(), "CompilePalX", "Parameters");
            var seen = new Dictionary<double, string>();

            foreach (string dir in Directory.GetDirectories(parameters))
            {
                string name = Path.GetFileName(dir);
                if (!File.Exists(Path.Combine(dir, "meta.json"))) continue;

                double order = Read(name).Order;

                Assert.False(seen.ContainsKey(order),
                    $"{name} and {(seen.TryGetValue(order, out var other) ? other : "?")} both have order {order}");

                seen[order] = name;
            }
        }
    }
}
