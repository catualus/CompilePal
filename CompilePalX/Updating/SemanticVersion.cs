using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CompilePalX
{
    /// <summary>
    /// A Semantic Versioning 2.0.0 version, with the ordering rules that come with it.
    ///
    /// <see cref="Version"/> cannot represent this. It parses digits and dots only, so a
    /// prerelease has nowhere to live - which is why the scheme inherited from upstream encoded
    /// one in the minor component instead: stable "029", prerelease "029.1". That had two
    /// consequences worth stating, because they are the reason this type exists.
    ///
    /// There was no minor version. The slot a minor number would occupy was the prerelease
    /// counter, so no small feature release was possible without incrementing the major.
    ///
    /// And prereleases sorted *above* stable. "029.1" parses to 29.1.0.0, which is greater than
    /// 29.0.0.0 - the exact opposite of what "prerelease" means. Under SemVer, 1.2.0-rc.1 is
    /// correctly less than 1.2.0.
    /// </summary>
    public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
    {
        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }

        /// <summary>The dot-separated identifiers after '-', or empty for a stable release.</summary>
        public IReadOnlyList<string> PreRelease { get; }

        public bool IsPreRelease => PreRelease.Count > 0;

        private SemanticVersion(int major, int minor, int patch, IReadOnlyList<string> preRelease)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            PreRelease = preRelease;
        }

        /// <summary>
        /// Parses "MAJOR.MINOR.PATCH" with an optional "-prerelease" suffix.
        ///
        /// Build metadata ("+abc") is accepted and discarded, per the spec: it takes no part in
        /// ordering, so keeping it would only invite someone to compare on it.
        /// </summary>
        public static bool TryParse(string? text, out SemanticVersion? version)
        {
            version = null;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            var value = text.Trim();

            // Tolerated because tags carry it and people type it. Not emitted anywhere.
            if (value.StartsWith('v') || value.StartsWith('V'))
                value = value[1..];

            var plus = value.IndexOf('+');
            if (plus >= 0)
                value = value[..plus];

            var dash = value.IndexOf('-');
            var preRelease = Array.Empty<string>();

            if (dash >= 0)
            {
                var suffix = value[(dash + 1)..];
                value = value[..dash];

                if (suffix.Length == 0)
                    return false;

                preRelease = suffix.Split('.');

                // Every identifier must be non-empty and alphanumeric-or-hyphen. A numeric one
                // must not carry leading zeroes, because it is compared as a number and "01"
                // would otherwise be a second spelling of "1".
                foreach (var identifier in preRelease)
                {
                    if (identifier.Length == 0) return false;
                    if (!identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
                    if (identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit)) return false;
                }
            }

            var parts = value.Split('.');
            if (parts.Length != 3)
                return false;

            var numbers = new int[3];
            for (var i = 0; i < 3; i++)
            {
                // No leading zeroes, no signs, no whitespace - NumberStyles.None rather than the
                // default, which would accept " 1 " and make two spellings of one version.
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                    return false;

                if (parts[i].Length > 1 && parts[i][0] == '0')
                    return false;
            }

            version = new SemanticVersion(numbers[0], numbers[1], numbers[2], preRelease);
            return true;
        }

        public static SemanticVersion Parse(string text) =>
            TryParse(text, out var version) && version is not null
                ? version
                : throw new FormatException($"'{text}' is not a semantic version.");

        /// <summary>
        /// Ordering per the specification.
        ///
        /// The part people get wrong, and the reason this is written out rather than delegated:
        /// a version WITH a prerelease is lower than the same version without one. 1.2.0-rc.1
        /// precedes 1.2.0. Numeric identifiers compare numerically, alphanumeric ones compare as
        /// text, numeric sorts below alphanumeric, and a longer run of identifiers wins a tie.
        /// </summary>
        public int CompareTo(SemanticVersion? other)
        {
            if (other is null) return 1;

            var byNumber = Major.CompareTo(other.Major);
            if (byNumber != 0) return byNumber;

            byNumber = Minor.CompareTo(other.Minor);
            if (byNumber != 0) return byNumber;

            byNumber = Patch.CompareTo(other.Patch);
            if (byNumber != 0) return byNumber;

            if (!IsPreRelease && !other.IsPreRelease) return 0;

            // A stable release outranks any prerelease of the same numbers.
            if (!IsPreRelease) return 1;
            if (!other.IsPreRelease) return -1;

            var shared = Math.Min(PreRelease.Count, other.PreRelease.Count);
            for (var i = 0; i < shared; i++)
            {
                var mine = PreRelease[i];
                var theirs = other.PreRelease[i];

                var mineNumeric = mine.All(char.IsAsciiDigit);
                var theirsNumeric = theirs.All(char.IsAsciiDigit);

                if (mineNumeric && theirsNumeric)
                {
                    var comparison = long.Parse(mine, CultureInfo.InvariantCulture)
                        .CompareTo(long.Parse(theirs, CultureInfo.InvariantCulture));
                    if (comparison != 0) return comparison;
                    continue;
                }

                if (mineNumeric != theirsNumeric)
                    return mineNumeric ? -1 : 1;

                var text = string.CompareOrdinal(mine, theirs);
                if (text != 0) return text;
            }

            return PreRelease.Count.CompareTo(other.PreRelease.Count);
        }

        public override string ToString()
        {
            var core = $"{Major}.{Minor}.{Patch}";
            return IsPreRelease ? core + "-" + string.Join('.', PreRelease) : core;
        }

        public bool Equals(SemanticVersion? other) => CompareTo(other) == 0;
        public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(Major, Minor, Patch, string.Join('.', PreRelease));

        public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
        public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
        public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
        public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;
        public static bool operator ==(SemanticVersion? a, SemanticVersion? b) =>
            a is null ? b is null : a.Equals(b);
        public static bool operator !=(SemanticVersion? a, SemanticVersion? b) => !(a == b);
    }
}
