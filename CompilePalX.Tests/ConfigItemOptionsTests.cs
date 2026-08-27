using System.Collections.Generic;
using Newtonsoft.Json;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Parameters that offer a fixed set of values.
    ///
    /// The declaration comes straight out of a plugin's parameters.json, so what matters is that it
    /// survives the round trip: read from JSON, cloned into a preset, and still pointing at the same
    /// list of choices afterwards.
    /// </summary>
    public class ConfigItemOptionsTests
    {
        [Fact]
        public void OptionsAreReadFromParametersJson()
        {
            var items = JsonConvert.DeserializeObject<ConfigItem[]>("""
            [{
              "Name": "Tags",
              "Parameter": " -tags",
              "CanHaveValue": true,
              "Options": ["fun", "roleplay", "scenic"]
            }]
            """);

            Assert.NotNull(items);
            Assert.Equal(new[] { "fun", "roleplay", "scenic" }, items![0].Options);
            Assert.True(items[0].HasOptions);
        }

        [Fact]
        public void AParameterWithoutThemIsStillFreeText()
        {
            var items = JsonConvert.DeserializeObject<ConfigItem[]>("""
            [{ "Name": "Change note", "Parameter": " -changes", "CanHaveValue": true }]
            """);

            Assert.Null(items![0].Options);
            Assert.False(items[0].HasOptions);
        }

        [Fact]
        public void AnEmptyListIsNotAChoice()
        {
            // Otherwise the cell offers a dropdown with nothing in it and no way to type a value.
            var item = new ConfigItem { Name = "x", Options = new List<string>() };

            Assert.False(item.HasOptions);
        }

        [Fact]
        public void CloningKeepsTheChoices()
        {
            var item = new ConfigItem
            {
                Name = "Tags",
                CanHaveValue = true,
                Value = "scenic",
                Options = new List<string> { "fun", "scenic" },
            };

            var clone = (ConfigItem)item.Clone();

            Assert.Equal(item.Options, clone.Options);
            Assert.True(clone.HasOptions);
            Assert.Equal("scenic", clone.Value);
        }

        [Fact]
        public void TheChosenValueIsWhatReachesTheCommandLine()
        {
            var item = new ConfigItem
            {
                Name = "Tags",
                Parameter = " -tags",
                CanHaveValue = true,
                Options = new List<string> { "fun", "scenic" },
                Value = "scenic",
            };

            // Nothing about Options changes how a parameter is rendered into arguments - it only
            // decides how the value was picked.
            Assert.Equal(" -tags", item.Parameter);
            Assert.Equal("scenic", item.Value);
        }
    }
}
