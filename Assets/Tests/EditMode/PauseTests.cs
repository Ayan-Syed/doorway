using NUnit.Framework;

// A pause shorter than Navigator.ShortPauseSeconds (a permission dialog) keeps the alignment and the localizer's
// cloud; a longer one (the phone locked, perhaps carried somewhere) asks to line up again and spreads the cloud.
public class PauseTests
{
    [Test]
    public void APermissionDialogIsNotALongPause()
    {
        Assert.IsFalse(Navigator.LongPause(0.4));
        Assert.IsFalse(Navigator.LongPause(Navigator.ShortPauseSeconds - 0.1));
        Assert.IsFalse(Navigator.LongPause(double.NaN), "never paused");
    }

    [Test]
    public void ThePhoneLockedForAWhileIsALongPause()
    {
        Assert.IsTrue(Navigator.LongPause(Navigator.ShortPauseSeconds));
        Assert.IsTrue(Navigator.LongPause(90.0));
    }

    // The engine side of a long pause indoors (Localizer.OnApplicationPause relocalizes): sure before, not after.
    [Test]
    public void ALongPauseIndoorsLeavesTheLocalizerUnsure()
    {
        EvaluationData data = EvaluationData.Load();
        if (data == null)
            Assert.Ignore("The floor's plan, walkable map or walls aren't there.");
        var engine = new LocalizerEngine(data.plan, data.walkable, data.field, data.walls, LocalizerEngine.Config.Full, 1);
        var options = SyntheticWalks.Options.Default(42);
        options.lossChance = 0f;
        var lines = SyntheticWalks.Walk(data.routes[0], data.walkable, data.wallSegments, data.plan.signs, data.doorSpots,
            options);
        var feeder = new ReplayEvaluator.Feeder(engine);
        double last = 0;
        bool wasSure = false;
        foreach (string line in lines)
        {
            if (feeder.Feed(line, out _, out double t))
                last = t;
            wasSure |= engine.State == LocalizerState.Localized;
        }
        Assume.That(wasSure, "the walk never got sure");
        engine.Relocalize(last + Navigator.ShortPauseSeconds);
        Assert.AreNotEqual(LocalizerState.Localized, engine.State);
    }
}
