using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CompilePalX.Configuration
{
    public enum ToolsPlusPlusMode
    {
        /// <summary>Detect tools++ by inspecting the configured compiler binaries.</summary>
        Auto,
        /// <summary>Always offer tools++ parameters, regardless of detection.</summary>
        ForceOn,
        /// <summary>Never offer tools++ parameters.</summary>
        ForceOff,
    }

    public enum AppTheme
    {
        /// <summary>Follow the Windows light/dark setting.</summary>
        System,
        Light,
        Dark,
    }

    public class Settings : ICloneable, IEquatable<GameConfiguration>
    {
        /// <summary>Light/dark appearance. Applied at startup and whenever settings are saved.</summary>
        public AppTheme Theme { get; set; } = AppTheme.System;

        /// <summary>Options shown in the settings window combo box.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public IEnumerable<AppTheme> Themes => Enum.GetValues<AppTheme>();

        /// <summary>
        /// Opt-in usage reporting. Off by default and deliberately so.
        ///
        /// Upstream shipped this on for everyone, with no setting to refuse and two hardcoded
        /// destinations - one of which the code could not name the owner of. A fork cannot inherit
        /// that: nobody downloading this build agreed to send anything anywhere. See AnalyticsManager.
        /// </summary>
        public bool AnalyticsEnabled { get; set; } = false;

        /// <summary>
        /// Segment-compatible write key for usage reporting. Blank means there is nowhere to send to,
        /// so nothing is sent even when <see cref="AnalyticsEnabled"/> is on.
        /// </summary>
        public string AnalyticsWriteKey { get; set; } = "";

        /// <summary>
        /// Collector the events go to. Blank uses Segment's own endpoint; set it to a self-hosted
        /// RudderStack, Jitsu or any other service speaking the same batch API to keep the data
        /// entirely under your control.
        /// </summary>
        public string AnalyticsHost { get; set; } = "";

        /// <summary>
        /// Random per-install identifier, generated on first report. Not derived from the machine -
        /// clearing it here is all it takes to start over as a new install.
        /// </summary>
        public string? AnalyticsInstallId { get; set; } = null;

        public string ErrorSourceURL { get; set; } = "https://www.interlopers.net/includes/errorpage/errorChecker.txt";
        public int ErrorCacheExpirationDays { get; set; } = 7;
        public bool PlaySoundOnCompileCompletion { get; set; } = true;

        /// <summary>
        /// Comma-separated fallback list, same syntax as a XAML FontFamily attribute. Compile tool
        /// output (VBSP's lump report, etc.) is column-aligned with spaces, so this needs to stay a
        /// monospace family or those columns misalign again.
        /// </summary>
        public string OutputFontFamily { get; set; } = "Cascadia Mono, Cascadia Code, Consolas, Courier New";
        public double OutputFontSize { get; set; } = 13;

        /// <summary>
        /// Controls whether parameters exclusive to ficool2's Hammer++ compile tools are shown.
        /// Auto inspects the configured vbsp/vvis/vrad/bspzip binaries.
        /// </summary>
        public ToolsPlusPlusMode ToolsPlusPlusMode { get; set; } = ToolsPlusPlusMode.Auto;

        /// <summary>Options shown in the settings window combo box.</summary>
        [Newtonsoft.Json.JsonIgnore]
        public IEnumerable<ToolsPlusPlusMode> ToolsPlusPlusModes => Enum.GetValues<ToolsPlusPlusMode>();

        /// <summary>
        /// When a tools++ build of vbsp/vvis/vrad/bspzip is found next to the configured binary (usually
        /// in bin/win64 or bin/x64), run it instead. The Hammer++ tools are preferred because they are
        /// faster and support options the stock tools lack - notably bspzip++ for repacking.
        /// </summary>
        public bool PreferToolsPlusPlusBinaries { get; set; } = true;

        /// <summary>Delay before edits to a preset are flushed to disk, in milliseconds.</summary>
        public int AutosaveDelayMilliseconds { get; set; } = 750;

        // not directly editable by user, set in MainWindow.xaml.cs on shutdown
        public string? MapListHeight { get; set; } = null;

        // not directly editable by user, restores the preset selected when the app was last closed
        public string? LastPreset { get; set; } = null;

        public object Clone()
        {
            return MemberwiseClone();
        }

        public bool Equals(GameConfiguration? other)
        {
            throw new NotImplementedException();
        }
    }
}
