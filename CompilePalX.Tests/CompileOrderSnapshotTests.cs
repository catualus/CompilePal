using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// The compile loop walks the ordered list of steps. That list is rebuilt - cleared and refilled -
    /// by OrderManager.UpdateOrder, which several UI actions call, including simply opening the ORDER
    /// tab.
    ///
    /// Iterating it directly meant any of those aborted the compile partway through with "collection
    /// was modified; enumeration operation may not execute". It surfaced after VRAD had already spent
    /// four minutes on the map, and the work was lost.
    /// </summary>
    public class CompileOrderSnapshotTests
    {
        [Fact]
        public void RebuildingACollectionWhileIteratingItThrows()
        {
            var order = new ObservableCollection<string> { "VBSP", "VVIS", "VRAD" };

            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (var step in order)
                {
                    // What UpdateOrder does to CurrentOrder while the compile is between steps.
                    if (step == "VBSP")
                    {
                        order.Clear();
                        foreach (var rebuilt in new[] { "VBSP", "VVIS", "VRAD" })
                            order.Add(rebuilt);
                    }
                }
            });
        }

        [Fact]
        public void IteratingASnapshotSurvivesTheRebuild()
        {
            var order = new ObservableCollection<string> { "VBSP", "VVIS", "VRAD" };
            var visited = new List<string>();

            foreach (var step in order.ToList())
            {
                visited.Add(step);

                if (step == "VBSP")
                {
                    order.Clear();
                    order.Add("something else entirely");
                }
            }

            // The compile finishes the steps it started with, which is the correct behaviour as well as
            // the safe one: changing the order mid-run must not change what this run is doing.
            Assert.Equal(new[] { "VBSP", "VVIS", "VRAD" }, visited);
        }

        private static string SourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "CompilePalX", "Compiling", "CompilingManager.cs");
                if (File.Exists(candidate))
                    return Path.Combine(dir.FullName, "CompilePalX", "Compiling");

                dir = dir.Parent;
            }

            throw new InvalidOperationException($"Could not find CompilingManager.cs above {AppContext.BaseDirectory}");
        }

        [Fact]
        public void TheCompileLoopDoesNotIterateTheLiveOrder()
        {
            string code = File.ReadAllText(Path.Combine(SourceDir(), "CompilingManager.cs"));

            Assert.DoesNotMatch(new Regex(@"foreach\s*\([^)]*\s+in\s+OrderManager\.CurrentOrder\s*\)"), code);
        }
    }
}
