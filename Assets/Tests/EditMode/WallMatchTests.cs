using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

// Wall matching and the depth scan's degenerate case, on a synthetic 60 m x 2 m corridor (10 cm cells).
public class WallMatchTests
{
    static readonly Vector2 Truth = new Vector2(30f, 1.3f);

    static Walkable Corridor()
    {
        int w = 640, h = 40;
        var rows = new string[h];
        var c = new char[w];
        for (int j = 0; j < h; j++)
        {
            for (int i = 0; i < w; i++)
            {
                float x = -2f + (i + 0.5f) * 0.1f, z = 3f - (j + 0.5f) * 0.1f;
                c[i] = x > 0f && x < 60f && z > 0f && z < 2f ? '.' : '#';
            }
            rows[j] = new string(c);
        }
        return Walkable.FromRows(-2f, 3f, 0.1f, rows);
    }

    static float Gaussian(System.Random random)
    {
        double u = 1.0 - random.NextDouble(), v = random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v));
    }

    // The corridor's two walls as ARCore would report them from the truth, the world turned by bias degrees.
    static List<WallObservation> Seen(System.Random random, float bias, bool phantom)
    {
        var seen = new List<WallObservation>();
        foreach (float wallZ in new[] { 0f, 2f })
        {
            var centre = new Vector2(Truth.x + Gaussian(random) * 0.5f, wallZ + Gaussian(random) * 0.03f);
            var normal = new Vector2(0f, wallZ == 0f ? 1f : -1f);
            float turn = Gaussian(random) * 2f;
            seen.Add(new WallObservation
            {
                centre = PositionFilter.ToPlan(centre - Truth, -bias),
                direction = PositionFilter.ToPlan(PositionFilter.ToPlan(Vector2.right, turn), -bias),
                normal = PositionFilter.ToPlan(PositionFilter.ToPlan(normal, turn), -bias),
                length = 2f,
            });
        }
        if (phantom)
        {
            // A cabinet across the corridor the plan doesn't have.
            seen.Add(new WallObservation
            {
                centre = PositionFilter.ToPlan(new Vector2(2f, 0.7f), -bias),
                direction = PositionFilter.ToPlan(Vector2.up, -bias),
                normal = PositionFilter.ToPlan(Vector2.left, -bias),
                length = 1.2f,
            });
        }
        return seen;
    }

    [TestCase(false)]
    [TestCase(true)]
    public void WallsRecoverTheHeadingAndTheSidewaysPosition(bool phantom)
    {
        Walkable walkable = Corridor();
        var matcher = new WallMatcher(new List<(Vector2, Vector2)>
        {
            (new Vector2(0f, 0f), new Vector2(60f, 0f)),
            (new Vector2(60f, 2f), new Vector2(0f, 2f)),
        }, walkable);
        var random = new System.Random(phantom ? 2 : 1);
        for (int s = 0; s < 6; s++)
        {
            float bias = s % 2 == 0 ? 7f : -7f;   // the AR world is turned this much; the filter starts thinking 0
            var filter = new PositionFilter(walkable, 300, s + 1, 2000) { Adaptive = true };
            filter.Reset(Truth + new Vector2(0f, s % 2 == 0 ? 0.5f : -0.5f), Vector2.zero, 0f, 0.4f, 6f, 45f);
            for (int u = 0; u < 30; u++)
                matcher.Weigh(filter, Seen(random, bias, phantom), Vector2.zero);
            float heading = Mathf.Abs(Mathf.DeltaAngle(filter.Yaw, bias));
            float sideways = Mathf.Abs(filter.Estimate.y - Truth.y);
            Assert.Less(heading, 2f, $"case {s}: heading {heading:F2} degrees off");
            Assert.Less(sideways, 0.3f, $"case {s}: {sideways:F2} m off sideways");
        }
    }

    [Test]
    public void ALongCorridorKeepsItsSpreadAlongButNotAcross()
    {
        // The scan sees both side walls but neither end (6 m range): positions along the corridor can't be told
        // apart, and the filter mustn't pretend they can.
        Walkable walkable = Corridor();
        var field = DistanceField.Build(walkable, null, null);
        var random = new System.Random(3);
        var points = new Vector2[ScanBuffer.Target];
        var filter = new PositionFilter(walkable, 1000, 1, 2000) { Adaptive = true };
        filter.Reset(Truth, Vector2.zero, 0f, 0.3f, 3f, 45f);
        filter.Scatter(Truth, 10f, false);
        var matcher = new ScanMatcher(field, 2000);
        for (int u = 0; u < 30; u++)
        {
            var buffer = new ScanBuffer();
            for (int f = 0; f < 10; f++)
            {
                for (int k = 0; k < 40; k++)
                {
                    float heading = 90f + (u % 6 - 3) * 25f + (k / 39f - 0.5f) * 60f + (f - 5) * 6f;
                    Vector2 d = PositionFilter.HeadingVector(heading);
                    float hit = -1f;
                    for (float s = 0.05f; s <= 6f; s += 0.05f)
                    {
                        if (!walkable.IsWalkable(Truth + d * s))
                        {
                            hit = s;
                            break;
                        }
                    }
                    if (hit < 0f)
                        continue;
                    float range = random.NextDouble() < 0.2 ? 0.4f + (float)random.NextDouble() * (hit - 0.4f)
                        : hit + Gaussian(random) * 0.05f;
                    if (range >= 0.4f)
                        buffer.Add(f * 0.2, d * range);
                }
            }
            matcher.Weigh(filter, points, buffer.Build(2.0, points));
        }
        float along = 0f, across = 0f, total = 0f;
        for (int i = 0; i < filter.Count; i++)
        {
            float w = filter.ParticleWeight(i);
            Vector2 d = filter.ParticlePosition(i) - Truth;
            along += w * d.x * d.x;
            across += w * d.y * d.y;
            total += w;
        }
        along = Mathf.Sqrt(along / total);
        across = Mathf.Sqrt(across / total);
        Assert.Greater(along, 3f, $"spread along collapsed to {along:F2} m");
        Assert.Less(across, 0.3f, $"spread across {across:F2} m");
        Assert.Greater(filter.Radius(0.68f), 3f, "the 68% radius should show the ambiguity");
        Assert.Less(filter.Confidence, 0.5f, "confidence should show the ambiguity");
    }
}
