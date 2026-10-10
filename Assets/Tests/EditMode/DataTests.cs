using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

// The shipped data through the app's own readers, and broken copies of it through the same readers: each must
// fail with a FormatException that names the file, never an index error deep in the map.
public class DataTests
{
    const string Folder = "Assets/Navigation/Data/";
    const string PlanPath = Folder + "ba1-floor1.json";
    const string PlacesPath = Folder + "ba1-floor1-places.bytes";
    const string WallsPath = Folder + "ba1-floor1-walls.bytes";
    const string WalkablePath = Folder + "ba1-floor1-walkable.bytes";
    const string CampusPath = Folder + "ucf-campus.bytes";
    const string CampusInfoPath = Folder + "ucf-campus.json";

    static TextAsset Asset(string path)
    {
        var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(path);
        Assert.IsNotNull(asset, $"Missing {path}: run Tools/ba1-floor1/make_data.py --campus");
        return asset;
    }

    static string PlanJson => Asset(PlanPath).text;
    static FloorPlan Plan => FloorPlan.Load(Asset(PlanPath));

    static byte[] Cut(byte[] bytes, int length)
    {
        var copy = new byte[length];
        Array.Copy(bytes, copy, Math.Min(length, bytes.Length));
        return copy;
    }

    static byte[] Patched(byte[] bytes, int at, params byte[] values)
    {
        var copy = (byte[])bytes.Clone();
        Array.Copy(values, 0, copy, at, values.Length);
        return copy;
    }

    [Test]
    public void ShippedDataPassesTheBuildCheck()
    {
        Assert.IsNull(BuildNavigateScene.DataProblem());
    }

    [Test]
    public void ShippedFilesReadBack()
    {
        FloorPlan plan = Plan;
        byte[] cells = FloorMap.ReadPlaceGrid(Asset(PlacesPath).bytes, PlacesPath, plan, out int width, out int height);
        Assert.AreEqual((600, 489), (width, height));
        Assert.AreEqual(width * height, cells.Length);

        WallMesh.Parse(Asset(WallsPath).bytes, WallsPath, out int[] rings, out Vector2[] points, out int[] triangles);
        Assert.Greater(rings.Length, 50);
        Assert.AreEqual(0, triangles.Length % 3);
        Assert.Less(Mathf.Abs(points[0].x), 60f);

        Walkable ground = Walkable.Load(Asset(WalkablePath).bytes);
        Assert.AreEqual(0.1f, ground.cell, 1e-6f);
        foreach (PlanNode node in plan.nodes)
            Assert.IsTrue(ground.IsWalkable(node.Position), node.id);

        CampusData campus = CampusData.Parse(Asset(CampusPath).bytes, Asset(CampusInfoPath).text, CampusPath);
        Assert.IsNotNull(campus.indoor);
        Assert.AreEqual(plan.building, campus.indoor.name);
        Assert.AreEqual(plan.geo.lat, campus.geo.lat, 1e-6f);
    }

    [Test]
    public void ShippedPlanHasSignsOnRealDoors()
    {
        FloorPlan plan = Plan;
        Assert.Greater(plan.signs.Count, 30);
        foreach (PlanSign sign in plan.signs)
        {
            PlanPlace place = plan.places.Find(p => p.id == sign.place);
            PlanDoor door = place.doors.Find(d => d.node == sign.door);
            Assert.IsNotNull(door, sign.text);
            Assert.AreEqual(1f, sign.Normal.magnitude, 0.01f);
            Assert.Less(Vector2.Dot(sign.Normal, door.Direction), -0.99f, $"{sign.text} faces back out of its door");
        }
    }

    [TestCase(PlacesPath)]
    [TestCase(WallsPath)]
    [TestCase(CampusPath)]
    [TestCase(WalkablePath)]
    public void BrokenCopiesThrowFormatException(string path)
    {
        byte[] bytes = Asset(path).bytes;
        Read(path, bytes);   // the real one reads
        string name = path;
        AssertRefused(path, Cut(bytes, bytes.Length - 1), name);              // a byte short
        AssertRefused(path, Cut(bytes, 3), name);                             // shorter than its header
        AssertRefused(path, Cut(bytes, bytes.Length + 1), name);              // a byte too many
        AssertRefused(path, Patched(bytes, 0, (byte)'X'), name);              // someone else's file
        AssertRefused(path, Patched(bytes, 4, 9, 0), name);                   // a version this app doesn't read
    }

    static void Read(string path, byte[] bytes)
    {
        switch (path)
        {
            case PlacesPath:
                FloorMap.ReadPlaceGrid(bytes, path, Plan, out _, out _);
                break;
            case WallsPath:
                WallMesh.Parse(bytes, path, out _, out _, out _);
                break;
            case CampusPath:
                CampusData.Parse(bytes, Asset(CampusInfoPath).text, path);
                break;
            default:
                Walkable.Load(bytes);
                break;
        }
    }

    static void AssertRefused(string path, byte[] bytes, string name)
    {
        var e = Assert.Throws<FormatException>(() => Read(path, bytes));
        // Walkable names itself "walkable"; the rest name the file they were given.
        StringAssert.Contains(path == WalkablePath ? "walkable" : name, e.Message);
    }

    [Test]
    public void PlaceGridForOtherPlacesIsRefused()
    {
        FloorPlan plan = Plan;
        (plan.places[0], plan.places[1]) = (plan.places[1], plan.places[0]);
        var e = Assert.Throws<FormatException>(() =>
            FloorMap.ReadPlaceGrid(Asset(PlacesPath).bytes, PlacesPath, plan, out _, out _));
        StringAssert.Contains("different list of places", e.Message);
    }

    [Test]
    public void BuildCheckNamesTheBrokenFile()
    {
        byte[] places = Asset(PlacesPath).bytes, walls = Asset(WallsPath).bytes;
        byte[] walkable = Asset(WalkablePath).bytes, campus = Asset(CampusPath).bytes;
        string info = Asset(CampusInfoPath).text;
        Assert.IsNull(BuildNavigateScene.DataProblem(PlanJson, places, walls, walkable, campus, info));
        StringAssert.Contains(WallsPath,
            BuildNavigateScene.DataProblem(PlanJson, places, Cut(walls, walls.Length - 2), walkable, campus, info));
        StringAssert.Contains(PlacesPath,
            BuildNavigateScene.DataProblem(PlanJson, null, walls, walkable, campus, info));
        string otherGeo = info.Replace("\"angle\":315.371", "\"angle\":300.0");
        Assert.AreNotEqual(info, otherGeo);
        StringAssert.Contains("geo fit",
            BuildNavigateScene.DataProblem(PlanJson, places, walls, walkable, campus, otherGeo));
    }

    // FloorPlan.Validate catches each kind of broken plan, naming what's wrong.
    [TestCase("duplicate", "used twice")]
    [TestCase("unknownEdge", "unknown node")]
    [TestCase("selfLoop", "to itself")]
    [TestCase("island", "in pieces")]
    [TestCase("doorFacesNowhere", "faces nowhere")]
    [TestCase("labelWithoutArea", "run basemap.py")]
    [TestCase("signOnNoDoor", "isn't a door of")]
    [TestCase("noImage", "image block")]
    [TestCase("noGeo", "geo block")]
    [TestCase("duplicatePlace", "used twice")]
    public void ValidateRejects(string breakage, string message)
    {
        FloorPlan plan = JsonUtility.FromJson<FloorPlan>(PlanJson);
        switch (breakage)
        {
            case "duplicate":
                plan.nodes[1].id = plan.nodes[0].id;
                break;
            case "unknownEdge":
                plan.edges[0].b = "nowhere";
                break;
            case "selfLoop":
                plan.edges[0].b = plan.edges[0].a;
                break;
            case "island":
                plan.nodes.Add(new PlanNode { id = "island", x = 1f, z = 1f });
                break;
            case "doorFacesNowhere":
                plan.places[0].doors[0].dx = plan.places[0].doors[0].dz = 0f;
                break;
            case "labelWithoutArea":
                plan.labels[0].area = 0f;
                break;
            case "signOnNoDoor":
                plan.signs[0].door = plan.nodes[0].id;
                break;
            case "noImage":
                plan.image = new PlanImage();
                break;
            case "noGeo":
                plan.geo = new PlanGeo();
                break;
            case "duplicatePlace":
                plan.places[1].id = plan.places[0].id;
                break;
        }
        Assert.IsFalse(FloorPlan.Validate(JsonUtility.ToJson(plan), out string error));
        StringAssert.Contains(message, error);
        StringAssert.Contains(message, plan.Check());
    }
}
