using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

// SyntheticWalks, continued: stress walks in a world that isn't the plan. The building the walk happens in is the
// plan scaled and turned a little, with some walls a few centimetres off, some doors shut that the plan has open
// (and the other way), furniture the plan doesn't have, glass that depth sees through, and people walking about.
// ARCore drifts in heading and scale, jumps and loses tracking; depth is noisy, biased, patchy and sometimes absent;
// the user walks at their own pace, stops, turns back, takes wrong turns, lowers the phone and looks at the screen;
// plates are read only from the front and up close, sometimes wrongly, sometimes when there's no plate at all; and
// the snap is off by up to a metre and 30 degrees, now and then at the wrong entrance or a few metres in. Every knob
// comes in three tiers (easy, typical, harsh), each seeded on its own. The truth is in plan coordinates (where the
// user is on the map), written as T rows with the header listing the knobs and the events, for finding out why a
// walk went wrong. Events (E,t,what: jump, loss, regain, misread, falseplate, lowered) are marked for that too;
// the evaluator skips them.
public static partial class SyntheticWalks
{
    public enum Tier { Easy, Typical, Harsh }

    // The floor as the generator needs it: the plan's walkable map, distance field, walls, signs, doors and routes.
    public sealed class Floor
    {
        public Walkable walkable;
        public DistanceField field;
        public List<(Vector2 a, Vector2 b)> walls;
        public List<PlanSign> signs;
        public Vector2[] doorSpots, doorDirections;
        public Vector2[] exitSpots, exitDirections;
        public List<Vector2[]> routes;
    }

    // What a stress walk was like: the knobs and what happened, for clustering the bad ones.
    public sealed class StressInfo
    {
        public Tier tier;
        public float planScale, planTurn, arScale, arDrift, speed, lowered, depthMissing, depthBias, misread;
        public int wallOffsets, doorsFlipped, clutter, people, jumps, losses, misreads, falsePlates, stops, uTurns, wrongTurns;
        public bool wrongSnap, insideSnap;
        public float snapError, snapTurn, length, lostSeconds;

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "tier={0} length={1:F0} planScale={2:F3} planTurn={3:F2} wallOffsets={4} doorsFlipped={5} clutter={6} " +
                "people={7} arScale={8:F3} arDrift={9:F1} jumps={10} losses={11} lostSeconds={12:F0} speed={13:F2} " +
                "lowered={14:F2} depthMissing={15:F2} depthBias={16:F3} misreads={17} falsePlates={18} stops={19} " +
                "uTurns={20} wrongTurns={21} wrongSnap={22} insideSnap={23} snapError={24:F2} snapTurn={25:F0}",
                tier, length, planScale, planTurn, wallOffsets, doorsFlipped, clutter, people, arScale, arDrift, jumps,
                losses, lostSeconds, speed, lowered, depthMissing, depthBias, misreads, falsePlates, stops, uTurns,
                wrongTurns, wrongSnap ? 1 : 0, insideSnap ? 1 : 0, snapError, snapTurn);
        }
    }

    // The knobs of one tier: each a range sampled per walk.
    struct Knobs
    {
        public float planScale, planTurn;              // ± fractions and degrees
        public int wallOffsets, doorFlips, clutter;    // up to
        public bool tables;                            // a table area in the atrium
        public int people;                             // up to
        public float glassPass;                        // share of glass that depth sees straight through
        public float noise, missingMin, missingMax, bias, noDepth;
        public float driftMin, driftMax, arScale;      // degrees per root minute; ± scale
        public float jumpsPerMinute, lossesPerMinute, lossMin, lossMax;
        public float wallMiss;
        public float speedMin, speedMax, loweredMin, loweredMax, looking;
        public float stopsPerMinute, uTurnChance, wrongTurnChance;
        public float plateRead, misread, falsePlatesPerMinute;
        public float snapError, snapTurn, wrongSnap, insideSnap;
        public float compassBias, compassNoise;
    }

    static Knobs TierKnobs(Tier tier)
    {
        switch (tier)
        {
            case Tier.Easy:
                return new Knobs
                {
                    planScale = 0.005f, planTurn = 0.3f, wallOffsets = 3, doorFlips = 1, clutter = 5, people = 2,
                    glassPass = 0.5f, noise = 0.005f, missingMin = 0.2f, missingMax = 0.3f, bias = 0.01f, noDepth = 0.02f,
                    driftMin = 0.5f, driftMax = 1f, arScale = 0.02f, jumpsPerMinute = 0.1f, lossesPerMinute = 0.1f,
                    lossMin = 1f, lossMax = 3f, wallMiss = 0.5f, speedMin = 0.8f, speedMax = 1.4f, loweredMin = 0.2f,
                    loweredMax = 0.25f, looking = 0.1f, stopsPerMinute = 0.5f, uTurnChance = 0.05f, wrongTurnChance = 0.05f,
                    plateRead = 0.7f, misread = 0.02f, falsePlatesPerMinute = 0.2f, snapError = 0.3f, snapTurn = 10f,
                    compassBias = 5f, compassNoise = 6f,
                };
            case Tier.Typical:
                return new Knobs
                {
                    planScale = 0.01f, planTurn = 0.6f, wallOffsets = 6, doorFlips = 2, clutter = 15, tables = true,
                    people = 4, glassPass = 0.6f, noise = 0.01f, missingMin = 0.3f, missingMax = 0.45f, bias = 0.02f,
                    noDepth = 0.05f, driftMin = 1f, driftMax = 2f, arScale = 0.035f, jumpsPerMinute = 0.4f,
                    lossesPerMinute = 0.6f, lossMin = 1f, lossMax = 8f, wallMiss = 0.5f, speedMin = 0.6f, speedMax = 1.6f,
                    loweredMin = 0.2f, loweredMax = 0.4f, looking = 0.2f, stopsPerMinute = 1f, uTurnChance = 0.15f,
                    wrongTurnChance = 0.15f, plateRead = 0.6f, misread = 0.05f, falsePlatesPerMinute = 0.5f,
                    snapError = 0.7f, snapTurn = 20f, wrongSnap = 0.1f, insideSnap = 0.1f, compassBias = 10f, compassNoise = 8f,
                };
            default:
                return new Knobs
                {
                    planScale = 0.015f, planTurn = 1f, wallOffsets = 10, doorFlips = 4, clutter = 30, tables = true,
                    people = 8, glassPass = 0.7f, noise = 0.01f, missingMin = 0.45f, missingMax = 0.6f, bias = 0.03f,
                    noDepth = 0.15f, driftMin = 2f, driftMax = 3f, arScale = 0.05f, jumpsPerMinute = 1f,
                    lossesPerMinute = 1f, lossMin = 2f, lossMax = 15f, wallMiss = 0.5f, speedMin = 0.6f, speedMax = 1.8f,
                    loweredMin = 0.4f, loweredMax = 0.6f, looking = 0.25f, stopsPerMinute = 1.5f, uTurnChance = 0.25f,
                    wrongTurnChance = 0.25f, plateRead = 0.45f, misread = 0.08f, falsePlatesPerMinute = 1f,
                    snapError = 1f, snapTurn = 30f, wrongSnap = 0.15f, insideSnap = 0.15f, compassBias = 15f,
                    compassNoise = 10f,
                };
        }
    }

    // The building as it really is: a grid like the walkable map (0 wall, 1 floor, 2 glass, 3 furniture), the plan
    // turned and scaled about its centre to get there, and people walking.
    sealed class World
    {
        public const byte Wall = 0, Open = 1, Glass = 2, Furniture = 3;
        public readonly Walkable map;
        public readonly byte[] cells;
        public readonly Vector2 centre;
        public readonly float scale, turn;
        public readonly List<(Vector2[] path, float speed, float offset)> people = new List<(Vector2[], float, float)>();
        public float glassPass;

        public World(Walkable map, float scale, float turn)
        {
            this.map = map;
            this.scale = scale;
            this.turn = turn;
            centre = new Vector2(map.left + map.width * map.cell * 0.5f, map.top - map.height * map.cell * 0.5f);
            cells = new byte[map.width * map.height];
            for (int j = 0; j < map.height; j++)
            {
                for (int i = 0; i < map.width; i++)
                    cells[j * map.width + i] = map.IsWalkable(ToPlan(CellCentre(i, j))) ? Open : Wall;
            }
        }

        public Vector2 CellCentre(int i, int j) => new Vector2(map.left + (i + 0.5f) * map.cell, map.top - (j + 0.5f) * map.cell);

        public Vector2 ToWorld(Vector2 plan) => centre + PositionFilter.ToPlan(plan - centre, -turn) * scale;
        public Vector2 ToPlan(Vector2 world) => centre + PositionFilter.ToPlan((world - centre) / scale, turn);
        public Vector2 DirectionToWorld(Vector2 plan) => PositionFilter.ToPlan(plan, -turn);

        public byte At(Vector2 p)
        {
            int i = Mathf.FloorToInt((p.x - map.left) / map.cell), j = Mathf.FloorToInt((map.top - p.y) / map.cell);
            return i < 0 || j < 0 || i >= map.width || j >= map.height ? Wall : cells[j * map.width + i];
        }

        public void Set(Vector2 p, byte value)
        {
            int i = Mathf.FloorToInt((p.x - map.left) / map.cell), j = Mathf.FloorToInt((map.top - p.y) / map.cell);
            if (i >= 0 && j >= 0 && i < map.width && j < map.height)
                cells[j * map.width + i] = value;
        }

        // Every cell in a rectangle (a centre, unit axis u, half lengths along u and across) set to value, where
        // the cell holds `only` (or anything, for 255).
        public void Fill(Vector2 middle, Vector2 u, float halfAlong, float halfAcross, byte value, byte only = 255)
        {
            var v = new Vector2(-u.y, u.x);
            float step = map.cell * 0.5f;
            for (float a = -halfAlong; a <= halfAlong; a += step)
            {
                for (float b = -halfAcross; b <= halfAcross; b += step)
                {
                    Vector2 p = middle + u * a + v * b;
                    if (only == 255 || At(p) == only)
                        Set(p, value);
                }
            }
        }

        public Vector2 PersonAt(int k, double t)
        {
            var (path, speed, offset) = people[k];
            float total = 0f;
            for (int i = 1; i < path.Length; i++)
                total += Vector2.Distance(path[i - 1], path[i]);
            if (total <= 0f)
                return path[0];
            // There and back along the path.
            float d = Mathf.PingPong((float)t * speed + offset, total);
            for (int i = 1; i < path.Length; i++)
            {
                float leg = Vector2.Distance(path[i - 1], path[i]);
                if (d <= leg)
                    return Vector2.Lerp(path[i - 1], path[i], leg > 0f ? d / leg : 0f);
                d -= leg;
            }
            return path[path.Length - 1];
        }

        // Metres along a ray to what depth sees (wall, furniture, a person, or glass that doesn't let it through);
        // -1 for nothing within max. passRoll: this ray's chance at glass.
        public float Cast(Vector2 from, Vector2 direction, float max, double t, float passRoll)
        {
            float hit = -1f;
            for (float d = 0.05f; d <= max; d += 0.05f)
            {
                byte c = At(from + direction * d);
                if (c == Open)
                    continue;
                if (c == Glass)
                {
                    if (passRoll < glassPass)
                        continue;   // straight through
                    return -1f;     // no return off glass
                }
                hit = d;
                break;
            }
            // People: circles of 0.25 m.
            for (int k = 0; k < people.Count; k++)
            {
                Vector2 to = PersonAt(k, t) - from;
                float along = Vector2.Dot(to, direction);
                if (along <= 0.3f || hit >= 0f && along >= hit || along > max)
                    continue;
                float across = (to - direction * along).magnitude;
                if (across < 0.25f)
                    hit = along - Mathf.Sqrt(0.0625f - across * across);
            }
            return hit;
        }

        public bool Clear(Vector2 a, Vector2 b, double t)
        {
            float length = Vector2.Distance(a, b);
            Vector2 d = (b - a) / Mathf.Max(length, 1e-4f);
            for (float s = 0.05f; s < length - 0.05f; s += 0.05f)
            {
                if (At(a + d * s) != Open)
                    return false;
            }
            for (int k = 0; k < people.Count; k++)
            {
                Vector2 to = PersonAt(k, t) - a;
                float along = Vector2.Dot(to, d);
                if (along > 0.3f && along < length - 0.3f && (to - d * along).magnitude < 0.25f)
                    return false;
            }
            return true;
        }
    }

    // One stress walk of the given tier on floor, seeded. Returns the log's lines; info says what it was like.
    public static List<string> Stress(Floor floor, Tier tier, int seed, out StressInfo info)
    {
        var random = new System.Random(seed);
        // Each group of knobs from its own seed, so changing one model doesn't reshuffle the rest.
        var planRandom = new System.Random(seed * 31 + 1);
        var arRandom = new System.Random(seed * 31 + 2);
        var depthRandom = new System.Random(seed * 31 + 3);
        var userRandom = new System.Random(seed * 31 + 4);
        var plateRandom = new System.Random(seed * 31 + 5);
        var peopleRandom = new System.Random(seed * 31 + 6);
        Knobs k = TierKnobs(tier);
        info = new StressInfo { tier = tier };

        // The route: a door to a door, or an entrance to a room.
        Vector2[] route = floor.routes[random.Next(floor.routes.Count)];

        // The world.
        info.planScale = 1f + Uniform(planRandom, -k.planScale, k.planScale);
        info.planTurn = Uniform(planRandom, -k.planTurn, k.planTurn);
        var world = new World(floor.walkable, info.planScale, info.planTurn) { glassPass = k.glassPass };
        List<Vector2> path = UserPath(route, floor.walkable, userRandom, k, info);
        BuildWorld(world, floor, planRandom, peopleRandom, k, info, path);

        // ARCore, depth and the user.
        info.arScale = 1f + Uniform(arRandom, -k.arScale, k.arScale);
        if (Mismatch.arScale != 0f)
            info.arScale = 1f + Mismatch.arScale;
        info.arDrift = Uniform(arRandom, k.driftMin, k.driftMax);
        float arYaw = Uniform(arRandom, -180f, 180f);
        info.depthMissing = Uniform(depthRandom, k.missingMin, k.missingMax);
        info.depthBias = Uniform(depthRandom, -k.bias, k.bias);
        if (Mismatch.depthBias != 0f)
            info.depthBias = Mismatch.depthBias;
        info.speed = Uniform(userRandom, k.speedMin, k.speedMax);
        info.lowered = Uniform(userRandom, k.loweredMin, k.loweredMax);
        float compassOffset = Uniform(userRandom, -180f, 180f), compassBias = Uniform(userRandom, -k.compassBias, k.compassBias);

        // The snap.
        var log = new List<string>();
        log.Add("# Doorway stress walk, seed " + seed);
        info.snapError = Mathf.Abs(Gaussian(userRandom)) * k.snapError * 0.6f;
        info.snapTurn = Uniform(userRandom, -k.snapTurn, k.snapTurn);
        info.insideSnap = userRandom.NextDouble() < k.insideSnap;
        info.wrongSnap = userRandom.NextDouble() < k.wrongSnap;
        Vector2 start = path[0];
        Vector2 away = (path.Count > 1 ? path[1] - path[0] : Vector2.up).normalized;
        Vector2 spot = route[0];
        // The app asks for the door at the user's back (Navigator.DoorPose): its away direction, if route[0] is a door.
        Vector2 doorAway = away;
        for (int d = 0; d < floor.doorSpots.Length; d++)
            if ((floor.doorSpots[d] - spot).sqrMagnitude < 0.01f)
                doorAway = -floor.doorDirections[d].normalized;
        Vector2 spotAway = doorAway;
        int startLeg = 1;
        if (info.insideSnap)
        {
            // Snapped a few metres in: the user stands 3 m along the route but says they're at its door.
            start = Along(path, 3f, out startLeg);
        }
        if (info.wrongSnap)
        {
            // The wrong door: another entrance GPS can't tell from this one if this is one (SnapAlternatives: BA1's 7
            // entrances are 15.7 to 71.5 m apart, and the pairs under ConfusableEntrance are 15.7, 18.2 and 18.3 m),
            // else another door nearby.
            List<int> choices = SnapAlternatives(floor, spot);
            if (choices.Count > 0)
            {
                int d = choices[random.Next(choices.Count)];
                spot = floor.doorSpots[d];
                spotAway = -floor.doorDirections[d];
            }
            else
            {
                info.wrongSnap = false;
            }
        }
        float postureAngle = Uniform(userRandom, 0f, 360f);
        Vector2 truth = start + PositionFilter.HeadingVector(postureAngle) * info.snapError;
        if (!floor.walkable.IsWalkable(truth))
            truth = start;
        // The plan's yaw in the AR world: the world's turn plus ARCore's, and the snap's posture error on top.
        float trueYaw = info.planTurn + arYaw;
        // Where the phone really faces (plan): the door at the back, give or take; already walking if snapped inside.
        float facing = PositionFilter.Heading(info.insideSnap ? away : doorAway) + info.snapTurn;
        float baseYaw = facing + trueYaw - PositionFilter.Heading(spotAway);
        Vector2 ar = new Vector2(Uniform(arRandom, -3f, 3f), Uniform(arRandom, -3f, 3f));
        double t = 0;
        // The other doors the snap might have been at, as the Localizer offers them (SnapAlternatives).
        var snapLine = new StringBuilder(Row("n", t, "start", spot.x, spot.y, F(baseYaw, 2), ar.x, ar.y));
        List<int> alternatives = SnapAlternatives(floor, spot);
        snapLine.Append(',').Append(alternatives.Count);
        foreach (int d in alternatives)
        {
            float yaw = facing + trueYaw - PositionFilter.Heading(-floor.doorDirections[d]);
            snapLine.Append(',').Append(F(floor.doorSpots[d].x, 3)).Append(',').Append(F(floor.doorSpots[d].y, 3))
                .Append(',').Append(F(yaw, 2));
        }
        // The camera's track before the tap (as Localizer buffers it): standing at the door; or, tapped a few steps
        // in, walking in from TrackOutside metres outside the door along the user's path to where they stand.
        var track = new List<Vector2>();
        var trackRandom = new System.Random(seed * 7 + 3);   // its own: the other streams stay as they were
        if (info.insideSnap)
        {
            var walkIn = new List<Vector2> { path[0] - doorAway * TrackOutside };
            for (int i = 0; i < startLeg; i++)
                walkIn.Add(path[i]);
            walkIn.Add(truth);
            float total = 0f;
            for (int i = 1; i < walkIn.Count; i++)
                total += Vector2.Distance(walkIn[i - 1], walkIn[i]);
            for (double back = TrackSeconds; back >= 0.0; back -= TrackStep)
                track.Add(AlongBack(walkIn, total, (float)back * info.speed));
        }
        else
        {
            for (double back = TrackSeconds; back >= 0.0; back -= TrackStep)
                track.Add(truth + new Vector2(Gaussian(trackRandom), Gaussian(trackRandom)) * 0.02f);
        }
        snapLine.Append(',').Append(track.Count);
        for (int i = 0; i < track.Count; i++)
        {
            Vector2 arPoint = ar + PositionFilter.ToPlan(world.ToWorld(track[i]) - world.ToWorld(truth), -arYaw) * info.arScale;
            snapLine.Append(',').Append(F(-(TrackSeconds - i * TrackStep), 2)).Append(',').Append(F(arPoint.x, 3))
                .Append(',').Append(F(arPoint.y, 3));
        }
        log.Add(snapLine.ToString());
        // The route being guided (r,t,k,(x,z)*k): the truth's companion, for scoring guidance near turns.
        var routeLine = new StringBuilder(Row("r", t, route.Length));
        foreach (Vector2 p in route)
            routeLine.Append(',').Append(F(p.x, 3)).Append(',').Append(F(p.y, 3));
        log.Add(routeLine.ToString());
        log.Add(Row("c", t, "start", truth.x, truth.y));

        // Walk.
        var buffer = new ScanBuffer();
        var scan = new Vector2[ScanBuffer.Target];
        float dt = 1f / PoseRate;
        double nextMeasure = 0, nextCompass = 0, nextPlate = 0, stopUntil = -1, lostUntil = -1, phoneUntil = 0;
        int phone = 0;   // 0 up, 1 looking at the screen, 2 lowered
        float sinceStep = 0f, lastFacing = facing, look = 0f;
        double lookUntil = -1;
        var lastRead = new Dictionary<int, double>();
        int leg = startLeg;
        Vector2 position = truth;
        bool tracking = true;
        while (leg < path.Count)
        {
            t += dt;
            // Stops now and then.
            if (t > stopUntil && userRandom.NextDouble() < k.stopsPerMinute / 60f * dt)
            {
                stopUntil = t + Uniform(userRandom, 2f, 10f);
                info.stops++;
            }
            float move = t < stopUntil ? 0f : info.speed * dt;
            Vector2 before = position;
            while (move > 0f && leg < path.Count)
            {
                Vector2 to = path[leg] - position;
                if (to.magnitude <= move)
                {
                    move -= to.magnitude;
                    position = path[leg];
                    leg++;
                }
                else
                {
                    position += to.normalized * move;
                    move = 0f;
                }
            }
            Vector2 step = position - before;
            if (step.sqrMagnitude > 1e-8f)
                facing = Mathf.MoveTowardsAngle(facing, PositionFilter.Heading(step), 180f * dt);
            if (t > lookUntil && userRandom.NextDouble() < 0.1f * dt)
            {
                look = Uniform(userRandom, -70f, 70f);
                lookUntil = t + Uniform(userRandom, 1f, 3f);
            }
            float phoneHeading = facing + (t < lookUntil ? look : 0f) + Gaussian(userRandom) * 3f;
            float turnRate = Mathf.Abs(Mathf.DeltaAngle(lastFacing, phoneHeading)) / dt;
            lastFacing = phoneHeading;

            // How the phone is held.
            if (t >= phoneUntil)
            {
                double r = userRandom.NextDouble();
                int was = phone;
                phone = r < info.lowered ? 2 : r < info.lowered + k.looking ? 1 : 0;
                if (phone != was)
                    log.Add(Row("E", t, phone == 2 ? "lowered" : phone == 1 ? "looking" : "up"));
                phoneUntil = t + Uniform(userRandom, 3f, 15f);
            }

            // ARCore: drift, scale, jumps, losses (more when walking fast or turning hard).
            float strain = 1f + (info.speed > 1.4f ? 1f : 0f) + (turnRate > 90f ? 2f : 0f);
            arYaw += Gaussian(arRandom) * info.arDrift * Mathf.Sqrt(dt / 60f);
            Vector2 worldStep = world.DirectionToWorld(step) * world.scale;
            ar += PositionFilter.ToPlan(worldStep, -arYaw) * info.arScale;
            if (tracking && arRandom.NextDouble() < k.jumpsPerMinute / 60f * dt * strain)
            {
                ar += PositionFilter.HeadingVector(Uniform(arRandom, 0f, 360f)) * Uniform(arRandom, 0.3f, 2f);
                info.jumps++;
                log.Add(Row("E", t, "jump"));
            }
            if (tracking && arRandom.NextDouble() < k.lossesPerMinute / 60f * dt * strain)
            {
                lostUntil = t + Uniform(arRandom, k.lossMin, k.lossMax);
                info.losses++;
                log.Add(Row("E", t, "loss"));
            }
            bool wasTracking = tracking;
            tracking = t >= lostUntil;
            if (!tracking)
                info.lostSeconds += dt;
            if (tracking && !wasTracking)
            {
                ar += PositionFilter.HeadingVector(Uniform(arRandom, 0f, 360f)) * Uniform(arRandom, 0f, 0.5f);
                log.Add(Row("E", t, "regain"));
            }
            float cameraYaw = phoneHeading + info.planTurn + arYaw;
            log.Add(Row("a", t, tracking ? 1 : 0, ar.x, CameraHeight, ar.y, F(cameraYaw, 2)));

            // Steps (always counted; used only while ARCore can't track) and the compass.
            sinceStep += step.magnitude;
            // A compass bias that drifts (the building's steel) by Mismatch.compassDrift degrees over 60 s, back and forth.
            float drift = Mismatch.compassDrift * Mathf.Sin((float)t * 2f * Mathf.PI / 120f);
            float compass = Mathf.Repeat(phoneHeading - compassOffset + compassBias + drift + Gaussian(userRandom) * k.compassNoise, 360f);
            if (sinceStep >= Mismatch.stepLength)
            {
                sinceStep -= Mismatch.stepLength;
                log.Add(Row("k", t, F(compass, 1), 0f));
            }
            if (tracking && t >= nextCompass)
            {
                nextCompass = t + 1f / CompassRate;
                log.Add(Row("m", t, F(compass, 1)));
            }

            if (t >= nextMeasure)
            {
                nextMeasure = t + 1f / MeasureRate;
                log.Add(Row("T", t, position.x, position.y, F(phoneHeading, 1)));
                if (tracking && phone != 2)
                {
                    StressDepth(world, depthRandom, k, info, position, phoneHeading, phone == 1, trueYaw, arYaw, ar, t, buffer);
                    int n = buffer.Build(t, scan);
                    var line = new StringBuilder("d,").Append(F(t, 3)).Append(",depth,").Append(n);
                    for (int j = 0; j < n; j++)
                        line.Append(',').Append(F(scan[j].x, 2)).Append(',').Append(F(scan[j].y, 2));
                    log.Add(line.ToString());
                    if (phone == 0)
                        log.Add(StressWalls(world, floor, arRandom, k, position, phoneHeading, arYaw, ar, t));
                }
            }

            // Plates: from the front, up close, the phone up; sometimes wrong, sometimes nothing there.
            if (tracking && phone == 0 && t >= nextPlate)
            {
                nextPlate = t + 1f / SightRate;
                StressPlates(world, floor, plateRandom, k, info, position, phoneHeading, arYaw, ar, t, lastRead, log);
            }
        }
        log.Add(Row("c", t, "end", position.x, position.y));
        info.length = 0f;
        for (int i = 1; i < path.Count; i++)
            info.length += Vector2.Distance(path[i - 1], path[i]);
        log.Insert(1, "# " + info);
        return log;
    }

    /// <summary>Within this of a door snapped at, other doors are offered as where the snap may really have been.</summary>
    public const float AlternativeReach = 12f;

    // The doors a snap at spot might really have been at: the other entrances if spot is one, else the doors within
    // AlternativeReach (as indices into floor.doorSpots). Mirrors Localizer.Alternatives.
    /// <summary>Routes between door pairs (seeded, so always the same), 25 m or longer, and from the entrances.</summary>
    public static void Routes(FloorPlan plan, Walkable walkable, out List<Vector2[]> routes, out List<Vector2[]> entranceRoutes)
    {
        var doors = plan.places.SelectMany(p => p.doors).ToList();
        var router = new Router(plan, walkable);
        var random = new System.Random(4);
        routes = new List<Vector2[]>();
        for (int tries = 0; routes.Count < 30 && tries < 2000; tries++)
        {
            PlanDoor from = doors[random.Next(doors.Count)];
            PlanPlace to = plan.places[random.Next(plan.places.Count)];
            Route route = router.FindRoute(from.node, to);
            if (route != null && route.length >= 25f)
                routes.Add(route.points.ToArray());
        }
        entranceRoutes = new List<Vector2[]>();
        var exits = plan.places.Where(p => p.kind == "exit").SelectMany(p => p.doors).ToList();
        for (int tries = 0; exits.Count > 0 && entranceRoutes.Count < 12 && tries < 2000; tries++)
        {
            PlanDoor from = exits[random.Next(exits.Count)];
            Route route = router.FindRoute(from.node, plan.places[random.Next(plan.places.Count)]);
            if (route != null && route.length >= 25f)
                entranceRoutes.Add(route.points.ToArray());
        }
    }

    /// <summary>The floor for stress walks: every door with its direction (spot to door), the exits among them.</summary>
    public static Floor StressFloor(FloorPlan plan, Walkable walkable, DistanceField field,
        List<(Vector2 a, Vector2 b)> walls, List<Vector2[]> routes)
    {
        var doors = plan.places.SelectMany(p => p.doors.Select(door => (exit: p.kind == "exit", door))).ToList();
        var exits = doors.Where(d => d.exit).ToList();
        return new Floor
        {
            walkable = walkable, field = field, walls = walls, signs = plan.signs,
            doorSpots = doors.Select(d => plan.NodePosition(d.door.node)).ToArray(),
            doorDirections = doors.Select(d => d.door.Direction).ToArray(),
            exitSpots = exits.Select(d => plan.NodePosition(d.door.node)).ToArray(),
            exitDirections = exits.Select(d => d.door.Direction).ToArray(),
            routes = routes,
        };
    }

    /// <summary>The world worse than the filter assumes, on top of every tier (sensitivity analysis): depth noise
    /// and false plates as multiples, depth bias and ARCore's scale error as fixed fractions (0: the tier's own), the
    /// compass bias drifting this many degrees, the plan's wall offsets as multiples (of 10 to 30 cm, and of how many
    /// walls), and the user's
    /// real step length (the engine assumes 0.7 m).</summary>
    public struct MismatchKnobs
    {
        public float depthNoise, depthBias, arScale, compassDrift, falsePlates, wallError, wallCount, stepLength;
        public static MismatchKnobs None => new MismatchKnobs
            { depthNoise = 1f, falsePlates = 1f, wallError = 1f, wallCount = 1f, stepLength = 0.7f };
    }

    public static MismatchKnobs Mismatch = MismatchKnobs.None;

    /// <summary>Entrances further apart than this, metres, GPS tells apart before the user is in.</summary>
    public const float ConfusableEntrance = 25f;

    public static List<int> SnapAlternatives(Floor floor, Vector2 spot)
    {
        bool entrance = false;
        foreach (Vector2 e in floor.exitSpots)
            entrance |= (e - spot).sqrMagnitude < 0.01f;
        var doors = new List<int>();
        for (int d = 0; d < floor.doorSpots.Length; d++)
        {
            Vector2 p = floor.doorSpots[d];
            float distance = Vector2.Distance(p, spot);
            if (distance < 0.5f)
                continue;
            bool isExit = false;
            foreach (Vector2 e in floor.exitSpots)
                isExit |= (e - p).sqrMagnitude < 0.01f;
            if (distance <= AlternativeReach || entrance && isExit && distance <= ConfusableEntrance)
                doors.Add(d);
        }
        return doors;
    }

    // The way the user really walks a route: back and forth now and then (a U-turn), and a few metres down a wrong
    // hallway and back (a wrong turn), where the floor allows.
    static List<Vector2> UserPath(Vector2[] route, Walkable walkable, System.Random random, Knobs k, StressInfo info)
    {
        var path = new List<Vector2> { route[0] };
        for (int i = 1; i < route.Length; i++)
        {
            Vector2 a = route[i - 1], b = route[i];
            float leg = Vector2.Distance(a, b);
            if (leg > 6f && random.NextDouble() < k.uTurnChance)
            {
                Vector2 middle = Vector2.Lerp(a, b, Uniform(random, 0.4f, 0.8f));
                Vector2 back = middle - (b - a).normalized * Mathf.Min(Uniform(random, 3f, 6f), leg * 0.3f);
                path.Add(middle);
                path.Add(back);
                path.Add(middle);
                info.uTurns++;
            }
            path.Add(b);
            if (i < route.Length - 1 && random.NextDouble() < k.wrongTurnChance)
            {
                Vector2 along = (b - a).normalized;
                Vector2 side = random.NextDouble() < 0.5 ? new Vector2(-along.y, along.x) : new Vector2(along.y, -along.x);
                foreach (Vector2 way in new[] { side, along, -side })
                {
                    if (Vector2.Dot((route[i + 1] - b).normalized, way) > 0.9f)
                        continue;   // that's the right way, not a wrong turn
                    if (walkable.SegmentClear(b, b + way * 4f))
                    {
                        path.Add(b + way * 4f);
                        path.Add(b);
                        info.wrongTurns++;
                        break;
                    }
                }
            }
        }
        return path;
    }

    static void BuildWorld(World world, Floor floor, System.Random random, System.Random peopleRandom, Knobs k,
        StressInfo info, List<Vector2> path)
    {
        var worldPath = new List<Vector2>();
        foreach (Vector2 p in path)
            worldPath.Add(world.ToWorld(p));
        bool NearPath(Vector2 p, float distance)
        {
            for (int i = 1; i < worldPath.Count; i++)
            {
                if (Vector2.Distance(p, Router.ClosestOnSegment(p, worldPath[i - 1], worldPath[i])) < distance)
                    return true;
            }
            return false;
        }

        // Walls a little off where the plan has them: pushed out into the room or back into the wall.
        int offsets = Mathf.RoundToInt(random.Next(k.wallOffsets + 1) * Mismatch.wallCount);
        for (int n = 0; n < offsets; n++)
        {
            var (a, b) = floor.walls[random.Next(floor.walls.Count)];
            Vector2 wa = world.ToWorld(a), wb = world.ToWorld(b);
            Vector2 u = (wb - wa).normalized, v = new Vector2(-u.y, u.x);
            Vector2 middle = (wa + wb) * 0.5f;
            // The open side: the one with floor.
            if (world.At(middle + v * 0.2f) != World.Open)
                v = -v;
            if (world.At(middle + v * 0.2f) != World.Open)
                continue;
            float offset = Uniform(random, 0.1f, 0.3f) * Mismatch.wallError, half = Vector2.Distance(wa, wb) * 0.5f;
            if (random.NextDouble() < 0.5)
            {
                if (NearPath(middle + v * offset, 0.7f))
                    continue;
                world.Fill(middle + v * (offset * 0.5f), u, half, offset * 0.5f, World.Wall, World.Open);
            }
            else
            {
                world.Fill(middle - v * (offset * 0.5f + 0.02f), u, half, offset * 0.5f, World.Open, World.Wall);
            }
            info.wallOffsets++;
        }

        // Doors the plan has open, shut; walls the plan has, with a doorway in them.
        int flips = random.Next(k.doorFlips + 1);
        for (int n = 0; n < flips; n++)
        {
            if (random.NextDouble() < 0.5)
            {
                int d = random.Next(floor.doorSpots.Length);
                Vector2 spot = world.ToWorld(floor.doorSpots[d]), dir = world.DirectionToWorld(floor.doorDirections[d]).normalized;
                if (NearPath(spot + dir, 0.5f))
                    continue;
                world.Fill(spot + dir * 0.9f, new Vector2(-dir.y, dir.x), 0.5f, 0.08f, World.Wall);
            }
            else
            {
                var (a, b) = floor.walls[random.Next(floor.walls.Count)];
                Vector2 wa = world.ToWorld(a), wb = world.ToWorld(b);
                if (Vector2.Distance(wa, wb) < 1.2f)
                    continue;
                world.Fill((wa + wb) * 0.5f, (wb - wa).normalized, 0.45f, 0.35f, World.Open, World.Wall);
            }
            info.doorsFlipped++;
        }

        // Glass: round the atrium (the open floor furthest from walls) and by the entrances.
        var nearAtrium = new HashSet<long>();
        Walkable map = world.map;
        for (int j = 0; j < map.height; j += 5)
        {
            for (int i = 0; i < map.width; i += 5)
            {
                Vector2 p = world.CellCentre(i, j);
                if (world.At(p) == World.Open && floor.field.Distance(world.ToPlan(p)) >= 2.2f)
                {
                    int ci = Mathf.FloorToInt(p.x), cj = Mathf.FloorToInt(p.y);
                    for (int di = -4; di <= 4; di++)
                        for (int dj = -4; dj <= 4; dj++)
                            nearAtrium.Add(((long)(ci + di) << 32) ^ (uint)(cj + dj));
                }
            }
        }
        var exits = new List<Vector2>();
        foreach (Vector2 e in floor.exitSpots)
            exits.Add(world.ToWorld(e));
        for (int j = 1; j < map.height - 1; j++)
        {
            for (int i = 1; i < map.width - 1; i++)
            {
                int c = j * map.width + i;
                if (world.cells[c] != World.Wall)
                    continue;
                // Only a wall's face: next to floor.
                if (world.cells[c - 1] != World.Open && world.cells[c + 1] != World.Open &&
                    world.cells[c - map.width] != World.Open && world.cells[c + map.width] != World.Open)
                    continue;
                Vector2 p = world.CellCentre(i, j);
                bool glass = nearAtrium.Contains(((long)Mathf.FloorToInt(p.x) << 32) ^ (uint)Mathf.FloorToInt(p.y));
                if (!glass)
                {
                    foreach (Vector2 e in exits)
                    {
                        if ((p - e).sqrMagnitude < 16f)
                        {
                            glass = true;
                            break;
                        }
                    }
                }
                if (glass)
                    world.cells[c] = World.Glass;
            }
        }

        // Furniture: benches, bins and tables; and tables across the atrium.
        int clutter = random.Next(k.clutter + 1);
        for (int n = 0, tries = 0; n < clutter && tries < clutter * 20; tries++)
        {
            Vector2 p = world.CellCentre(random.Next(map.width), random.Next(map.height));
            if (world.At(p) != World.Open || NearPath(p, 1.3f))
                continue;
            float angle = Uniform(random, 0f, 180f);
            world.Fill(p, PositionFilter.HeadingVector(angle), Uniform(random, 0.2f, 0.75f), Uniform(random, 0.2f, 0.5f),
                World.Furniture, World.Open);
            info.clutter++;
            n++;
        }
        if (k.tables)
        {
            for (int j = 0; j < map.height; j += 25)
            {
                for (int i = 0; i < map.width; i += 25)
                {
                    Vector2 p = world.CellCentre(i, j) + new Vector2(Uniform(random, -0.5f, 0.5f), Uniform(random, -0.5f, 0.5f));
                    if (world.At(p) != World.Open || floor.field.Distance(world.ToPlan(p)) < 2.2f || NearPath(p, 1.3f))
                        continue;
                    world.Fill(p, Vector2.right, 0.4f, 0.4f, World.Furniture, World.Open);
                    info.clutter++;
                }
            }
        }

        // People walking routes of their own.
        int people = peopleRandom.Next(k.people + 1);
        for (int n = 0; n < people; n++)
        {
            Vector2[] r = floor.routes[peopleRandom.Next(floor.routes.Count)];
            var wr = new Vector2[r.Length];
            for (int i = 0; i < r.Length; i++)
                wr[i] = world.ToWorld(r[i]);
            world.people.Add((wr, Uniform(peopleRandom, 1f, 1.4f), Uniform(peopleRandom, 0f, 100f)));
        }
        info.people = people;
    }

    // One depth frame: 40 columns across 60 degrees from the truth in the world, with range noise growing as range
    // squared, a bias, missing pixels, flying pixels at edges, and nothing at all now and then. Looking at the
    // screen, the camera points at the floor a metre or two ahead: only walls that close show above floor height.
    static void StressDepth(World world, System.Random random, Knobs k, StressInfo info, Vector2 truth, float heading,
        bool looking, float trueYaw, float arYaw, Vector2 ar, double t, ScanBuffer buffer)
    {
        if (random.NextDouble() < k.noDepth)
            return;
        Vector2 eye = world.ToWorld(truth);
        float max = looking ? 1.6f : MaxDepth;
        float previous = -1f;
        for (int c = 0; c < Rays; c++)
        {
            float h = heading + (c / (float)(Rays - 1) - 0.5f) * FieldOfView;
            Vector2 direction = world.DirectionToWorld(PositionFilter.HeadingVector(h));
            float hit = world.Cast(eye, direction, max, t, (float)random.NextDouble());
            float range = hit;
            // Flying pixels: at an edge (the next column much further or nearer), a point in between.
            if (hit > 0f && previous > 0f && Mathf.Abs(hit - previous) > 0.8f && random.NextDouble() < 0.3)
                range = (hit + previous) * 0.5f;
            previous = hit;
            if (range < 0f || random.NextDouble() < info.depthMissing)
                continue;
            range = range * (1f + info.depthBias) + Gaussian(random) * 0.01f * Mismatch.depthNoise * range * range;
            if (range < 0.4f || range > MaxDepth)
                continue;
            // From the world to ARCore's world: turned, scaled, offset as ARCore sees it now.
            Vector2 offset = direction * range;
            Vector2 arOffset = PositionFilter.ToPlan(offset, -arYaw) * info.arScale;
            buffer.Add(t, ar + arOffset);
        }
    }

    // The walls ARCore has found within 4 m in view (about half the plain ones), and furniture faces now and then.
    static string StressWalls(World world, Floor floor, System.Random random, Knobs k, Vector2 truth, float heading,
        float arYaw, Vector2 ar, double t)
    {
        var line = new StringBuilder("p,").Append(F(t, 3));
        var body = new StringBuilder();
        int count = 0;
        Vector2 eye = world.ToWorld(truth);
        // Walls ARCore misses stay missed for the walk: the same seed per wall.
        for (int w = 0; w < floor.walls.Count; w++)
        {
            if (((w * 7919 + (int)(world.turn * 1000f)) & 1023) / 1024f < k.wallMiss)
                continue;
            Vector2 a = world.ToWorld(floor.walls[w].a), b = world.ToWorld(floor.walls[w].b);
            Vector2 near = Router.ClosestOnSegment(eye, a, b);
            if (Vector2.Distance(eye, near) > WallRange ||
                Mathf.Abs(Mathf.DeltaAngle(world.turn + heading, PositionFilter.Heading(near - eye))) > WallView)
                continue;
            Vector2 along = (b - a).normalized, normal = new Vector2(-along.y, along.x);
            if (Vector2.Dot(eye - near, normal) < 0f)
                normal = -normal;
            if (world.At(near + normal * 0.15f) != World.Open || !world.Clear(eye, near + normal * 0.3f, t))
                continue;   // moved, glass or furniture in front, or out of sight
            float ta = Mathf.Max(-1.5f, -Vector2.Distance(near, a)), tb = Mathf.Min(1.5f, Vector2.Distance(near, b));
            if (tb - ta < 0.8f)
                continue;
            Vector2 centre = near + along * ((ta + tb) * 0.5f) + new Vector2(Gaussian(random), Gaussian(random)) * 0.03f;
            float turn = Gaussian(random) * 2f;
            Vector2 c = ar + PositionFilter.ToPlan(centre - eye, -arYaw);
            Vector2 d = PositionFilter.ToPlan(PositionFilter.ToPlan(along, turn), -arYaw);
            Vector2 n = PositionFilter.ToPlan(PositionFilter.ToPlan(normal, turn), -arYaw);
            body.Append(',').Append(F(c.x, 3)).Append(',').Append(F(c.y, 3)).Append(',').Append(F(d.x, 4)).Append(',')
                .Append(F(d.y, 4)).Append(',').Append(F(n.x, 4)).Append(',').Append(F(n.y, 4)).Append(',')
                .Append(F(tb - ta, 2));
            count++;
        }
        return line.Append(',').Append(count).Append(body).ToString();
    }

    static void StressPlates(World world, Floor floor, System.Random random, Knobs k, StressInfo info, Vector2 truth,
        float heading, float arYaw, Vector2 ar, double t, Dictionary<int, double> lastRead, List<string> log)
    {
        Vector2 eye = world.ToWorld(truth);
        for (int s = 0; s < floor.signs.Count; s++)
        {
            PlanSign sign = floor.signs[s];
            Vector2 at = world.ToWorld(sign.Position), normal = world.DirectionToWorld(sign.Normal);
            Vector2 to = at - eye;
            float range = to.magnitude;
            if (range < 1f || range > 5f || Mathf.Abs(Mathf.DeltaAngle(world.turn + heading, PositionFilter.Heading(to))) > SignView)
                continue;
            // From the front: within 45 degrees of square.
            if (Vector2.Dot(-to / range, normal) < 0.707f || !world.Clear(eye, at + normal * 0.3f, t))
                continue;
            bool measured = random.NextDouble() < 0.5;
            if (!measured && range > 3f || lastRead.TryGetValue(s, out double last) && t - last < 3.0 ||
                random.NextDouble() > k.plateRead)
                continue;
            lastRead[s] = t;
            string text = sign.text;
            if (random.NextDouble() < k.misread)
            {
                text = Misread(floor.signs, text, random);
                info.misreads++;
                log.Add(Row("E", t, "misread"));
            }
            float sigma = measured ? 0.1f + 0.05f * range : 0.3f * range;
            float arRange = range * info.arScale * (measured ? 1f + Gaussian(random) * 0.04f : 1.1f + Gaussian(random) * 0.15f);
            float bearing = PositionFilter.Heading(PositionFilter.ToPlan(to, -arYaw)) + Gaussian(random) * 2f;
            log.Add(Row("s", t, text, ar.x, ar.y, F(bearing, 2), F(arRange, 3), F(sigma, 3), measured ? 1 : 0));
        }
        // A plate read where there's none (a poster, a sign for something else).
        if (random.NextDouble() < k.falsePlatesPerMinute * Mismatch.falsePlates / 60f / SightRate)
        {
            string text = floor.signs[random.Next(floor.signs.Count)].text;
            float bearing = heading + world.turn + arYaw + Uniform(random, -25f, 25f);
            float range = Uniform(random, 1.5f, 4f);
            log.Add(Row("s", t, text, ar.x, ar.y, F(bearing, 2), F(range, 3), F(0.3f * range, 3), 0));
            info.falsePlates++;
            log.Add(Row("E", t, "falseplate"));
        }
    }

    // A plausible wrong reading: another plate's text that differs in one character, if there is one.
    static string Misread(List<PlanSign> signs, string text, System.Random random)
    {
        var near = new List<string>();
        foreach (PlanSign s in signs)
        {
            if (s.text == text || s.text.Length != text.Length)
                continue;
            int differ = 0;
            for (int i = 0; i < text.Length; i++)
                differ += s.text[i] != text[i] ? 1 : 0;
            if (differ == 1)
                near.Add(s.text);
        }
        return near.Count > 0 ? near[random.Next(near.Count)] : signs[random.Next(signs.Count)].text;
    }

    // The camera's track before a snap: TrackSeconds of it, a point every TrackStep; walking in from TrackOutside metres
    // outside the door.
    const double TrackSeconds = 8.0, TrackStep = 0.1;
    const float TrackOutside = 2f;

    // The point back metres before the end of a polyline total metres long (its start, further back than that).
    static Vector2 AlongBack(List<Vector2> line, float total, float back)
    {
        float distance = Mathf.Max(0f, total - back);
        for (int i = 1; i < line.Count; i++)
        {
            float l = Vector2.Distance(line[i - 1], line[i]);
            if (distance <= l)
                return Vector2.Lerp(line[i - 1], line[i], l > 0f ? distance / l : 0f);
            distance -= l;
        }
        return line[line.Count - 1];
    }

    static Vector2 Along(List<Vector2> path, float distance, out int leg)
    {
        for (leg = 1; leg < path.Count; leg++)
        {
            float l = Vector2.Distance(path[leg - 1], path[leg]);
            if (distance <= l)
                return Vector2.Lerp(path[leg - 1], path[leg], l > 0f ? distance / l : 0f);
            distance -= l;
        }
        leg = path.Count;
        return path[path.Count - 1];
    }
}
