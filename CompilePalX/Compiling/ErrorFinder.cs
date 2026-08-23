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
        /// <summary>
        /// Replaced wholesale on every load, never added to in place.
        ///
        /// <see cref="GetError"/> runs on the compile thread for every line of compiler output, while a
        /// refresh may be assembling a new catalogue on its own thread. Adding to the live list
        /// mid-enumeration is a "collection was modified" exception thrown out of the middle of a
        /// compile - which is why loads now build a local list and publish it in one assignment.
        /// </summary>
        private static volatile List<Error> errorList = [];

        [GeneratedRegex("<h4>(.*?)</h4>")]
        private static partial Regex ErrorRegex();
        private static Regex errorDescriptionPattern = ErrorRegex();

        private static string errorStyle = Path.Combine("./Compiling", "errorstyle.html");
        private static string errorCache = Path.Combine("./Compiling", "errors.txt");

        /// <summary>
        /// Sits beside errors.txt and records what that cache is and how the last fetch went: the URL it
        /// came from, the validators the server gave us, and when we last tried. Without it every launch
        /// looks like the first one, which is what let the app keep hammering a rate-limited source until
        /// it started refusing outright.
        /// </summary>
        private static string errorCacheMeta = Path.Combine("./Compiling", "errors.cache.json");

        // Offline fallbacks, used when the remote source is unreachable and no cache exists.
        // errors.default.txt is a copy of the upstream interlopers catalogue in its own format;
        // errors.supplement.json adds the messages that catalogue predates. See LoadOfflineErrorData.
        private static string bundledErrors = Path.Combine("./Compiling", "errors.default.txt");
        private static string supplementErrors = Path.Combine("./Compiling", "errors.supplement.json");

        /// <summary>
        /// How long to leave a source alone after it turns us away.
        ///
        /// The default source rate limits to roughly a handful of requests and answers 403 once past it.
        /// Retrying on the very next launch - which is what happened - turns a temporary throttle into a
        /// standing one, because the retries themselves keep the window open.
        /// </summary>
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(6);

        /// <summary>
        /// Shared. A fresh HttpClient per fetch strands a socket every time one is collected; a single
        /// fetch at startup never made that visible, but a fetch on every settings save and every
        /// shutdown did.
        /// </summary>
        private static readonly HttpClient http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.ExpectContinue = true;

            // Identify the app. A bare .NET client sends no User-Agent at all, which is exactly the
            // shape of request the default source's filtering rejects.
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", $"CompilePal/{UpdateManager.CurrentVersion} (+https://github.com/catualus/CompilePal)");

            return client;
        }

        /// <summary>What the last fetch attempt learned, persisted next to the cache.</summary>
        private class CacheMetadata
        {
            /// <summary>The source these validators and this cache belong to.</summary>
            public string SourceUrl { get; set; } = "";

            /// <summary>When the cache last held a body we actually received, or revalidated.</summary>
            public DateTime? FetchedUtc { get; set; }

            /// <summary>When a request was last made, successful or not.</summary>
            public DateTime? LastAttemptUtc { get; set; }

            /// <summary>Why the last attempt failed, or null if it succeeded.</summary>
            public string? LastFailure { get; set; }

            public string? ETag { get; set; }
            public string? LastModified { get; set; }
        }

        /// <summary>
        /// One load at a time. Init runs at startup and again whenever the source setting changes, and
        /// two loads racing means two downloads and a catalogue assembled from whichever finished last.
        /// </summary>
        private static readonly SemaphoreSlim loadLock = new(1, 1);

        /// <summary>
        /// Kicks off a load of the error catalogue.
        /// </summary>
        /// <param name="refresh">
        /// Revalidate against the source now, ignoring both the cache expiry and any active backoff.
        /// Reserved for the user actually asking - changing the source URL. Deliberately not what an
        /// ordinary settings save does: SaveSettings also runs on window close to persist the map list
        /// height and the last preset, so forcing a refresh there put a download on every single
        /// shutdown and spent the source's request budget within a few launches.
        /// </param>
        public static void Init(bool refresh = false)
        {
            Thread t = new Thread(() => AsyncInit(ConfigurationManager.Settings.ErrorSourceURL, ConfigurationManager.Settings.ErrorCacheExpirationDays, refresh))
            {
                // Nothing waits on this, and it must not hold the process open at shutdown.
                IsBackground = true,
            };
            t.Start();
        }

        static async void AsyncInit(string errorURL, int errorCacheExpirationDays, bool refresh)
        {
            if (!await loadLock.WaitAsync(TimeSpan.FromSeconds(60)))
            {
                CompilePalLogger.LogLineDebug("Error data load already in progress, skipping");
                return;
            }

            try
            {
                var meta = ReadCacheMetadata();

                // Validators belong to the URL they came from. Sending them to a different source asks
                // it whether its copy matches some other site's ETag, and a 304 answering that question
                // would leave the wrong catalogue loaded.
                if (meta.SourceUrl != errorURL)
                    meta = new CacheMetadata { SourceUrl = errorURL };

                bool haveCache = File.Exists(errorCache);

                bool cacheIsFresh = haveCache && meta.FetchedUtc is { } fetched
                    && DateTime.UtcNow.Subtract(fetched).TotalDays < errorCacheExpirationDays;

                // Caches written before this metadata existed have no FetchedUtc. Fall back to the
                // file's own timestamp rather than treating a perfectly good cache as expired.
                if (haveCache && meta.FetchedUtc is null)
                    cacheIsFresh = DateTime.Now.Subtract(File.GetLastWriteTime(errorCache)).TotalDays < errorCacheExpirationDays;

                if (!refresh && cacheIsFresh && TryLoadCache())
                    return;

                // A source that just turned us away is not asked again straight away. The cache, or the
                // bundled catalogue, carries the feature through the backoff window while it clears.
                if (!refresh && meta.LastFailure is not null && meta.LastAttemptUtc is { } lastAttempt
                    && DateTime.UtcNow.Subtract(lastAttempt) < FailureBackoff)
                {
                    CompilePalLogger.LogLineDebug(
                        $"Skipping error data fetch: last attempt failed ({meta.LastFailure}), not retrying for {FailureBackoff.TotalHours:0}h");

                    if (!TryLoadCache())
                        LoadOfflineErrorData();

                    return;
                }

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, errorURL);

                    // Conditional request. When the catalogue has not changed the server answers 304
                    // with no body - cheaper for it and for us, and on a source that counts requests it
                    // is the difference between staying current and being locked out.
                    if (haveCache)
                    {
                        if (meta.ETag is not null)
                            request.Headers.TryAddWithoutValidation("If-None-Match", meta.ETag);
                        if (meta.LastModified is not null)
                            request.Headers.TryAddWithoutValidation("If-Modified-Since", meta.LastModified);
                    }

                    meta.LastAttemptUtc = DateTime.UtcNow;

                    var httpResult = await http.SendAsync(request);

                    if (httpResult.StatusCode == HttpStatusCode.NotModified && TryLoadCache())
                    {
                        CompilePalLogger.LogLineDebug("Error data unchanged at source, keeping the cache");
                        meta.FetchedUtc = DateTime.UtcNow;
                        meta.LastFailure = null;
                        WriteCacheMetadata(meta);
                        return;
                    }

                    httpResult.EnsureSuccessStatusCode();
                    string result = await httpResult.Content.ReadAsStringAsync();

                    // Read from the content headers, where Content-Type actually lives. Looking for it
                    // on the response headers - which is what this did - never found it, so a source
                    // serving JSON was parsed as the line-based text format and produced nothing.
                    var contentType = httpResult.Content.Headers.ContentType?.MediaType;
                    if (contentType == "application/json")
                    {
                        LoadJSONErrorData(result);
                    }
                    else
                    {
                        LoadTextErrorData(result);
                    }

                    await File.WriteAllTextAsync(errorCache, JsonConvert.SerializeObject(errorList, new RegexConverter()));

                    meta.FetchedUtc = DateTime.UtcNow;
                    meta.LastFailure = null;
                    meta.ETag = httpResult.Headers.ETag?.ToString();
                    meta.LastModified = httpResult.Content.Headers.LastModified?.ToString("R");
                    WriteCacheMetadata(meta);
                }
                catch (Exception e)
                {
                    // The error-data source is a best-effort convenience fetch, and failing over to a
                    // local cache (or just running with no known-error lookups) is a fully handled,
                    // routine outcome - the source going offline/unreachable is not a CompilePal bug,
                    // so it stays out of the visible compile output and only goes to the debug log.
                    CompilePalLogger.LogLineDebug($"Failed to fetch error data from {errorURL}: {e.Message}");

                    // Recorded so the next launch backs off instead of trying again straight away.
                    meta.LastFailure = e.Message;
                    WriteCacheMetadata(meta);

                    if (TryLoadCache())
                    {
                        CompilePalLogger.LogLineDebug("Loading error data from cache");
                    }
                    else
                    {
                        // Without this the app silently runs with an EMPTY error list: nothing in the
                        // compile output is recognised, explained or navigable. The remote source has
                        // been returning 403 for every request, so relying on it alone means the
                        // feature is simply absent on a fresh install.
                        CompilePalLogger.LogLineDebug($"Error cache not usable: {errorCache}, falling back to bundled catalogue");
                        LoadOfflineErrorData();
                    }
                }
            }
            catch (Exception x)
            {
                //nonvital part, record but dont quit
                ExceptionHandler.LogException(x, false);
            }
            finally
            {
                loadLock.Release();
            }
        }

        /// <summary>
        /// Loads errors.txt if it is there and readable, reporting whether it yielded anything.
        ///
        /// A truncated cache - a crash mid-write, a full disk - used to throw out of the fetch handler
        /// and leave the app with no catalogue at all, rather than falling through to the bundled one.
        /// </summary>
        static bool TryLoadCache()
        {
            if (!File.Exists(errorCache))
                return false;

            try
            {
                LoadJSONErrorData(File.ReadAllText(errorCache));
                return errorList.Count > 0;
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Failed to read the error cache {errorCache}: {e.Message}");
                return false;
            }
        }

        static CacheMetadata ReadCacheMetadata()
        {
            try
            {
                if (File.Exists(errorCacheMeta))
                    return JsonConvert.DeserializeObject<CacheMetadata>(File.ReadAllText(errorCacheMeta)) ?? new CacheMetadata();
            }
            catch (Exception e)
            {
                CompilePalLogger.LogLineDebug($"Failed to read {errorCacheMeta}: {e.Message}");
            }

            return new CacheMetadata();
        }

        static void WriteCacheMetadata(CacheMetadata meta)
        {
            try
            {
                File.WriteAllText(errorCacheMeta, JsonConvert.SerializeObject(meta, Formatting.Indented));
            }
            catch (Exception e)
            {
                // Losing the bookkeeping only costs the backoff on the next run, which is not worth
                // failing a load over.
                CompilePalLogger.LogLineDebug($"Failed to write {errorCacheMeta}: {e.Message}");
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
            // Built here and published once at the end. Loading straight into errorList meant a second
            // load appended to the first, so every refresh doubled the catalogue - and the count logged
            // below was the running total, not what this load contributed.
            var loaded = new List<Error>();
            int upstream = 0;

            if (File.Exists(bundledErrors))
            {
                try
                {
                    ParseTextErrorData(File.ReadAllText(bundledErrors), loaded);
                    upstream = loaded.Count;
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

            int added = ParseSupplementErrorData(loaded);

            Publish(loaded);

            if (upstream + added > 0)
                CompilePalLogger.LogLineDebug($"Loaded {upstream} upstream + {added} supplementary error definitions");
        }

        static int ParseSupplementErrorData(List<Error> into)
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
                        into.Add(new Error
                        {
                            RegexTrigger = new Regex(entry.Pattern, RegexOptions.IgnoreCase),
                            Severity = entry.Severity,
                            ShortDescription = entry.Title,
                            Message = style.Replace("%content%", entry.Html),
                            ID = into.Count,
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
            var loaded = new List<Error>();
            ParseTextErrorData(input, loaded);
            Publish(loaded);
        }

        /// <summary>
        /// Renumbers and installs a freshly built catalogue as the live one.
        ///
        /// IDs have to be dense and unique across the whole list because <see cref="Error.Equals"/> and
        /// <see cref="Error.GetHashCode"/> are defined purely on ID - two entries sharing one are the
        /// same error as far as the log's occurrence counting is concerned.
        /// </summary>
        static void Publish(List<Error> loaded)
        {
            for (int i = 0; i < loaded.Count; i++)
                loaded[i].ID = i;

            errorList = loaded;
        }

        static void ParseTextErrorData(string input, List<Error> into)
        {
            string style = File.ReadAllText(errorStyle);

            var lines = input.Split(["\r\n", "\n"], StringSplitOptions.None);

            int count = int.Parse(lines[0]);

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

                into.Add(error);
            }
        }

        public static Error? GetError(string line)
        {
            // Read once. The field is replaced by a background refresh, and re-reading it inside the
            // loop could walk one list and then continue into another.
            var errors = errorList;

            foreach (var error in errors)
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
