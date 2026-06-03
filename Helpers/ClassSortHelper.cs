using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ЧОШ_информатор.Helpers;

public static class ClassSortHelper
{
    public static IOrderedEnumerable<string> SortClasses(this IEnumerable<string> classes)
    {
        return classes.OrderBy(GetClassSortValue).ThenBy(c => c);
    }

    public static int GetClassSortValue(string className)
    {
        if (string.IsNullOrEmpty(className)) return int.MaxValue;
        var match = Regex.Match(className, @"\d+");
        if (match.Success && int.TryParse(match.Value, out int result))
        {
            return result;
        }
        return int.MaxValue;
    }
}
