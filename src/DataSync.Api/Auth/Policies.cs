namespace DataSync.Api.Auth;

/// <summary>
/// What a request is allowed to do.
/// <para>
/// **The dividing line is "does this change something or make something happen", not "is this a
/// GET".** <c>POST /connections/{name}/test</c> opens a connection to somebody's database and
/// <c>POST /scripts/test</c> compiles and runs operator-authored C#; both are reads in HTTP terms and
/// neither belongs to a viewer.
/// </para>
/// <para>
/// The default for anything unmarked is <see cref="Admin"/>, so an endpoint added later is closed by
/// omission rather than open by omission. That is the whole reason to state a fallback policy at all.
/// </para>
/// </summary>
public static class Policies
{
    public const string Admin = "Admin";
    public const string Viewer = "Viewer";
}
