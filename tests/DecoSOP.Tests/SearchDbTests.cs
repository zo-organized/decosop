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
            Assert.True(seen.Add(SearchDb.RowIdFor(SearchDb.ModuleOnlineSop, id)), $"Online SOP {id} collided");
            Assert.True(seen.Add(SearchDb.RowIdFor(SearchDb.ModuleOnlineDoc, id)), $"Online Doc {id} collided");
        }
    }

    [Fact]
    public void TheSameFileAlwaysGetsTheSameRowId()
    {
        Assert.Equal(SearchDb.RowIdFor(SearchDb.ModuleDoc, 4242), SearchDb.RowIdFor(SearchDb.ModuleDoc, 4242));
    }

    [Fact]
    public void LocalRowIdsAreUnchangedSoExistingIndexesStayValid()
    {
        // Indexes built before ONLINE pages existed used fileId*2 (+1 for Doc). Changing that
        // would silently orphan every row in an existing index.
        Assert.Equal(14, SearchDb.RowIdFor(SearchDb.ModuleSop, 7));
        Assert.Equal(15, SearchDb.RowIdFor(SearchDb.ModuleDoc, 7));
    }

    [Fact]
    public void AnUnknownModuleIsRejectedRatherThanAliasedOntoAnother()
    {
        Assert.Throws<ArgumentException>(() => SearchDb.RowIdFor("Inventory", 7));
    }
}
