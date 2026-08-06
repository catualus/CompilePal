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
        /// Source SDK compilers. Hidden unless tools++ is detected for the owning process.
        /// </summary>
        public bool RequiresToolsPlusPlus { get; set; }

        /// <summary>
        /// The mirror of <see cref="RequiresToolsPlusPlus"/>: a stock-compiler option that tools++
        /// dropped, so it has to be hidden when tools++ *is* in use. Verified by asking the binary -
        /// ficool2's VRAD answers "Unrecognized option '-normal_priority'".
        /// </summary>
        public bool IncompatibleWithToolsPlusPlus { get; set; }

        /// <summary>
        /// The game whitelist/blacklist on this parameter describes the *stock* compilers only -
        /// tools++ accepts it whatever game is configured, so the game gate should not apply when
        /// tools++ is in use.
        ///
        /// Several arguments are like this because the stock lists were written when the option only
        /// shipped in CS:GO's branch: <c>-StaticPropSampleScale</c>, <c>-StaticPropBounce</c>,
        /// <c>-aoscale</c> and <c>-dumppropmaps</c> are all marked for other games and all answer
        /// "accepted" when handed to ficool2's VRAD. Without this they get correctly filtered out by
        /// the game check and silently lost on a Garry's Mod tools++ compile that had been using them.
        /// </summary>
        public bool SupportedByToolsPlusPlus { get; set; }

        public bool IsCompatible
        {
            get
            {
                bool toolsPlusPlus = ToolsPlusPlusDetector.IsEnabledFor(OwningProcess);

                // parameter only exists in tools++, hide it when the configured compiler is stock
                if (RequiresToolsPlusPlus && !toolsPlusPlus)
                    return false;

                // and the reverse: stock-only options the tools++ rewrite no longer accepts
                if (IncompatibleWithToolsPlusPlus && toolsPlusPlus)
                    return false;

                // the game lists below describe the stock binaries; tools++ ships this one regardless
                if (toolsPlusPlus && SupportedByToolsPlusPlus)
                    return true;

                // current game configuration has no SteamAppID
                if (GameConfigurationManager.GameConfiguration != null && GameConfigurationManager.GameConfiguration.SteamAppID == null)
                    return true;

                int currentAppID = (int)GameConfigurationManager.GameConfiguration!.SteamAppID!;

                // supported game ID list should take precedence. If defined, check that current GameConfiguration SteamID is in whitelist
                if (CompatibleGames != null)
                    return CompatibleGames.Contains(currentAppID);

                // If defined, check that current GameConfiguration SteamID is not in blacklist
                if (IncompatibleGames != null)
                    return !IncompatibleGames.Contains(currentAppID);

                // parameter does not define which games are supported
                return true;
            }
        }

        /// <summary>
        /// Name of the CompileProcess this parameter was loaded for. Set by ConfigurationManager after
        /// deserialization; used to resolve which compiler binary to test for tools++ support.
        /// </summary>
        [Newtonsoft.Json.JsonIgnore]
        public string? OwningProcess { get; set; }

        /// <summary>
        /// Copies every field by hand, so anything added to this class has to be added here too or it
        /// is silently dropped the moment an item is cloned - which is on the path every preset takes,
        /// since a preset stores only a name and a value and is rehydrated from the parameter list.
        /// Both tools++ compatibility flags were missed here first time round, and the symptom was a
        /// parameter being filtered out for the wrong reason with nothing to suggest why.
        /// </summary>
        public object Clone()
        {
            return new ConfigItem() {Name=Name,Parameter=Parameter,Description = Description,Value=Value, Value2 = Value2, CanHaveValue = CanHaveValue,Warning = Warning,CanBeUsedMoreThanOnce = CanBeUsedMoreThanOnce, ReadOutput = ReadOutput, ValueIsFile = ValueIsFile, Value2IsFile = Value2IsFile, ValueIsFolder = ValueIsFolder, Value2IsFolder = Value2IsFolder, WaitForExit = WaitForExit, CompatibleGames = CompatibleGames, IncompatibleGames = IncompatibleGames, RequiresToolsPlusPlus = RequiresToolsPlusPlus, IncompatibleWithToolsPlusPlus = IncompatibleWithToolsPlusPlus, SupportedByToolsPlusPlus = SupportedByToolsPlusPlus, OwningProcess = OwningProcess};
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
