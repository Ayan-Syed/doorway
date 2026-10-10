using NUnit.Framework;
using UnityEngine;

public class StepCounterTests
{
    // Upward acceleration sampled at 50 Hz for some seconds: a sine of this many cycles a second and amplitude.
    static int Count(float hertz, float amplitude, float seconds = 5f, float noise = 0f)
    {
        var detector = new StepCounter.PeakDetector();
        var random = new System.Random(1);
        int steps = 0;
        for (int i = 0; i < seconds * 50f; i++)
        {
            double t = i / 50.0;
            float a = amplitude * Mathf.Sin(2f * Mathf.PI * hertz * (float)t) + noise * (float)(random.NextDouble() * 2.0 - 1.0);
            if (detector.Add(t, a))
                steps++;
        }
        return steps;
    }

    [Test]
    public void WalkingGivesOneStepPerBounce()
    {
        Assert.AreEqual(10, Count(2f, 3f));                 // 2 steps a second for 5 s
        Assert.AreEqual(9, Count(1.8f, 2f, noise: 0.3f));   // slower, with sensor noise
    }

    [Test]
    public void StandingStillGivesNoSteps()
    {
        Assert.AreEqual(0, Count(2f, 0.5f, noise: 0.3f));
        Assert.AreEqual(0, Count(0f, 0f, noise: 0.5f));
    }

    [Test]
    public void ShakingFasterThanWalkingIsCappedByTheMinimumInterval()
    {
        Assert.LessOrEqual(Count(6f, 4f), Mathf.CeilToInt(5f / StepCounter.PeakDetector.MinInterval));
    }

    [Test]
    public void HeadingIsClockwiseFromNorth()
    {
        // Flat, top edge to the north.
        Assert.AreEqual(0f, StepCounter.HeadingFromRotation(Quaternion.identity), 1e-3f);
        // Flat, turned a quarter clockwise seen from above (about world z, by -90): top edge to the east.
        Quaternion east = new Quaternion(0f, 0f, -Mathf.Sqrt(0.5f), Mathf.Sqrt(0.5f));
        Assert.AreEqual(90f, StepCounter.HeadingFromRotation(east), 1e-3f);
        // Standing up (tipped back about device x by 90) and facing north: the camera looks north.
        Quaternion upright = new Quaternion(Mathf.Sqrt(0.5f), 0f, 0f, Mathf.Sqrt(0.5f));
        Assert.AreEqual(0f, StepCounter.HeadingFromRotation(upright), 1e-3f);
    }

    [Test]
    public void VerticalAccelerationFollowsTheWorldsUp()
    {
        // Flat: world up is the device's z.
        Assert.AreEqual(2f, StepCounter.VerticalAcceleration(new Vector3(0f, 0f, 2f), Quaternion.identity), 1e-5f);
        // Upright: world up is the device's y (the top edge).
        Quaternion upright = new Quaternion(Mathf.Sqrt(0.5f), 0f, 0f, Mathf.Sqrt(0.5f));
        Assert.AreEqual(2f, StepCounter.VerticalAcceleration(new Vector3(0f, 2f, 0f), upright), 1e-5f);
        Assert.AreEqual(0f, StepCounter.VerticalAcceleration(new Vector3(0f, 0f, 2f), upright), 1e-5f);
    }
}
