using System;
using System.IO;
using CompilePalX.Preview;
using Newtonsoft.Json.Linq;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Viswright's review in the preview: the decisions file and the review it names are copied in, stale
    /// copies from another map are cleared, and the page can only answer items Viswright already asked about.
    /// </summary>
    public class ViswrightReviewTests : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "CompilePalViswright_" + Guid.NewGuid().ToString("N"));
        private readonly string preview;
        private readonly string decisions;

        public ViswrightReviewTests()
        {
            preview = Directory.CreateDirectory(Path.Combine(root, "preview")).FullName;
            decisions = ViswrightReview.DecisionsPathFor(Path.Combine(root, "maps", "rp_test.vmf"));
            Directory.CreateDirectory(Path.GetDirectoryName(decisions)!);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        private void WriteDecisions(string? reviewFolder)
        {
            var file = new JObject
            {
                ["items"] = new JArray
                {
                    new JObject { ["id"] = "hints:1", ["decision"] = "pending" },
                    new JObject { ["id"] = "areaportals:2", ["decision"] = "accept" },
                },
            };
            if (reviewFolder != null)
                file["review"] = reviewFolder;
            File.WriteAllText(decisions, file.ToString());
        }

        [Fact]
        public void DecisionsSitBesideTheMap()
        {
            Assert.Equal(Path.Combine(root, "maps", "rp_test.viswright.json"), decisions);
        }

        [Fact]
        public void CopiesTheDecisionsItemsAndPictures()
        {
            string review = Directory.CreateDirectory(Path.Combine(root, "work", "review")).FullName;
            File.WriteAllText(Path.Combine(review, "items.json"), "[]");
            Directory.CreateDirectory(Path.Combine(review, "items"));
            File.WriteAllText(Path.Combine(review, "items", "1.png"), "png");
            File.WriteAllText(Path.Combine(review, "items", "1-3d.png"), "png");
            WriteDecisions(review);

            Assert.True(ViswrightReview.CopyInto(preview, decisions));
            Assert.True(File.Exists(Path.Combine(preview, ViswrightReview.DecisionsFile)));
            Assert.True(File.Exists(Path.Combine(preview, ViswrightReview.ItemsFile)));
            Assert.True(File.Exists(Path.Combine(preview, ViswrightReview.PictureFolder, "1.png")));
            Assert.True(File.Exists(Path.Combine(preview, ViswrightReview.PictureFolder, "1-3d.png")));
        }

        [Fact]
        public void AMissingReviewFolderStillShowsTheDecisions()
        {
            WriteDecisions(Path.Combine(root, "gone"));

            Assert.True(ViswrightReview.CopyInto(preview, decisions));
            Assert.True(File.Exists(Path.Combine(preview, ViswrightReview.DecisionsFile)));
            Assert.False(File.Exists(Path.Combine(preview, ViswrightReview.ItemsFile)));
        }

        [Fact]
        public void AMapWithoutDecisionsClearsTheLastMapsReview()
        {
            File.WriteAllText(Path.Combine(preview, ViswrightReview.DecisionsFile), "{}");
            File.WriteAllText(Path.Combine(preview, ViswrightReview.ItemsFile), "[]");
            Directory.CreateDirectory(Path.Combine(preview, ViswrightReview.PictureFolder));

            Assert.False(ViswrightReview.CopyInto(preview, decisions));
            Assert.False(ViswrightReview.CopyInto(preview, null));
            Assert.False(File.Exists(Path.Combine(preview, ViswrightReview.DecisionsFile)));
            Assert.False(File.Exists(Path.Combine(preview, ViswrightReview.ItemsFile)));
            Assert.False(Directory.Exists(Path.Combine(preview, ViswrightReview.PictureFolder)));
        }

        [Fact]
        public void DecideWritesTheItemsDecision()
        {
            WriteDecisions(null);

            Assert.True(ViswrightReview.Decide(decisions, "hints:1", "deny"));
            Assert.True(ViswrightReview.Decide(decisions, "areaportals:2", "deny"));

            var items = (JArray)JObject.Parse(File.ReadAllText(decisions))["items"]!;
            Assert.Equal("deny", items[0]!["decision"]!.ToString());
            Assert.Equal("deny", items[1]!["decision"]!.ToString());
        }

        [Theory]
        [InlineData("hints:1", "pending")]
        [InlineData("hints:1", "delete")]
        [InlineData("hints:9", "accept")]
        [InlineData("", "accept")]
        public void DecideTakesOnlyAnswersToItemsInTheFile(string id, string decision)
        {
            WriteDecisions(null);
            string before = File.ReadAllText(decisions);

            Assert.False(ViswrightReview.Decide(decisions, id, decision));
            Assert.Equal(before, File.ReadAllText(decisions));
        }

        [Fact]
        public void DecideWithoutAFileDoesNothing()
        {
            Assert.False(ViswrightReview.Decide(decisions, "hints:1", "accept"));
            Assert.False(File.Exists(decisions));
        }
    }
}
