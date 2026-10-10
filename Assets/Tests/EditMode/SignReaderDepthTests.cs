using NUnit.Framework;
using UnityEngine;

public class SignReaderDepthTests
{
    [Test]
    public void DepthBecomesRangeAlongTheRay()
    {
        Assert.AreEqual(2f, SignReader.DepthToRange(2f, Vector3.forward), 1e-6f);
        // 60 degrees off the optical axis the plate is twice as far along the ray as its depth.
        Vector3 aside = new Vector3(Mathf.Sin(60f * Mathf.Deg2Rad), 0f, Mathf.Cos(60f * Mathf.Deg2Rad));
        Assert.AreEqual(4f, SignReader.DepthToRange(2f, aside), 1e-4f);
        Assert.AreEqual(0f, SignReader.DepthToRange(0f, Vector3.forward));
        Assert.AreEqual(0f, SignReader.DepthToRange(2f, Vector3.back));
    }

    [Test]
    public void DepthIsTheMedianOfThePatchWithoutHoles()
    {
        const int w = 16, h = 9;
        var depth = new float[w * h];
        for (int i = 0; i < depth.Length; i++)
            depth[i] = 3f;
        // Around (8, 4): an outlier and some unknown pixels, which the median and the hole rule ignore.
        depth[4 * w + 8] = 50f;
        depth[3 * w + 7] = 0f;
        depth[5 * w + 9] = 0f;
        Assert.AreEqual(3f, SignReader.DepthAt(depth, w, h, new Vector2(8.5f / w, 4.5f / h)), 1e-6f);
        // At the corner the patch is cut off by the edge; still the median of what's there.
        Assert.AreEqual(3f, SignReader.DepthAt(depth, w, h, Vector2.zero), 1e-6f);
        Assert.AreEqual(0f, SignReader.DepthAt(new float[w * h], w, h, new Vector2(0.5f, 0.5f)));
        Assert.AreEqual(0f, SignReader.DepthAt(null, w, h, new Vector2(0.5f, 0.5f)));
        Assert.AreEqual(0f, SignReader.DepthAt(depth, w, h, new Vector2(1.5f, 0.5f)));
    }

    [Test]
    public void ASightingUsesDepthWhenTheFrameHasIt()
    {
        var go = new GameObject("Sign reader depth test");
        try
        {
            var reader = go.AddComponent<SignReader>();
            reader.logSightings = false;
            reader.SetTexts(new[] { "121" });
            SignSighting got = default;
            reader.Sighted += s => got = s;
            // The digits say 0.625 m (80 px at f = 1000); depth says 0.8 m, all over a 16 x 9 depth image.
            var depth = new float[16 * 9];
            for (int i = 0; i < depth.Length; i++)
                depth[i] = 0.8f;
            var frame = new SignReader.Frame
            {
                rotation = Quaternion.identity,
                focal = new Vector2(1000f, 1000f),
                principal = new Vector2(640f, 360f),
                imageRotation = 90,
                imageSize = new Vector2Int(1280, 720),
                depth = depth,
                depthWidth = 16,
                depthHeight = 9,
            };
            const string plate = "121|600|340|680|380\n\n";
            reader.Read(plate, frame);
            reader.Read(plate, frame);
            Assert.IsTrue(got.distanceMeasured);
            Assert.AreEqual(0.8f, got.distance, 1e-4f);
            Assert.AreEqual(0.1f + 0.05f * 0.8f, got.distanceSigma, 1e-4f);
            Assert.AreEqual(80f, got.digitPixels);
            Assert.AreEqual(1000f, got.focalPixels);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
