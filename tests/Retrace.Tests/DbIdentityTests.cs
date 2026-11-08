using Retrace.Core;
using Xunit;

namespace Retrace.Tests;

public class DbIdentityTests
{
    [Fact]
    public void An_unhooked_document_is_always_hooked_again_whatever_the_pointers_say()
    {
        Assert.True(DbIdentity.NeedsRehook(hooked: false, current: 5, hookedFor: 5));   // OnDocGone unhooked it: the same pointer does not mean its events are on
        Assert.True(DbIdentity.NeedsRehook(hooked: false, current: 0, hookedFor: 0));
    }

    [Fact]
    public void A_pointer_that_could_not_be_read_is_never_the_same_for_hooking()
    {
        Assert.True(DbIdentity.NeedsRehook(true, 0, 5));
        Assert.True(DbIdentity.NeedsRehook(true, 5, 0));
        Assert.True(DbIdentity.NeedsRehook(true, 6, 5));    // another drawing in the same document (SDI)
        Assert.False(DbIdentity.NeedsRehook(true, 5, 5));   // nothing changed: nothing is done twice
    }

    [Fact]
    public void A_pointer_that_could_not_be_read_is_the_same_for_restoring_a_view()
    {
        Assert.True(DbIdentity.SameForView(0, 5));
        Assert.True(DbIdentity.SameForView(5, 0));
        Assert.True(DbIdentity.SameForView(5, 5));
        Assert.False(DbIdentity.SameForView(6, 5));
    }
}
