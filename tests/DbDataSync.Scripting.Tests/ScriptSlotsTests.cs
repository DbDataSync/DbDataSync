using DbDataSync.Scripting.Abstractions;

namespace DbDataSync.Scripting.Tests;

/// <summary>
/// <see cref="ScriptSlots.Hook"/> is a known script <c>Kind</c> — a reusable SQL hook must save — but
/// deliberately absent from <see cref="ScriptSlots.All"/>, which also drives the SPA's generic
/// per-slot binding card. A hook has no binding hierarchy of its own; it is referenced by name from
/// inside a <c>HookConfig</c> entry, not bound the way <c>sqlColumnExpression</c> and friends are.
/// </summary>
public sealed class ScriptSlotsTests
{
    [Fact]
    public void Hook_IsKnown_ButNotInAll()
    {
        Assert.True(ScriptSlots.IsKnown(ScriptSlots.Hook));
        Assert.DoesNotContain(ScriptSlots.Hook, ScriptSlots.All);
    }

    [Fact]
    public void LifecycleHook_IsKnown_AndInAll()
    {
        Assert.True(ScriptSlots.IsKnown(ScriptSlots.LifecycleHook));
        Assert.Contains(ScriptSlots.LifecycleHook, ScriptSlots.All);
    }

    [Fact]
    public void AnUnknownKind_IsNotKnown() => Assert.False(ScriptSlots.IsKnown("somethingElse"));
}
