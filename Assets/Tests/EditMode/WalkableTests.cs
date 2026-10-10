using NUnit.Framework;
using UnityEngine;

public class WalkableTests
{
    // A 1 m x 0.5 m hallway of 10 cm cells, its left end at x = 0 and its top at z = 0.5, with a wall stub.
    static Walkable Hallway()
    {
        return Walkable.FromRows(0f, 0.5f, 0.1f,
            "##########",
            "#........#",
            "#...#....#",
            "#...#....#",
            "##########");
    }

    [Test]
    public void CellsAreWalkableOnlyWhereMarked()
    {
        Walkable w = Hallway();
        Assert.IsTrue(w.IsWalkable(new Vector2(0.15f, 0.35f)));
        Assert.IsFalse(w.IsWalkable(new Vector2(0.05f, 0.35f)));   // the left wall
        Assert.IsFalse(w.IsWalkable(new Vector2(0.45f, 0.25f)));   // the stub
        Assert.IsFalse(w.IsWalkable(new Vector2(-1f, 0.3f)));      // off the grid
    }

    [Test]
    public void SegmentThroughTheStubIsBlocked()
    {
        Walkable w = Hallway();
        Assert.IsTrue(w.SegmentClear(new Vector2(0.15f, 0.35f), new Vector2(0.85f, 0.35f)));
        Assert.IsFalse(w.SegmentClear(new Vector2(0.15f, 0.15f), new Vector2(0.85f, 0.15f)));
    }

    [Test]
    public void NearestFindsTheClosestWalkableCell()
    {
        Walkable w = Hallway();
        Assert.IsTrue(w.Nearest(new Vector2(0.45f, 0.25f), 0.5f, out Vector2 p));
        Assert.IsTrue(w.IsWalkable(p));
        Assert.Less(Vector2.Distance(p, new Vector2(0.45f, 0.25f)), 0.11f);
        Assert.IsFalse(w.Nearest(new Vector2(5f, 5f), 0.2f, out _));
    }

    [Test]
    public void LoadRejectsAnythingButAWalkableFile()
    {
        Assert.Throws<System.FormatException>(() => Walkable.Load(new byte[] { 1, 2, 3 }));
        Assert.Throws<System.FormatException>(() => Walkable.Load(new byte[30]));
    }
}
