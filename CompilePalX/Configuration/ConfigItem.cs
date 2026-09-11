using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using CompilePalX.Annotations;

namespace CompilePalX
{
    public class ConfigItem : ICloneable, INotifyPropertyChanged
    {
        private string name;
        private string parameter;
        private string description;
        private string value;
        private bool valueIsFile;
        private bool valueIsFolder;
        private List<string>? options;
        private string value2;
        private bool value2IsFile;
        private bool value2IsFolder;
        private bool readOutput;
        private bool waitForExit;
        private bool canHaveValue;
        private string warning;
        private bool canBeUsedMoreThanOnce;

        public string Name { get => name; set => Set(ref name, value); }
        public string Parameter { get => parameter; set => Set(ref parameter, value); }
        public string Description { get => description; set => Set(ref description, value); }

        public string Value { get => value; set => Set(ref this.value, value); }
        public bool ValueIsFile { get => valueIsFile; set => Set(ref valueIsFile, value); }
        public bool ValueIsFolder { get => valueIsFolder; set => Set(ref valueIsFolder, value); }

        /// <summary>
        /// The values this parameter accepts, when there is a fixed set of them.
        ///
        /// A parameter whose value is one of a handful of words - a tag, a quality level, a number of
        /// minutes - is a text box today, which means every one of those words has to be remembered
        /// and typed correctly, and a typo is only discovered by the compiler rejecting it. Declaring
        /// them here turns the cell into a list to pick from.
        ///
        /// Absent for every parameter that does not have a fixed set, which is most of them: a path,
        /// a change note and a map name are all free text and stay free text.
        /// </summary>
        public List<string>? Options { get => options; set => Set(ref options, value); }

        /// <summary>Whether this parameter offers a list rather than a text box.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool HasOptions => Options is { Count: > 0 };
        public string Value2 { get => value2; set => Set(ref value2, value); }
        public bool Value2IsFile { get => value2IsFile; set => Set(ref value2IsFile, value); }
        public bool Value2IsFolder { get => value2IsFolder; set => Set(ref value2IsFolder, value); }

        public bool ReadOutput { get => readOutput; set => Set(ref readOutput, value); }

        public bool WaitForExit { get => waitForExit; set => Set(ref waitForExit, value); }

        public bool CanHaveValue { get => canHaveValue; set => Set(ref canHaveValue, value); }

        public string Warning { get => warning; set => Set(ref warning, value); }

        public bool CanBeUsedMoreThanOnce { get => canBeUsedMoreThanOnce; set => Set(ref canBeUsedMoreThanOnce, value); }
        public HashSet<int>? IncompatibleGames { get; set; }
        public HashSet<int>? CompatibleGames { get; set; }

        /// <summary>
        /// Parameter is only offered by ficool2's Hammer++ compile tools (tools++), not by the stock
        /// Source SDK compilers.
        ///
        /// Only consulted when the compiler in use cannot be asked what it accepts - which is to say,
        /// for the stock tools, which print no option table. A tools++ build lists what it takes and
        /// that listing is believed over this flag in either direction.
        /// </summary>
        public bool RequiresToolsPlusPlus { get; set; }

        /// <summary>
        /// The compiler accepts this even though its <c>-help</c> does not list it, or Compile Pal
        /// consumes it itself and the compiler never sees it.
        ///
        /// The default rule is that a compiler which describes itself is believed, and anything it
        /// does not list is not offered. Three things break that rule and need saying so:
        /// <c>-StaticPropBounce</c> and vvis's <c>-tmpin</c>/<c>-tmpout</c> are accepted silently by
        /// tools++ but absent from its table, and <c>-normal_priority</c> is Compile Pal's own switch,
        /// stripped from the command line before the compiler runs. The last of these used to be
        /// marked incompatible with tools++ on the strength of the compiler rejecting it - which it
        /// does, and which is irrelevant, because it is never handed the flag.
        /// </summary>
        public bool NotInToolHelp { get; set; }

        /// <summary>
        /// This parameter was not in the shipped list at all; it was added because the compiler
        /// reported it. Named after its flag, described in the compiler's own words.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public bool FromToolHelp { get; set; }

        /// <summary>
        /// What the compiler uses when this is not given, as printed in its help. Null when the
        /// compiler in use has not said, or the option is a plain switch.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public string? ToolDefault { get; set; }

        /// <summary>The flag alone - " -bounce" is "-bounce", "+nav_max_view_distance 1" is "+nav_max_view_distance".</summary>
        [Newtonsoft.Json.JsonIgnore]
        public string Flag => (Parameter ?? "").Trim().Split(' ', 2)[0];

        /// <summary>
        /// Why <see cref="IsCompatible"/> is false, in words the log can print. Null when it is true.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public string? IncompatibilityReason => Availability().Reason;

        public bool IsCompatible => Availability().Offered;

        /// <summary>
        /// Whether the compiler that will run accepts this parameter under the current game.
        ///
        /// The order of the checks is the design:
        ///
        ///   1. If the compiler describes itself, that description is the truth. Listed means offered
        ///      whatever the game lists below say, because those lists were written for the stock
        ///      binaries and a tools++ build ships every option for every game. Not listed means not
        ///      offered, unless the parameter file says the omission is known (NotInToolHelp).
        ///   2. If it does not - the stock tools, or nothing configured - the shipped file is all
        ///      there is: tools++-only options are hidden, and the game lists apply.
        ///
        /// Only flags are checked against the compiler. A parameter with no flag of its own (the
        /// free-text "Command Line Argument", CUSTOM's program path) is passed through on the game
        /// rules alone, since there is nothing to look up.
        /// </summary>
        private (bool Offered, string? Reason) Availability()
        {
            var mode = ConfigurationManager.Settings.ToolsPlusPlusMode;
            var help = ToolsPlusPlusDetector.HelpFor(OwningProcess);

            if (help is not null && !NotInToolHelp && Flag.StartsWith('-'))
            {
                if (help.Has(Flag))
                    return (true, null);

                return (false, $"not accepted by {help.Label}");
            }

            if (help is null && RequiresToolsPlusPlus)
            {
                bool toolsPlusPlus = mode switch
                {
                    Configuration.ToolsPlusPlusMode.ForceOn => true,
                    Configuration.ToolsPlusPlusMode.ForceOff => false,
                    _ => ToolsPlusPlusDetector.IsEnabledFor(OwningProcess),
                };

                if (!toolsPlusPlus)
                    return (false, "requires the Hammer++ compile tools");
            }

            // current game configuration has no SteamAppID
            var game = GameConfigurationManager.GameConfiguration;
            if (game is null || game.SteamAppID is null)
                return (true, null);

            int currentAppID = (int)game.SteamAppID;
            string gameName = game.Name ?? "this game";

            // supported game ID list should take precedence. If defined, check that current GameConfiguration SteamID is in whitelist
            if (CompatibleGames != null)
                return CompatibleGames.Contains(currentAppID) ? (true, null) : (false, $"not supported by {gameName}");

            // If defined, check that current GameConfiguration SteamID is not in blacklist
            if (IncompatibleGames != null)
                return !IncompatibleGames.Contains(currentAppID) ? (true, null) : (false, $"not supported by {gameName}");

            // parameter does not define which games are supported
            return (true, null);
        }

        /// <summary>
        /// Name of the CompileProcess this parameter was loaded for. Set by ConfigurationManager after
        /// deserialization; used to resolve which compiler binary to test for tools++ support.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public string? OwningProcess { get; set; }

        /// <summary>
        /// A parameter built from one line of a compiler's help, for an option the shipped list does
        /// not describe. Its name is its flag, so a preset saved with it can be read back by any
        /// build that lists the same flag.
        /// </summary>
        public static ConfigItem FromToolOption(string processName, ToolOption option)
        {
            string description = option.Description;
            if (option.TakesValue)
                description += (description.Length > 0 ? " " : "") + $"Default: {option.Default}.";

            return new ConfigItem
            {
                Name = option.Flag,
                Parameter = " " + option.Flag,
                Description = description,
                Warning = "",
                CanHaveValue = option.TakesValue,
                ToolDefault = option.TakesValue ? option.Default : null,
                FromToolHelp = true,
                // Only ever seen in a tools++ listing, so when there is no listing to check against -
                // a stock compiler, or ForceOff - this must not be handed over on the game rules alone.
                RequiresToolsPlusPlus = true,
                OwningProcess = processName,
            };
        }

        /// <summary>
        /// A parameter for a flag a preset names that no current list carries. Null unless the name
        /// is a flag, since only parameters discovered from a compiler are saved under one.
        /// </summary>
        public static ConfigItem? FromPresetFlag(string processName, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(name) || !name.StartsWith('-') || name.Contains(' '))
                return null;

            return new ConfigItem
            {
                Name = name,
                Parameter = " " + name,
                Description = "Reported by a compiler that is not the one configured now.",
                Warning = "",
                CanHaveValue = !string.IsNullOrEmpty(value),
                FromToolHelp = true,
                RequiresToolsPlusPlus = true,
                OwningProcess = processName,
            };
        }

        /// <summary>
        /// Copies every field by hand, so anything added to this class has to be added here too or it
        /// is silently dropped the moment an item is cloned - which is on the path every preset takes,
        /// since a preset stores only a name and a value and is rehydrated from the parameter list.
        /// Both tools++ compatibility flags were missed here first time round, and the symptom was a
        /// parameter being filtered out for the wrong reason with nothing to suggest why.
        /// </summary>
        public object Clone()
        {
            // Options is copied by reference on purpose: it is the parameter's declaration, read from
            // the plugin's parameters.json and never edited, so every clone of a parameter shares the
            // same list of choices. The Value each clone holds is its own.
            return new ConfigItem() {Options=Options,Name=Name,Parameter=Parameter,Description = Description,Value=Value, Value2 = Value2, CanHaveValue = CanHaveValue,Warning = Warning,CanBeUsedMoreThanOnce = CanBeUsedMoreThanOnce, ReadOutput = ReadOutput, ValueIsFile = ValueIsFile, Value2IsFile = Value2IsFile, ValueIsFolder = ValueIsFolder, Value2IsFolder = Value2IsFolder, WaitForExit = WaitForExit, CompatibleGames = CompatibleGames, IncompatibleGames = IncompatibleGames, RequiresToolsPlusPlus = RequiresToolsPlusPlus, NotInToolHelp = NotInToolHelp, FromToolHelp = FromToolHelp, ToolDefault = ToolDefault, OwningProcess = OwningProcess};
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void Set<T>(ref T field, T newValue, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, newValue))
                return;

            field = newValue;
            OnPropertyChanged(propertyName);
        }

        [NotifyPropertyChangedInvocator]
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
