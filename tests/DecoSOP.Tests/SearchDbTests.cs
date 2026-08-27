using DecoSOP.Data;
using Xunit;

namespace DecoSOP.Tests;

public class SearchDbTests
{
    [Fact]
    public void RowIdsNeverCollideAcrossModules()
    {
        // Rowids are computed rather than stored so a file's index row can be replaced without
        // looking up what id it was given. That only works if the mapping is injective.
        var seen = new HashSet<long>();
        for (var id = 1; id <= 20_000; id++)
        {
            Assert.True(seen.Add(SearchDb.RowIdFor(SearchDb.ModuleSop, id)), $"SOP {id} collided");
            Assert.True(seen.Add(SearchDb.RowIdFor(SearchDb.ModuleDoc, id)), $"Doc {id} collided");
        }
    }

    [Fact]
    public void TheSameFileAlwaysGetsTheSameRowId()
    {
        Assert.Equal(SearchDb.RowIdFor(SearchDb.ModuleDoc, 4242), SearchDb.RowIdFor(SearchDb.ModuleDoc, 4242));
    }

    [Fact]
    public void AnUnknownModuleDoesNotSilentlyAliasOntoSop()
    {
        // If a third module is ever added, this test should fail rather than the new module's
        // documents quietly overwriting SOP rows.
        Assert.Equal(SearchDb.RowIdFor("Sop", 7), SearchDb.RowIdFor("Inventory", 7));
    }
}
