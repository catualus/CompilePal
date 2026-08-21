using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Markup;
using Xunit;

namespace CompilePalX.Tests
{
    /// <summary>
    /// While a compile runs, the controls that would change what is being compiled have to be locked.
    ///
    /// That used to be two mirrored lists of <c>SomeControl.IsEnabled = false</c> / <c>= true</c> in
    /// CompilingManager_OnStart and OnFinish - a control missing from either list stayed live for the
    /// whole compile, and nothing but care kept the pair in step. It is now a single bound property,
    /// MainWindow.IsNotCompiling.
    ///
    /// That swap has one sharp edge, which these tests exist for: in WPF, assigning a dependency
    /// property locally *removes* any binding on it. So a stray <c>ConfigDataGrid.IsEnabled = true</c>
    /// elsewhere in the window does not merely duplicate the binding, it permanently destroys it, and
    /// the control silently stops responding to compile state from that moment on.
    /// </summary>
    public class CompileStateLockTests
    {
        [WpfFact]
        public void AssigningIsEnabledDestroysABindingOnIt()
        {
            const string markup = """
                <StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                            Name="Root" Tag="True">
                  <Button Name="Bound" IsEnabled="{Binding Tag, ElementName=Root}"/>
                </StackPanel>
                """;

            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markup));
            var root = (StackPanel)XamlReader.Load(stream);
            var button = (Button)root.FindName("Bound");

            root.Tag = false;
            Assert.False(button.IsEnabled);

            // The assignment a well-meaning refactor leaves behind.
            button.IsEnabled = true;

            // The binding is now gone, not merely overridden: the source can say anything it likes and
            // the control no longer listens. This is why UpdateConfigGrid sets Visibility on the two
            // parameter grids but must never touch their IsEnabled.
            root.Tag = false;
            Assert.True(button.IsEnabled);
        }

        /// <summary>
        /// Locates the repository's CompilePalX source folder by walking up from the test binaries.
        /// The XAML is not copied to the output directory, so it has to be found rather than opened.
        /// </summary>
        private static string SourceDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "CompilePalX", "MainWindow.xaml");
                if (File.Exists(candidate))
                    return Path.Combine(dir.FullName, "CompilePalX");

                dir = dir.Parent;
            }

            throw new InvalidOperationException(
                $"Could not find CompilePalX/MainWindow.xaml above {AppContext.BaseDirectory}");
        }

        /// <summary>
        /// Controls that must not be usable mid-compile, and the property each binds IsEnabled to.
        ///
        /// Named controls only. The stepper's per-step parameter grids and their add/remove buttons live
        /// inside a DataTemplate, so they are instantiated once per step and have no unique name to
        /// assert against; <see cref="EveryStepEditorInTheSetupTemplateIsLocked"/> covers those instead.
        /// </summary>
        public static TheoryData<string, string> LockedControls() => new()
        {
            { "AddMapButton", "IsNotCompiling" },
            { "RemoveMapButton", "IsNotCompiling" },
            { "AddPresetButton", "IsNotCompiling" },
            { "FilterPresetButton", "IsNotCompiling" },
            { "PresetConfigListBox", "IsNotCompiling" },
            { "AddProcessesButton", "IsNotCompiling" },
            { "RemoveProcessesButton", "IsNotCompiling" },
            { "CompileProcessesListBox", "IsNotCompiling" },
            { "OrderGrid", "IsNotCompiling" },
            { "StepParameterGrid", "IsNotCompiling" },
            { "StepProgramGrid", "IsNotCompiling" },
        };

        /// <summary>
        /// The buttons that edit a step's parameters are repeated per step inside the SETUP template, so
        /// they cannot be found by name. Locking them still matters just as much - they add and remove
        /// entries from the preset a running compile is reading.
        /// </summary>
        [Fact]
        public void EveryStepEditorInTheSetupTemplateIsLocked()
        {
            string xaml = File.ReadAllText(Path.Combine(SourceDir(), "MainWindow.xaml"));

            foreach (var handler in new[] { "AddParameterButton_Click", "RemoveParameterButton_OnClickParameterButton_Click" })
            {
                var match = Regex.Match(xaml, @"<Button[^>]*?Click=""" + Regex.Escape(handler) + @"""[^>]*?>",
                    RegexOptions.Singleline);

                Assert.True(match.Success, $"no button wired to {handler} in MainWindow.xaml");
                Assert.Contains("IsEnabled=\"{Binding IsNotCompiling, ElementName=CompileWindow}\"", match.Value);
            }
        }

        [Theory]
        [MemberData(nameof(LockedControls))]
        public void EveryLockedControlBindsIsEnabled(string controlName, string property)
        {
            string xaml = File.ReadAllText(Path.Combine(SourceDir(), "MainWindow.xaml"));

            // The element's own tag, up to the first '>' that ends it.
            var match = Regex.Match(xaml, @"<[A-Za-z:.]+[^>]*?Name\s*=\s*""" + Regex.Escape(controlName) + @"""[^>]*?>",
                RegexOptions.Singleline);
            Assert.True(match.Success, $"{controlName} not found in MainWindow.xaml");

            Assert.Contains($"IsEnabled=\"{{Binding {property}, ElementName=CompileWindow}}\"", match.Value);
        }

        [Theory]
        [MemberData(nameof(LockedControls))]
        public void NoCodePathAssignsIsEnabledOnALockedControl(string controlName, string property)
        {
            _ = property;

            string code = File.ReadAllText(Path.Combine(SourceDir(), "MainWindow.xaml.cs"));

            // Reading IsEnabled is fine - the keyboard shortcuts check it before acting. Assigning it is
            // what silently unbinds the control, so only assignment is rejected. "==" is excluded so a
            // comparison never trips this.
            var assignment = new Regex($@"\b{Regex.Escape(controlName)}\.IsEnabled\s*=(?!=)");

            Assert.False(assignment.IsMatch(code),
                $"{controlName}.IsEnabled is assigned in MainWindow.xaml.cs, which removes its binding " +
                $"to {property} and stops it locking during a compile.");
        }
    }
}
