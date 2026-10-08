using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CompilePalX.Compilers.BSPPack;
using ValveKeyValue;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// A Linux user's PACK step died on "Attempted to finalize object while in state
    /// InObjectBetweenKeyAndValue at line 46, column 3" - line 46 being the brace that closes
    /// SearchPaths in Garry's Mod's gameinfo.txt. Strict KeyValues gives up on a search path written
    /// without quotes around a space; the game does not. GameInfoSearchPaths is the fallback that reads
    /// those lines the way they were meant.
    /// </summary>
    public class GameInfoSearchPathsTests
    {
        // Garry's Mod's layout: tabs, comments, a wildcard, VPKs and |macros|.
        private const string GarrysMod = """
            "GameInfo"
            {
            	game	"Garry's Mod"
            	"developer_url"		"http://www.garrysmod.com/"

            	FileSystem
            	{
            		SteamAppId				4000

            		SearchPaths
            		{
            			// Game content mounting is controlled by cfg/mount.cfg, and not here!
            			game+mod			garrysmod/addons/*
            			game+mod			garrysmod/garrysmod.vpk
            			game				|all_source_engine_paths|sourceengine/hl2_textures.vpk
            			mod+mod_write+default_write_path		|gameinfo_path|.
            			game+game_write		garrysmod
            			gamebin				garrysmod/bin
            			game				|all_source_engine_paths|sourceengine
            			game+download		garrysmod/download
            		}
            	}
            }
            """;

        private static List<string> Read(string text, out List<GameInfoSearchPaths.Problem> problems)
        {
            problems = [];
            return GameInfoSearchPaths.Read(text, problems);
        }

        private static List<string> ReadStrictly(string text)
        {
            var doc = KVSerializer.Create(KVSerializationFormat.KeyValues1Text)
                .Deserialize(new MemoryStream(Encoding.UTF8.GetBytes(text)));
            return ((IEnumerable<KVObject>)doc["FileSystem"]["SearchPaths"])
                .Select(o => o.Value.ToString()!)
                .ToList();
        }

        [Fact]
        public void AValidFileReadsExactlyAsKeyValuesReadsIt()
        {
            var paths = Read(GarrysMod, out var problems);

            Assert.Equal(ReadStrictly(GarrysMod), paths);
            Assert.Empty(problems);
        }

        [Fact]
        public void AnUnquotedPathWithASpaceIsReadWholeAndReported()
        {
            string text = GarrysMod.Replace("garrysmod/addons/*", "garrysmod/my addons/*");

            // The failure being worked around, so this test says why the reader exists.
            var strict = Record.Exception(() => ReadStrictly(text));
            Assert.Contains("InObjectBetweenKeyAndValue", strict?.Message ?? "");

            var paths = Read(text, out var problems);

            Assert.Equal("garrysmod/my addons/*", paths[0]);
            Assert.Equal("garrysmod/download", paths[^1]);
            Assert.Equal(8, paths.Count);

            var problem = Assert.Single(problems);
            Assert.Equal(13, problem.Line);
            Assert.Contains("my addons", problem.Text);
        }

        [Fact]
        public void AnEntryWithNoPathIsSkippedAndReported()
        {
            string text = GarrysMod.Replace("game+download\t\tgarrysmod/download", "game+download");

            var paths = Read(text, out var problems);

            Assert.DoesNotContain("game+download", paths);
            Assert.Equal("|all_source_engine_paths|sourceengine", paths[^1]);
            Assert.Equal("has no path", Assert.Single(problems).Reason);
        }

        [Fact]
        public void AQuotedPathWithASpaceIsFine()
        {
            string text = GarrysMod.Replace("garrysmod/addons/*", "\"garrysmod/my addons/*\"");

            var paths = Read(text, out var problems);

            Assert.Equal("garrysmod/my addons/*", paths[0]);
            Assert.Empty(problems);
            Assert.Equal(ReadStrictly(text), paths);
        }

        [Fact]
        public void TrailingCommentsAndPlatformConditionsAreNotPartOfThePath()
        {
            string text = GarrysMod
                .Replace("garrysmod/bin", "garrysmod/bin [$WIN32]")
                .Replace("garrysmod/download", "garrysmod/download // where downloads go");

            var paths = Read(text, out var problems);

            Assert.Contains("garrysmod/bin", paths);
            Assert.Equal("garrysmod/download", paths[^1]);
            Assert.Empty(problems);
        }

        [Fact]
        public void BracesOnTheSameLineAsTheirBlockNameAreFollowed()
        {
            string text = """
                "GameInfo" {
                    FileSystem {
                        SearchPaths {
                            game  hl2
                            game  "my mod"
                        }
                    }
                }
                """;

            Assert.Equal(["hl2", "my mod"], Read(text, out var problems));
            Assert.Empty(problems);
        }

        [Fact]
        public void OnlyTheFileSystemSearchPathsBlockIsRead()
        {
            string text = """
                "GameInfo"
                {
                    SearchPaths
                    {
                        game  not-this-one
                    }
                    FileSystem
                    {
                        SearchPaths
                        {
                            game  this-one
                        }
                    }
                }
                """;

            Assert.Equal(["this-one"], Read(text, out _));
        }
    }
}
