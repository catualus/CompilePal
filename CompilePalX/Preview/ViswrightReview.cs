using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using CompilePalX.Compiling;

namespace CompilePalX.Preview
{
    /// <summary>
    /// Viswright's review of a map, brought into the preview: what it did and considered, drawn over the
    /// compiled map, with the items waiting for a decision listed beside it.
    ///
    /// Viswright keeps its decisions beside the map as <c>&lt;map&gt;.viswright.json</c>, which names the folder its
    /// last review was written to. Both are copied into the preview folder for the page to read, with the
    /// review's pictures. A decision made in the page comes back as a message and is written to the
    /// decisions file beside the map, which is the one Viswright reads on the next compile.
    /// </summary>
    public static class ViswrightReview
    {
        public const string DecisionsFile = "viswright.json";
        public const string ItemsFile = "viswright-items.json";
        public const string PictureFolder = "viswright-pictures";

        /// <summary>The decisions file Viswright keeps for <paramref name="sourceVmf"/>.</summary>
        public static string DecisionsPathFor(string sourceVmf) =>
            Path.Combine(Path.GetDirectoryName(sourceVmf)!, Path.GetFileNameWithoutExtension(sourceVmf) + ".viswright.json");

        /// <summary>
        /// Copies the decisions, the review's items and its pictures into <paramref name="previewFolder"/>, or
        /// clears them when the map has none. Returns whether there was a review to show.
        /// </summary>
        public static bool CopyInto(string previewFolder, string? decisionsPath)
        {
            string decisionsCopy = Path.Combine(previewFolder, DecisionsFile);
            string itemsCopy = Path.Combine(previewFolder, ItemsFile);
            string pictures = Path.Combine(previewFolder, PictureFolder);
            foreach (var stale in new[] { decisionsCopy, itemsCopy })
                if (File.Exists(stale))
                    File.Delete(stale);
            if (Directory.Exists(pictures))
                Directory.Delete(pictures, recursive: true);

            if (decisionsPath is null || !File.Exists(decisionsPath))
                return false;
            File.Copy(decisionsPath, decisionsCopy);

            try
            {
                string? review = JObject.Parse(File.ReadAllText(decisionsPath))["review"]?.Value<string>();
                if (review is null || !Directory.Exists(review))
                    return true;
                string items = Path.Combine(review, "items.json");
                if (File.Exists(items))
                    File.Copy(items, itemsCopy);
                string itemPictures = Path.Combine(review, "items");
                if (Directory.Exists(itemPictures))
                {
                    Directory.CreateDirectory(pictures);
                    foreach (var png in Directory.EnumerateFiles(itemPictures, "*.png"))
                        File.Copy(png, Path.Combine(pictures, Path.GetFileName(png)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Newtonsoft.Json.JsonException)
            {
                CompilePalLogger.LogLineDebug($"Viswright review not copied into the preview: {e.Message}");
            }
            return true;
        }

        /// <summary>
        /// Sets one item's decision in the decisions file. Only accept and deny are taken, and only for an
        /// item already in the file: the page can answer what Viswright asked, nothing more.
        /// </summary>
        public static bool Decide(string decisionsPath, string id, string decision)
        {
            if (decision is not ("accept" or "deny") || !File.Exists(decisionsPath))
                return false;
            var root = JObject.Parse(File.ReadAllText(decisionsPath));
            var item = (root["items"] as JArray)?.OfType<JObject>().FirstOrDefault(i => i["id"]?.Value<string>() == id);
            if (item is null)
                return false;
            item["decision"] = decision;
            ConfigurationManager.WriteFileAtomic(decisionsPath, root.ToString(Newtonsoft.Json.Formatting.Indented));
            return true;
        }
    }
}
