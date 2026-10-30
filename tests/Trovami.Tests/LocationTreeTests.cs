using Trovami.Core.Models;
using Trovami.Core.Services;

namespace Trovami.Tests;

public class LocationTreeTests
{
    private static readonly Guid House = Guid.NewGuid(), Cellar = Guid.NewGuid(), Rack = Guid.NewGuid(), Box = Guid.NewGuid();

    private static LocationTree Tree() => new(
    [
        Node(House, null, "House"),
        Node(Cellar, House, "Cellar"),
        Node(Rack, Cellar, "Rack 2", items: 1),
        Node(Box, Rack, "Blue box", items: 3),
    ]);

    private static LocationNode Node(Guid id, Guid? parent, string name, int items = 0) =>
        new(id, ShortCodes.New(), parent, name, LocationKind.Other, null, null, null, null, items, 0);

    [Fact]
    public void PathGoesFromTheTopDown()
    {
        Assert.Equal("House › Cellar › Rack 2 › Blue box", Tree().PathOf(Box));
        Assert.Equal("Inbox", Tree().PathOf(null));
    }

    [Fact]
    public void KnowsWhatIsUnderWhat()
    {
        var tree = Tree();
        Assert.True(tree.IsUnder(Box, House));
        Assert.False(tree.IsUnder(House, Box));
        Assert.False(tree.IsUnder(House, House));
    }

    [Fact]
    public void CountsItemsInTheWholeSubtree()
    {
        Assert.Equal(4, Tree().ItemsBelow(House));
        Assert.Equal(3, Tree().ItemsBelow(Box));
    }

    [Fact]
    public void AMissingParentCountsAsTopLevel()
    {
        var orphan = Guid.NewGuid();
        var tree = new LocationTree([Node(orphan, Guid.NewGuid(), "Lost")]);
        Assert.Single(tree.ChildrenOf(null));
        Assert.Equal("Lost", tree.PathOf(orphan));
    }
}
