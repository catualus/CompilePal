using CompilePalX.Configuration;
using Xunit;

namespace CompilePalX.Tests
{
    public class PluginCommandTests
    {
        [Fact]
        public void SplitsAtTheFirstSpace()
        {
            var (fileName, arguments) = PluginCommand.Split(@"Plugins\Shipwright\shipwright-ui.exe -vmf C:\maps\gm_test.vmf");

            Assert.Equal(@"Plugins\Shipwright\shipwright-ui.exe", fileName);
            Assert.Equal(@"-vmf C:\maps\gm_test.vmf", arguments);
        }

        [Fact]
        public void AQuotedPathWithSpacesSurvives()
        {
            var (fileName, arguments) = PluginCommand.Split(
                "\"C:\\Program Files\\Compile Pal\\Plugins\\Shipwright\\shipwright-ui.exe\" -vmf gm_test.vmf");

            Assert.Equal(@"C:\Program Files\Compile Pal\Plugins\Shipwright\shipwright-ui.exe", fileName);
            Assert.Equal("-vmf gm_test.vmf", arguments);
        }

        [Fact]
        public void AProgramWithNoArgumentsHasNone()
        {
            var (fileName, arguments) = PluginCommand.Split("shipwright-ui.exe");

            Assert.Equal("shipwright-ui.exe", fileName);
            Assert.Equal("", arguments);
        }

        [Fact]
        public void SurroundingWhitespaceIsNotPartOfThePath()
        {
            var (fileName, _) = PluginCommand.Split("   shipwright-ui.exe -vmf x  ");

            Assert.Equal("shipwright-ui.exe", fileName);
        }

        [Fact]
        public void AnUnclosedQuoteFallsBackRatherThanThrowing()
        {
            var (fileName, arguments) = PluginCommand.Split("\"C:\\weird\\path.exe -vmf x");

            Assert.Equal("\"C:\\weird\\path.exe", fileName);
            Assert.Equal("-vmf x", arguments);
        }

        [Fact]
        public void NothingIsNotAProgram()
        {
            var (fileName, arguments) = PluginCommand.Split("");

            Assert.Equal("", fileName);
            Assert.Equal("", arguments);
        }
    }
}
