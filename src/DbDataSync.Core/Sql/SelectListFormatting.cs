namespace DbDataSync.Core.Sql;

/// <summary>
/// How a reader's SELECT list is joined together — one column per line rather than a single run-on
/// line, so a mapping with a hundred-plus columns is still readable wherever this statement is shown,
/// not just executed. Whitespace between SELECT-list entries is semantically inert on every engine this
/// project targets, so this changes nothing about what the statement does — including in "Preview SQL",
/// which shows the exact text a pass would run and must keep meaning that.
/// </summary>
public static class SelectListFormatting
{
    public static string JoinSelectList(IEnumerable<string> entries) => string.Join(",\n    ", entries);
}
