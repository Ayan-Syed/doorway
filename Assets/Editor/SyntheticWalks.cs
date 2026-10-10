using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// Walk logs made up on the real floor, in exactly the format the phone writes (PoseLog), so the evaluator and the
// tests run before real walks arrive. Each walk follows a route at 1.2 m/s with the phone mostly ahead, and ARCore's
// view of it is turned (a heading bias that drifts), scaled, and jittered. The sensors are simulated against the
// plan: depth rays to the nearest wall (5 cm noise, 20% outliers short of the wall: people and furniture), the
// walls within 4 m that ARCore finds (about half of them: plain paint gives it nothing), plates read now and then
// (half with depth, half by digit size), a tracking loss with steps and a compass, and checkpoints at every bend.
// The truth goes in too, five times a second, as T,t,x,z rows (the evaluator skips them; the tests use them).
// Seeded: the same seed makes the same walks.
public static partial class SyntheticWalks
{
    public struct Options
    {
        public int seed;
        /// <summary>The walk is snapped at a door this far from where it really starts (0: the right door).</summary>
        public float wrongSnap;
        /// <summary>Chance of one tracking loss (4 to 8 s, steps only) in the walk.</summary>
        public float lossChance;
        /// <summary>Heading bias range either way (degrees), and scale error range either way; with exact, exactly
        /// these.</summary>
        public float bias, scale;
        public bool exact;
        public bool scans, walls, plates;
        /// <summary>Lost by the user's asking at this many metres in (0: never), to test relocalizing.</summary>
        public float relocalizeAt;

        public static Options Default(int seed) => new Options
        {
            seed = seed, lossChance = 0.6f, bias = 8f, scale = 0.03f, scans = true, walls = true, plates = true,
        };
    }

    const float Pace = 1.2f, PoseRate = 15f, MeasureRate = 5f, CompassRate = 2f, SightRate = 2f;
    const float CameraHeight = 1.4f;
    const int Rays = 40;
    const float FieldOfView = 60f, MaxDepth = 6f, DepthNoise = 0.05f, OutlierShare = 0.2f;
    const float WallChance = 0.5f, WallRange = 4f, WallView = 50f;
    const float SignView = 25f, SignChance = 0.6f;

    // One walk along route (plan points, the first a door spot), against the floor's walkable map, wall segments and
    // signs. Returns the log's lines.
    public static List<string> Walk(Vector2[] route, Walkable walkable, List<(Vector2 a, Vector2 b)> walls,
        List<PlanSign> signs, Vector2[] doors, Options options)
    {
        var random = new System.Random(options.seed);
        var log = new List<string>();
        var line = new StringBuilder();
        float bias = options.exact ? options.bias : Uniform(random, -options.bias, options.bias);
        float scale = 1f + (options.exact ? options.scale : Uniform(random, -options.scale, options.scale));
        float compassOffset = Uniform(random, -180f, 180f);
        float snapError = Gaussian(random) * 5f;
        float drift = 0f;
        double t = 0;
        log.Add($"# Doorway synthetic walk, seed {options.seed}, bias {F(bias, 1)}, scale {F(scale, 3)}");

        // Which plan walls ARCore will find on this walk.
        var found = new bool[walls.Count];
        for (int k = 0; k < walls.Count; k++)
            found[k] = random.NextDouble() < WallChance;

        Vector2 truth = route[0];
        Vector2 world = new Vector2(Uniform(random, -2f, 2f), Uniform(random, -2f, 2f));
        float facing = PositionFilter.Heading(route.Length > 1 ? route[1] - route[0] : Vector2.up);

        Vector2 spot = route[0];
        if (options.wrongSnap > 0f && doors != null)
        {
            foreach (Vector2 door in doors)
            {
                if (Vector2.Distance(door, route[0]) >= options.wrongSnap)
                {
                    spot = door;
                    break;
                }
            }
        }
        log.Add(Row("n", t, "start", spot.x, spot.y, bias + drift + snapError, world.x, world.y));
        log.Add(Row("c", t, "start", route[0].x, route[0].y));

        // The walk, frame by frame at PoseRate.
        float length = 0f;
        for (int k = 1; k < route.Length; k++)
            length += Vector2.Distance(route[k - 1], route[k]);
        float lossStart = random.NextDouble() < options.lossChance ? Uniform(random, 10f, Mathf.Max(10f, length - 15f)) : -1f;
        float lossLength = Uniform(random, 4f, 8f) * Pace;
        var buffer = new ScanBuffer();
        var scan = new Vector2[ScanBuffer.Target];
        double nextMeasure = 0, nextCompass = 0, nextSight = 0;
        var lastSight = new Dictionary<int, double>();
        float walked = 0f, sinceStep = 0f, look = 0f, lookUntil = -1f;
        bool relocalized = false;
        int leg = 1;
        float dt = 1f / PoseRate;
        while (leg < route.Length)
        {
            t += dt;
            // Move along the route by one frame.
            float move = Pace * dt;
            Vector2 before = truth;
            while (move > 0f && leg < route.Length)
            {
                Vector2 to = route[leg] - truth;
                if (to.magnitude <= move)
                {
                    move -= to.magnitude;
                    truth = route[leg];
                    log.Add(Row("c", t, $"bend{leg}", truth.x, truth.y));
                    leg++;
                }
                else
                {
                    truth += to.normalized * move;
                    move = 0f;
                }
            }
            Vector2 step = truth - before;
            walked += step.magnitude;
            if (step.sqrMagnitude > 1e-8f)
                facing = PositionFilter.Heading(step);
            drift += Gaussian(random) * 0.4f * Mathf.Sqrt(step.magnitude);
            // Now and then the user looks aside for a moment.
            if (t > lookUntil && random.NextDouble() < 0.05 * dt)
            {
                look = Uniform(random, -60f, 60f);
                lookUntil = (float)t + 1.5f;
            }
            float phone = facing + (t < lookUntil ? look : 0f) + Gaussian(random) * 3f;

            bool lost = lossStart >= 0f && walked >= lossStart && walked < lossStart + lossLength;
            float yaw = bias + drift;
            world += PositionFilter.ToPlan(step, -yaw) * scale;
            if (lost)
            {
                sinceStep += step.magnitude;
                if (sinceStep >= 0.72f)
                {
                    sinceStep -= 0.72f;
                    log.Add(Row("k", t, F(Mathf.Repeat(phone - compassOffset + Gaussian(random) * 6f, 360f), 1), 0f));
                }
                log.Add(Row("a", t, 0, world.x, CameraHeight, world.y, phone + yaw));
                continue;
            }
            if (lossStart >= 0f && walked >= lossStart + lossLength && sinceStep >= 0f)
            {
                // ARCore back after the loss: its pose has slipped a little.
                world += new Vector2(Gaussian(random), Gaussian(random)) * 0.2f;
                sinceStep = -1f;
            }
            Vector2 jitter = new Vector2(Gaussian(random), Gaussian(random)) * 0.01f;
            log.Add(Row("a", t, 1, world.x + jitter.x, CameraHeight, world.y + jitter.y, phone + yaw));

            if (options.relocalizeAt > 0f && !relocalized && walked >= options.relocalizeAt)
            {
                relocalized = true;
                log.Add(Row("l", t));
            }
            if (t >= nextCompass)
            {
                nextCompass = t + 1f / CompassRate;
                log.Add(Row("m", t, F(Mathf.Repeat(phone - compassOffset + Gaussian(random) * 6f, 360f), 1)));
            }
            if (t >= nextMeasure)
            {
                nextMeasure = t + 1f / MeasureRate;
                log.Add(Row("T", t, truth.x, truth.y));
                if (options.scans)
                {
                    Depth(random, walkable, truth, phone, yaw, scale, world, t, buffer);
                    int n = buffer.Build(t, scan);
                    line.Clear().Append("d,").Append(F(t, 3)).Append(",depth,").Append(n);
                    for (int j = 0; j < n; j++)
                        line.Append(',').Append(F(scan[j].x, 2)).Append(',').Append(F(scan[j].y, 2));
                    log.Add(line.ToString());
                }
                if (options.walls)
                    log.Add(Walls(random, walkable, walls, found, truth, phone, yaw, scale, world, t));
            }
            if (options.plates && t >= nextSight)
            {
                nextSight = t + 1f / SightRate;
                for (int k = 0; k < signs.Count; k++)
                {
                    PlanSign sign = signs[k];
                    Vector2 to = sign.Position - truth;
                    float range = to.magnitude;
                    if (range < 1.5f || range > MaxDepth || Mathf.Abs(Mathf.DeltaAngle(phone, PositionFilter.Heading(to))) > SignView)
                        continue;
                    if (Vector2.Dot(-to, sign.Normal) < 0.3f * range || !walkable.SegmentClear(truth, sign.Position + sign.Normal * 0.5f))
                        continue;
                    if (lastSight.TryGetValue(k, out double last) && t - last < 3.0 || random.NextDouble() > SignChance)
                        continue;
                    lastSight[k] = t;
                    bool measured = random.NextDouble() < 0.5;
                    float sigma = measured ? 0.1f + 0.05f * range : 0.3f * range;
                    float worldRange = range * scale * (measured ? 1f + Gaussian(random) * 0.04f : 1.1f + Gaussian(random) * 0.15f);
                    float bearing = PositionFilter.Heading(PositionFilter.ToPlan(to, -yaw)) + Gaussian(random) * 2f;
                    log.Add(Row("s", t, sign.text, world.x, world.y, F(bearing, 2), F(worldRange, 3), F(sigma, 3),
                        measured ? 1 : 0));
                }
            }
        }
        return log;
    }

    // Depth rays across the camera's view, to the nearest wall within MaxDepth, into the buffer in world space.
    static void Depth(System.Random random, Walkable walkable, Vector2 truth, float phone, float yaw, float scale,
        Vector2 world, double t, ScanBuffer buffer)
    {
        for (int r = 0; r < Rays; r++)
        {
            float heading = phone + (r / (float)(Rays - 1) - 0.5f) * FieldOfView + Gaussian(random) * 0.5f;
            Vector2 direction = PositionFilter.HeadingVector(heading);
            float hit = Cast(walkable, truth, direction, MaxDepth);
            if (hit < 0f)
                continue;
            float range = hit + Gaussian(random) * DepthNoise;
            if (random.NextDouble() < OutlierShare)
                range = Uniform(random, 0.4f, hit);   // someone or something in the way
            if (range < 0.4f)
                continue;
            Vector2 plan = direction * range;
            buffer.Add(t, world + PositionFilter.ToPlan(plan, -yaw) * scale);
        }
    }

    // Distance along a ray to the first non-walkable cell, or -1 if none within max.
    static float Cast(Walkable walkable, Vector2 from, Vector2 direction, float max)
    {
        for (float d = 0.05f; d <= max; d += 0.05f)
        {
            if (!walkable.IsWalkable(from + direction * d))
                return d;
        }
        return -1f;
    }

    static string Walls(System.Random random, Walkable walkable, List<(Vector2 a, Vector2 b)> walls, bool[] found,
        Vector2 truth, float phone, float yaw, float scale, Vector2 world, double t)
    {
        var line = new StringBuilder("p,").Append(F(t, 3));
        int count = 0;
        var body = new StringBuilder();
        for (int k = 0; k < walls.Count; k++)
        {
            if (!found[k])
                continue;
            Vector2 a = walls[k].a, b = walls[k].b;
            Vector2 near = Router.ClosestOnSegment(truth, a, b);
            if (Vector2.Distance(truth, near) > WallRange ||
                Mathf.Abs(Mathf.DeltaAngle(phone, PositionFilter.Heading(near - truth))) > WallView)
                continue;
            Vector2 along = (b - a).normalized;
            Vector2 normal = new Vector2(-along.y, along.x);
            if (Vector2.Dot(truth - near, normal) < 0f)
                normal = -normal;
            if (!walkable.SegmentClear(truth, near + normal * 0.3f))
                continue;
            // The piece ARCore has seen: up to 1.5 m either side of the nearest point.
            float ta = Mathf.Max(-1.5f, -Vector2.Distance(near, a)), tb = Mathf.Min(1.5f, Vector2.Distance(near, b));
            float piece = tb - ta;
            if (piece < 0.8f)
                continue;
            Vector2 centre = near + along * ((ta + tb) * 0.5f) + new Vector2(Gaussian(random), Gaussian(random)) * 0.03f;
            float turn = Gaussian(random) * 2f;
            Vector2 d = PositionFilter.ToPlan(PositionFilter.ToPlan(along, turn), -yaw);
            Vector2 n = PositionFilter.ToPlan(PositionFilter.ToPlan(normal, turn), -yaw);
            Vector2 c = world + PositionFilter.ToPlan(centre - truth, -yaw) * scale;
            body.Append(',').Append(F(c.x, 3)).Append(',').Append(F(c.y, 3)).Append(',').Append(F(d.x, 4)).Append(',')
                .Append(F(d.y, 4)).Append(',').Append(F(n.x, 4)).Append(',').Append(F(n.y, 4)).Append(',')
                .Append(F(piece * scale, 2));
            count++;
        }
        return line.Append(',').Append(count).Append(body).ToString();
    }

    static string Row(string kind, double t, params object[] values)
    {
        var line = new StringBuilder(kind).Append(',').Append(F(t, 3));
        foreach (object v in values)
        {
            line.Append(',');
            line.Append(v is float f ? F(f, 3) : v is double d ? F(d, 3) : Convert.ToString(v, CultureInfo.InvariantCulture));
        }
        return line.ToString();
    }

    static string F(double v, int decimals) => v.ToString("F" + decimals, CultureInfo.InvariantCulture);

    static float Uniform(System.Random random, float a, float b) => a + (float)random.NextDouble() * (b - a);

    static float Gaussian(System.Random random)
    {
        double u = 1.0 - random.NextDouble(), v = random.NextDouble();
        return (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v));
    }
}
