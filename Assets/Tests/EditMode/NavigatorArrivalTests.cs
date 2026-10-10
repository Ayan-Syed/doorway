using NUnit.Framework;

// Arrival only counts while the localizer is sure enough of the position (Navigator.SureOfPosition).
public class NavigatorArrivalTests
{
    [Test]
    public void NoArrivalWhileConvergingWideOrLost()
    {
        Assert.IsTrue(Navigator.SureOfPosition(LocalizerState.Localized, 0.5f));
        Assert.IsTrue(Navigator.SureOfPosition(LocalizerState.Converging, Navigator.ArriveSpread));
        Assert.IsFalse(Navigator.SureOfPosition(LocalizerState.Converging, Navigator.ArriveSpread + 0.1f));
        Assert.IsFalse(Navigator.SureOfPosition(LocalizerState.Converging, 8f));
        Assert.IsFalse(Navigator.SureOfPosition(LocalizerState.Lost, 0.5f));
        Assert.IsFalse(Navigator.SureOfPosition(LocalizerState.Lost, 30f));
    }
}
