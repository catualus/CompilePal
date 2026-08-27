using System;
using System.IO;

namespace CompilePalX.Configuration
{
    /// <summary>
    /// Splitting a command a plugin declared into the program and its arguments.
    ///
    /// Its own class because it is worth testing and a button handler is not: a plugin folder under
    /// Program Files has a space in its path, so the naive split on the first space runs "C:\Program"
    /// and reports that the plugin is missing.
    /// </summary>
    internal static class PluginCommand
    {
        /// <summary>
        /// Splits at the quoted program path if there is one, and at the first space otherwise.
        ///
        /// Only the leading quote is honoured. Windows itself resolves an unquoted path with spaces
        /// by trying each prefix in turn, and reproducing that here would be guessing on a plugin's
        /// behalf about a path it wrote itself - a plugin that needs a space in its path can quote it.
        /// </summary>
        public static (string FileName, string Arguments) Split(string command)
        {
            command = (command ?? "").Trim();

            if (command.StartsWith('"'))
            {
                int closing = command.IndexOf('"', 1);

                if (closing > 0)
                    return (command[1..closing], command[(closing + 1)..].TrimStart());
            }

            int space = command.IndexOf(' ');

            return space < 0 ? (command, "") : (command[..space], command[(space + 1)..]);
        }

        /// <summary>
        /// Finds the program a plugin named, whatever the working directory happens to be.
        ///
        /// Plugin paths are written relative to the Compile Pal folder - that is what the format
        /// documents and what every plugin does - and a relative path resolves against the working
        /// directory, which is only the Compile Pal folder when the application was started from it.
        /// Launched from a shortcut with a different start-in, or from a terminal, or by another
        /// program, the same path points at nothing and the plugin quietly does not work.
        ///
        /// Returns null when there is nothing there, so the caller can say so rather than starting a
        /// process that cannot exist.
        /// </summary>
        public static string? Resolve(string fileName)
        {
            if (fileName.Length == 0)
                return null;

            if (File.Exists(fileName))
                return Path.GetFullPath(fileName);

            if (Path.IsPathRooted(fileName))
                return null;

            string besideTheApplication = Path.Combine(AppContext.BaseDirectory, fileName);

            return File.Exists(besideTheApplication) ? besideTheApplication : null;
        }
    }
}
