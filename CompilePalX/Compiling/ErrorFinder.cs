using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using CompilePalX.Compiling;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace CompilePalX
{
    static partial class ErrorFinder
    {
        private static List<Error> errorList = [];

        [GeneratedRegex("<h4>(.*?)</h4>")]
        private static partial Regex ErrorRegex();
        private static Regex errorDescriptionPattern = ErrorRegex();

        private static string errorStyle = Path.Combine("./Compiling", "errorstyle.html");
        private static string errorCache = Path.Combine("./Compiling", "errors.txt");

        // Offline fallbacks, used when the remote source is unreachable and no cache exists.
        // errors.default.txt is a copy of the upstream interlopers catalogue in its own format;
        // errors.supplement.json adds the messages that catalogue predates. See LoadOfflineErrorData.
        private static string bundledErrors = Path.Combine("./Compiling", "errors.default.txt");
        private static string supplementErrors = Path.Combine("./Compiling", "errors.supplement.json");
        public static void Init(bool refresh = false)
        {
            Thread t = new Thread(() => AsyncInit(ConfigurationManager.Settings.ErrorSourceURL, ConfigurationManager.Settings.ErrorCacheExpirationDays, refresh));
            t.Start();
        }

        static async void AsyncInit(string errorURL, int errorCacheExpirationDays, bool refresh)
        {
            try
            {
                if (!refresh && (File.Exists(errorCache) && (DateTime.Now.Subtract(File.GetLastWriteTime(errorCache)).TotalDays < errorCacheExpirationDays)))
                {
                    LoadJSONErrorData(File.ReadAllText(errorCache));
                    return;
                }

                try
                {
                    var c = new HttpClient();
                    c.DefaultRequestHeaders.ExpectContinue = true;
                    var httpResult = await c.GetAsync(errorURL);
                    httpResult.EnsureSuccessStatusCode();
                    string result = await httpResult.Content.ReadAsStringAsync();

                    httpResult.Headers.TryGetValues("Content-Type", out var contentType);
                    if (contentType != null && contentType.First() == "application/json")
                    {
                        LoadJSONErrorData(result);
                    } else
                    {
                        LoadTextErrorData(result);
                    }

                    await File.WriteAllTextAsync(errorCache, JsonConvert.SerializeObject(errorList, new RegexConverter()));
                }
                catch (Exception e)
                {
                    // The error-data source is a best-effort convenience fetch, and failing over to a
                    // local cache (or just running with no known-error lookups) is a fully handled,
                    // routine outcome - the source going offline/unreachable is not a CompilePal bug,
                    // so it stays out of the visible compile output and only goes to the debug log.
                    CompilePalLogger.LogLineDebug($"Failed to fetch error data from {errorURL}: {e.Message}");
                    if (File.Exists((errorCache)))
                    {
                        CompilePalLogger.LogLineDebug("Loading error data from cache");
                        LoadJSONErrorData(await File.ReadAllTextAsync(errorCache));
                    }
                    else
                    {
                        // Without this the app silently runs with an EMPTY error list: nothing in the
                        // compile output is recognised, explained or navigable. The remote source has
                        // been returning 403 for every request, so relying on it alone means the
                        // feature is simply absent on a fresh install.
                        CompilePalLogger.LogLineDebug($"Error cache not found: {errorCache}, falling back to bundled catalogue");
                        LoadOfflineErrorData();
                    }
                }
            }
            catch (Exception x)
            {
                //nonvital part, record but dont quit
                ExceptionHandler.LogException(x, false);
            }
        }

        static void LoadJSONErrorData(string input)
        {
            var errors = JsonConvert.DeserializeObject<List<Error>>(input, new RegexConverter()) ?? throw new Exception("Failed to deserialize errors");
            for (var i = 0; i < errors.Count; i++)
            {
                errors[i].ID = i;
            }
            errorList = errors;
        }

        /// <summary>
        /// Shape of an entry in errors.default.json. Deliberately not the same as <see cref="Error"/>:
        /// the bundled file stores the explanation as plain body HTML so it stays readable and
        /// editable, and the page style is applied here rather than being baked into the data.
        /// </summary>
        private class BundledError
        {
            public string Pattern { get; set; } = "";
            public int Severity { get; set; } = 3;
            public string Title { get; set; } = "";
            public string Html { get; set; } = "";
        }

        /// <summary>
        /// Loads the offline catalogue: the upstream interlopers data first, then the supplement.
        ///
        /// Order matters. <see cref="GetError"/> returns the first pattern that matches, so the
        /// upstream entries win wherever both describe the same message, and the supplement only
        /// answers for messages upstream never covered.
        /// </summary>
        static void LoadOfflineErrorData()
        {
            int upstream = 0;

            if (File.Exists(bundledErrors))
            {
                try
                {
                    LoadTextErrorData(File.ReadAllText(bundledErrors));
                    upstream = errorList.Count;
                }
                catch (Exception e)
                {
                    CompilePalLogger.LogLineDebug($"Failed to parse {bundledErrors}: {e.Message}");
                }
            }
            else
            {
                CompilePalLogger.LogLineDebug($"Bundled catalogue not found: {bundledErrors}");
            }

            int added = LoadSupplementErrorData();

            if (upstream + added > 0)
                CompilePalLogger.LogLineDebug($"Loaded {upstream} upstream + {added} supplementary error definitions");
        }

        static int LoadSupplementErrorData()
        {
            try
            {
                if (!File.Exists(supplementErrors))
                    return 0;

                var entries = JsonConvert.DeserializeObject<List<BundledError>>(File.ReadAllText(supplementErrors));
                if (entries is null)
                    return 0;

                string style = File.Exists(errorStyle) ? File.ReadAllText(errorStyle) : "%content%";
                int added = 0;

                foreach (var entry in entries)
                {
                    try
                    {
                        errorList.Add(new Error
                        {
                            RegexTrigger = new Regex(entry.Pattern, RegexOptions.IgnoreCase),
                            Severity = entry.Severity,
                            ShortDescription = entry.Title,
                            Message = style.Replace("%content%", entry.Html),
                            ID = errorList.Count,
                        });
                        added++;
                    }
                    catch (ArgumentException ex)
                    {
                        // One malformed pattern must not cost us the rest of the catalogue.
                        CompilePalLogger.LogLineDebug($"Skipping supplementary error '{entry.Title}': {ex.Message}");
                    }
                }

                return added;
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Failed to load {supplementErrors}: {e.Message}");
                return 0;
            }
        }

        static void LoadTextErrorData(string input)
        {
            string style = File.ReadAllText(errorStyle);

            var lines = input.Split(["\r\n", "\n"], StringSplitOptions.None);

            int count = int.Parse(lines[0]);

            int id = 0;
            for (int i = 1; i < (count * 2) + 1; i++)
            {
                Error error = new Error();

                // Split into exactly two parts: the pattern itself may legitimately contain '|'
                // as regex alternation, and a plain Split would silently truncate it at the
                // first one - matching far less than the entry intended.
                var data = lines[i].Split('|', 2);

                error.Severity = int.Parse(data[0]);
                error.RegexTrigger = new Regex(data[1]);
                i++;

                var shortDesc = errorDescriptionPattern.Match(lines[i]);
                error.ShortDescription = shortDesc.Success ? shortDesc.Groups[1].Value : "unknown error";

                error.Message = style.Replace("%content%", lines[i]);


                error.ID = id;
                errorList.Add(error);
                id++;
            }
        }

        public static Error? GetError(string line)
        {
            foreach (var error in errorList)
            {
                if (error.RegexTrigger.IsMatch(line))
                {
	                var err = error.Clone() as Error;
					// remove all control chars
	                err.ShortDescription = new string(line.Where(c => !char.IsControl(c)).ToArray());;
                    return err;
                }
            }
            return null;
        }

        public static void ShowErrorDialog(Error error)
        {
            ErrorWindow w = new ErrorWindow(error);
            w.ShowDialog();
        }
    }

    public class Error : ICloneable
    {
        public Regex RegexTrigger;
        public string Message;
        public string ShortDescription;
        public int Severity;

        [JsonIgnore]
        public int ID;

        public Error() { }

        public Error(string message, string shortDescription, ErrorSeverity severity, int id = -1)
        {
            Message = message;
            ShortDescription = shortDescription;
            Severity = (int) severity;
            ID = id;
        }
        public Error(string message, ErrorSeverity severity, int id = -1)
        {
            Message = message;
            ShortDescription = message;
            Severity = (int) severity;
            ID = id;
        }

        public override bool Equals(object obj)
        {
            if (obj is not Error) {
                return false;
            }
            return ((Error)obj).ID == ID;
        }

        public override int GetHashCode()
        {
            return ID;//ID is unique between errors
        }

        public object Clone()
        {
	        return MemberwiseClone();
        }

        [JsonIgnore]
        public Brush ErrorColor => GetSeverityBrush(Severity);

        public static Brush GetSeverityBrush(int severity)
        {
            return severity switch
            {
                2 => (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Severity2"),
                3 => (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Severity3"),
                4 => (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Severity4"),
                5 => (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Severity5"),
                _ => (Brush)Application.Current.TryFindResource("CompilePal.Brushes.Severity1"),
            };
        }

        public string SeverityText
        {
            get
            {
                return Severity switch
                {
                    2 => "Caution",
                    3 => "Warning",
                    4 => "Error",
                    5 => "Fatal Error",
                    _ => "Info",
                };
            }
        }
    }

    public enum ErrorSeverity {
        Info = 1,
        Caution = 2,
        Warning = 3,
        Error = 4,
        FatalError = 5,
    }
}
