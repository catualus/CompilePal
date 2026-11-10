using System;
using CompilePalX.Compilers;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// Which step programs are tied to Compile Pal's lifetime. The game a GAME step launches must
    /// not be: closing Compile Pal would close the game the user is playing the map in.
    /// </summary>
    [Collection("Settings")]
    public class ChildProcessJobTests : IDisposable
    {
        private readonly GameConfiguration? original = GameConfigurationManager.GameConfiguration;

        public void Dispose() => GameConfigurationManager.GameConfiguration = original;

        [Fact]
        public void TheGameIsNotTiedToCompilePal()
        {
            GameConfigurationManager.GameConfiguration = new GameConfiguration { Name = "Garry's Mod", GameEXE = @"C:\Games\GarrysMod\hl2.exe" };

            Assert.False(ChildProcessJob.ShouldAdopt("$gameEXE$", @"C:\Games\GarrysMod\hl2.exe"));
            Assert.False(ChildProcessJob.ShouldAdopt("C:/Games/GarrysMod/hl2.exe", @"C:\Games\GarrysMod\hl2.exe"));
        }

        [Fact]
        public void CompilersAndPluginsAre()
        {
            GameConfigurationManager.GameConfiguration = new GameConfiguration { Name = "Garry's Mod", GameEXE = @"C:\Games\GarrysMod\hl2.exe" };

            Assert.True(ChildProcessJob.ShouldAdopt("$vvis$", @"C:\Games\GarrysMod\bin\vvis.exe"));
            Assert.True(ChildProcessJob.ShouldAdopt(@"Plugins\Viswright\viswright.exe", @"Plugins\Viswright\viswright.exe"));
        }
    }
}
