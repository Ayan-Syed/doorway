using System;
using System.Collections.Generic;
using UnityEngine;

// ReplayEvaluator, continued: a fingerprint of one stress walk, stage by stage (the floor the walks are made on, the
// distance field, the generated log rows, the localizer's estimate after every truth row), as FNV-1a hashes. The
// editor test (DeterminismTests) and the harness outside Unity compute it with the same code: equal fingerprints
// mean equal results; the first stage that differs says where they part.
public static partial class ReplayEvaluator
{
    /// <summary>The 40-walk typical aggregate (seeds 1, 4, ... 118), each walk replayed with the given localizer
    /// seeds and pooled: p75 error (m), %wrong, %sure, %Lost and calibration, for comparing the editor with the
    /// harness statistically (bit-for-bit they part: the localizer's floating point differs between runtimes).</summary>
    public static float[] TypicalAggregate(FloorPlan plan, Walkable walkable, DistanceField field, WallMatcher walls,
        SyntheticWalks.Floor floor, int[] engineSeeds)
    {
        var results = new List<StressWalkResult>();
        for (int k = 0; k < 40; k++)
        {
            int seed = 3 * k + 1;
            List<string> lines = SyntheticWalks.Stress(floor, SyntheticWalks.Tier.Typical, seed, out SyntheticWalks.StressInfo info);
            var walk = new StressWalk { name = $"stress-{seed:D4}", tier = SyntheticWalks.Tier.Typical, info = info.ToString(), lines = lines };
            foreach (int e in engineSeeds)
                results.Add(ReplayStress(plan, walkable, field, walls, LocalizerEngine.Config.Full, walk, e));
        }
        StressSummary s = SummarizeStress(results)[0];
        return new[] { s.p75, 100f * s.wrongShare, 100f * s.localizedShare, 100f * s.lostShare, s.calibration };
    }

    public static string Fingerprint(FloorPlan plan, Walkable walkable, DistanceField field, WallMatcher walls,
        SyntheticWalks.Floor floor, int seed, SyntheticWalks.MismatchKnobs mismatch)
    {
        ulong floorHash = Offset, fieldHash = Offset, rowsHash = Offset, estimatesHash = Offset;
        foreach (Vector2[] route in floor.routes)
            foreach (Vector2 p in route)
                Add(ref floorHash, p);
        foreach (Vector2 p in floor.doorSpots)
            Add(ref floorHash, p);
        foreach (Vector2 p in floor.doorDirections)
            Add(ref floorHash, p);
        foreach (var (a, b) in floor.walls)
        {
            Add(ref floorHash, a);
            Add(ref floorHash, b);
        }
        foreach (PlanSign s in floor.signs)
            Add(ref floorHash, s.Position);
        for (float x = -40f; x <= 40f; x += 0.37f)
            for (float z = -40f; z <= 40f; z += 0.37f)
                Add(ref fieldHash, (uint)field.Centimetres(x, z));

        SyntheticWalks.MismatchKnobs was = SyntheticWalks.Mismatch;
        SyntheticWalks.Mismatch = mismatch;
        List<string> lines;
        try
        {
            lines = SyntheticWalks.Stress(floor, (SyntheticWalks.Tier)(seed % 3), seed, out _);
        }
        finally
        {
            SyntheticWalks.Mismatch = was;
        }
        // And per row kind (a, c, d, k, m, n, p, s, T, E), with the first row of each kind's length, to say which
        // part of the walk parted first when the rows differ.
        var kinds = new SortedDictionary<char, ulong>();
        var firsts = new SortedDictionary<char, string>();
        foreach (string line in lines)
        {
            char kind = line.Length > 0 ? line[0] : ' ';
            kinds.TryGetValue(kind, out ulong h);
            if (h == 0)
            {
                h = Offset;
                firsts[kind] = line.Length > 48 ? line.Substring(0, 48) : line;
            }
            foreach (char c in line)
            {
                if (kind != '#')   // comments (the walk's description) aren't data, and their text formats differently
                    Add(ref rowsHash, c);
                Add(ref h, c);
            }
            kinds[kind] = h;
        }
        var detail = new System.Text.StringBuilder();
        foreach (var kv in kinds)
            detail.Append(' ').Append(kv.Key).Append(':').Append((kv.Value & 0xffffffff).ToString("X8"));

        var engine = new LocalizerEngine(plan, walkable, field, walls, LocalizerEngine.Config.Full, 1);
        var feeder = new Feeder(engine);
        foreach (string line in lines)
        {
            if (!feeder.Feed(line, out string[] c, out _) || c[0] != "T")
                continue;
            Add(ref estimatesHash, engine.Position);
            Add(ref estimatesHash, (uint)engine.State);
        }
        LastDetail = detail + " | " + string.Join(" | ", firsts.Values);
        return $"floor {floorHash:X16} field {fieldHash:X16} rows {rowsHash:X16} estimates {estimatesHash:X16}";
    }

    /// <summary>The last fingerprint's rows per kind, and the first row of each kind (for a mismatch's message).</summary>
    public static string LastDetail { get; private set; }

    const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;

    static void Add(ref ulong hash, uint value)
    {
        for (int i = 0; i < 4; i++, value >>= 8)
        {
            hash ^= value & 0xff;
            hash *= Prime;
        }
    }

    static void Add(ref ulong hash, Vector2 v)
    {
        Add(ref hash, BitConverter.ToUInt32(BitConverter.GetBytes(v.x), 0));
        Add(ref hash, BitConverter.ToUInt32(BitConverter.GetBytes(v.y), 0));
    }
}
