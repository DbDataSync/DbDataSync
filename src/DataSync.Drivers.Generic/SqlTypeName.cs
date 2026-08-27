namespace DataSync.Drivers.Generic;

public static class SqlTypeName
{
    /// <summary>The bare type name from a DDL-ready spec: <c>decimal(18,2)</c> → <c>decimal</c>.
    /// Lower-cased, because no engine in scope treats type names case-sensitively.</summary>
    public static string BaseOf(string nativeType)
    {
        var paren = nativeType.IndexOf('(');
        return (paren < 0 ? nativeType : nativeType[..paren]).Trim().ToLowerInvariant();
    }
}
