using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Data;

namespace CompilePalX
{
    /// <summary>
    /// Splits a picker's rows into groups the dialog draws differently.
    ///
    /// <c>IsCompatible</c> is the flat list at the top. <c>Incompatible</c> is folded away under a
    /// warning. <c>Discovered</c> is folded away too, without the warning: it is everything the
    /// compiler reported that Compile Pal's own list does not describe, named by its flag and
    /// explained in the compiler's words - available, but a long tail that would otherwise bury the
    /// curated options it is appended to.
    /// </summary>
    internal class IsCompatiblePropertyGroup : PropertyGroupDescription
    {
        public const string Compatible = "IsCompatible";
        public const string Incompatible = "Incompatible";
        public const string Discovered = "Discovered";

        public override object GroupNameFromItem(object item, int level, CultureInfo culture)
        {
            // dynamically get IsCompatible property
            bool? res = item.GetType().GetProperty("IsCompatible")?.GetValue(item, null) as bool?;

            if (res != true)
                return Incompatible;

            bool discovered = item.GetType().GetProperty("FromToolHelp")?.GetValue(item, null) as bool? == true;

            return discovered ? Discovered : Compatible;
        }
    }
}
