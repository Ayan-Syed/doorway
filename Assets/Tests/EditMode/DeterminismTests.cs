using System.Linq;
using NUnit.Framework;

// The harness outside Unity (the simulation numbers in the reports) must agree with the editor. Floating point differs
// between .NET runtimes, so it runs on the editor's own Mono with the plan's numbers read as the editor's JsonUtility
// reads them; then the floor, the distance field and the generated walk agree bit for bit (ReplayEvaluator.
// Fingerprint, below). The localizer's estimates still part (the editor's JIT computes some float steps differently,
// and the phone runs IL2CPP anyway), so the localizer is held to agree statistically: the editor's 40-walk typical
// aggregate must fall within the band the harness gives over eight pairs of localizer seeds (mean ± about 4 sd).
public class DeterminismTests
{
    // Seed 253, typical tier, ARCore measuring 8% short.
    const string Floor = "floor B64E8412B299125D", Field = "field 74709097D69277FC", Rows = "rows 1145FB02ED1CC19B";

    [Test]
    public void AStressWalkMatchesTheHarnessBitForBit()
    {
        EvaluationData data = Data();
        var mismatch = SyntheticWalks.MismatchKnobs.None;
        mismatch.arScale = -0.08f;
        string fingerprint = ReplayEvaluator.Fingerprint(data.plan, data.walkable, data.field, data.walls,
            data.StressFloor(), 253, mismatch);
        System.Console.WriteLine($"determinism fingerprint: {fingerprint}");
        StringAssert.Contains(Floor, fingerprint, "the floor (routes, doors, walls, signs) differs");
        StringAssert.Contains(Field, fingerprint, "the distance field differs");
        StringAssert.Contains(Rows, fingerprint, $"the generated walk differs (by row kind: {ReplayEvaluator.LastDetail})");
    }

    // The harness's band per metric (seeds 1+2, 3+4 ... 15+16): p75 0.589 ± 0.015 m, %wrong 0.000 ± 0.000,
    // %sure 45.5 ± 0.9, %Lost 3.5 ± 0.75, calibration 0.676 ± 0.007 (mean ± sd).
    static readonly (string name, float low, float high)[] Bands =
    {
        ("p75 (m)", 0.53f, 0.65f),
        ("%wrong", 0f, 0.2f),
        ("%sure", 41.8f, 49.2f),
        ("%Lost", 0f, 6.5f),
        ("calibration", 0.64f, 0.71f),
    };

    [Test]
    public void TheEditorAgreesWithTheHarnessStatistically()
    {
        EvaluationData data = Data();
        float[] m = ReplayEvaluator.TypicalAggregate(data.plan, data.walkable, data.field, data.walls, data.StressFloor(),
            new[] { 1, 2 });
        string all = string.Join(", ", Enumerable.Range(0, Bands.Length).Select(i => $"{Bands[i].name} {m[i]:F3}"));
        System.Console.WriteLine($"editor typical aggregate: {all}");
        for (int i = 0; i < Bands.Length; i++)
            Assert.That(m[i], Is.InRange(Bands[i].low, Bands[i].high), $"{Bands[i].name} outside the harness's band | {all}");
    }

    static EvaluationData Data()
    {
        EvaluationData data = EvaluationData.Load();
        if (data == null)
            Assert.Ignore("The floor's plan, walkable map or walls aren't there.");
        return data;
    }
}
