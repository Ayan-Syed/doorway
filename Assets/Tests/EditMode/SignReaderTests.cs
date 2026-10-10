using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

public class SignReaderTests
{
    // A 1280 x 720 camera image (sensor orientation) with different focal lengths across and down, so a swapped axis
    // shows, and the principal point in the middle.
    const float Width = 1280f, Height = 720f;
    static readonly Vector2 Focal = new Vector2(1000f, 1100f);
    static readonly Vector2 Principal = new Vector2(640f, 360f);

    // A projection that shows the whole upright image: its size and focal lengths in the upright picture's pixels.
    static Matrix4x4 Projection(float width, float height, Vector2 focal)
    {
        Matrix4x4 p = Matrix4x4.zero;
        p[0, 0] = 2f * focal.x / width;
        p[1, 1] = 2f * focal.y / height;
        p[2, 2] = -1.001f;
        p[2, 3] = -0.1f;
        p[3, 2] = -1f;
        return p;
    }

    // Upright, a quarter-turned image is as wide as the sensor image is tall, and its focal lengths swap.
    static Matrix4x4 ProjectionFor(int rotation)
    {
        return rotation % 180 == 90
            ? Projection(Height, Width, new Vector2(Focal.y, Focal.x))
            : Projection(Width, Height, Focal);
    }

    static void AssertViewport(int rotation, Vector2 pixel, Vector2 expected)
    {
        Vector2 at = SignReader.ImageToViewport(pixel, Focal, Principal, rotation, ProjectionFor((rotation % 360 + 360) % 360));
        Assert.That(Vector2.Distance(at, expected), Is.LessThan(1e-4f), $"rotation {rotation}, pixel {pixel}: {at} not {expected}");
    }

    static readonly Vector2[] Pixels =
    {
        new Vector2(640f, 360f), new Vector2(0f, 0f), new Vector2(1280f, 720f), new Vector2(320f, 180f), new Vector2(1000f, 100f),
    };

    [Test]
    public void PortraitTurnsTheImageTopToTheScreenRight()
    {
        // The usual phone held upright: the sensor image is turned a quarter clockwise, its top to the screen's right
        // and its left edge to the screen's top.
        foreach (Vector2 p in Pixels)
            AssertViewport(90, p, new Vector2(1f - p.y / Height, 1f - p.x / Width));
        Vector3 top = SignReader.ImageToCameraDirection(new Vector2(640f, 0f), Focal, Principal, 90);
        Assert.Greater(top.x, 0.1f);
        Assert.AreEqual(0f, top.y, 1e-5f);
        Vector3 left = SignReader.ImageToCameraDirection(new Vector2(0f, 360f), Focal, Principal, 90);
        Assert.Greater(left.y, 0.1f);
        Assert.AreEqual(0f, left.x, 1e-5f);
    }

    [Test]
    public void LandscapeShowsTheImageAsItIs()
    {
        foreach (Vector2 p in Pixels)
            AssertViewport(0, p, new Vector2(p.x / Width, 1f - p.y / Height));
    }

    [Test]
    public void OtherTurnsMapTheWayRoundTheyShould()
    {
        foreach (Vector2 p in Pixels)
        {
            AssertViewport(270, p, new Vector2(p.y / Height, p.x / Width));
            AssertViewport(180, p, new Vector2(1f - p.x / Width, p.y / Height));
            AssertViewport(-90, p, new Vector2(p.y / Height, p.x / Width));   // the same as 270
        }
    }

    [Test]
    public void ThePrincipalPointIsStraightAhead()
    {
        foreach (int rotation in new[] { 0, 90, 180, 270 })
        {
            Vector3 d = SignReader.ImageToCameraDirection(Principal, Focal, Principal, rotation);
            Assert.That(Vector3.Distance(d, Vector3.forward), Is.LessThan(1e-6f));
        }
    }

    [Test]
    public void DistanceComesFromTheDigitsHeight()
    {
        // Upright image: the digits' height is the box's height, with the focal length down the image.
        var box = new Rect(600f, 335f, 120f, 50f);
        Assert.AreEqual(Focal.y * SignReader.PlateDigitHeight / 50f, SignReader.Distance(box, Focal, 0, Vector3.forward), 1e-5f);
        // A quarter-turned image: the digits stand along the sensor's x, so it's the box's width and the focal across.
        var turned = new Rect(600f, 300f, 50f, 120f);
        Assert.AreEqual(Focal.x * SignReader.PlateDigitHeight / 50f, SignReader.Distance(turned, Focal, 90, Vector3.forward), 1e-5f);
        Assert.AreEqual(50f, SignReader.TextPixels(turned, 270));
        // That's the depth; 45 degrees off-centre the plate is further along the ray.
        Vector3 aside = new Vector3(1f, 0f, 1f).normalized;
        Assert.AreEqual(Focal.y * SignReader.PlateDigitHeight / 50f * Mathf.Sqrt(2f), SignReader.Distance(box, Focal, 0, aside), 1e-4f);
        Assert.AreEqual(0f, SignReader.Distance(new Rect(0f, 0f, 0f, 0f), Focal, 0, Vector3.forward));
    }

    [Test]
    public void FramesAreReadAtMost1280WideAndEven()
    {
        Assert.AreEqual(new Vector2Int(1280, 720), SignReader.ReadSize(new Vector2Int(1920, 1080)));
        Assert.AreEqual(new Vector2Int(640, 480), SignReader.ReadSize(new Vector2Int(640, 480)));
        Assert.AreEqual(new Vector2Int(1278, 720), SignReader.ReadSize(new Vector2Int(1279, 721)));
    }

    [Test]
    public void NormaliseFixesLookAlikesOnlyAmongDigits()
    {
        Assert.AreEqual("107A", SignReader.Normalise("1O7a"));
        Assert.AreEqual("121", SignReader.Normalise(" 12l "));
        Assert.AreEqual("121", SignReader.Normalise("l21."));
        Assert.AreEqual("121", SignReader.Normalise("|2|"));
        Assert.AreEqual("107", SignReader.Normalise("IO7"));
        Assert.AreEqual("ROOM", SignReader.Normalise("Room"));
        Assert.AreEqual("OIL", SignReader.Normalise("oil"));
        Assert.AreEqual("", SignReader.Normalise(null));
    }

    [Test]
    public void OnlyThreeDigitsAndALetterArePlates()
    {
        Assert.IsTrue(SignReader.IsPlateText("121"));
        Assert.IsTrue(SignReader.IsPlateText("107A"));
        Assert.IsFalse(SignReader.IsPlateText("12"));
        Assert.IsFalse(SignReader.IsPlateText("1210"));
        Assert.IsFalse(SignReader.IsPlateText("107AB"));
        Assert.IsFalse(SignReader.IsPlateText("ROOM"));
        Assert.IsFalse(SignReader.IsPlateText("107a"));
    }

    [Test]
    public void ParseReadsWordsLinesAndBarsInText()
    {
        var words = new List<SignReader.Word>();
        SignReader.ParseWords("ROOM|10|20|60|40\n121|70|20|110|42\n\n|21|5|6|7|8\nnonsense\n\n", words);
        Assert.AreEqual(3, words.Count);
        Assert.AreEqual("ROOM", words[0].text);
        Assert.AreEqual(0, words[1].line);
        Assert.AreEqual(Rect.MinMaxRect(70f, 20f, 110f, 42f), words[1].box);
        Assert.AreEqual("|21", words[2].text);
        Assert.AreEqual(1, words[2].line);
        Assert.AreEqual(Rect.MinMaxRect(5f, 6f, 7f, 8f), words[2].box);
    }

    static SignReader.Word W(string text, int line, float left, float size = 20f)
    {
        return new SignReader.Word { text = text, line = line, box = new Rect(left, 100f, size, size) };
    }

    static Dictionary<string, Rect> Plates(ICollection<string> valid, params SignReader.Word[] words)
    {
        var plates = new Dictionary<string, Rect>();
        SignReader.FindPlates(new List<SignReader.Word>(words), valid, plates);
        return plates;
    }

    [Test]
    public void PlatesMustBeOnThePlan()
    {
        var plan = new HashSet<string> { "121", "107", "107A" };
        CollectionAssert.AreEquivalent(new[] { "121" }, Plates(plan, W("ROOM", 0, 0f), W("12l", 0, 50f)).Keys);
        Assert.IsEmpty(Plates(plan, W("999", 0, 0f), W("EXIT", 1, 0f)));
        Assert.IsEmpty(Plates(new HashSet<string>(), W("121", 0, 0f)));
    }

    [Test]
    public void ALoneLetterAfterANumberJoinsIt()
    {
        var plan = new HashSet<string> { "107", "107A" };
        Dictionary<string, Rect> plates = Plates(plan, W("107", 0, 0f), W("A", 0, 30f));
        CollectionAssert.AreEquivalent(new[] { "107A" }, plates.Keys);
        Assert.AreEqual(Rect.MinMaxRect(0f, 100f, 50f, 120f), plates["107A"]);
        // Not when the plan has no 107A, nor across lines.
        CollectionAssert.AreEquivalent(new[] { "107" }, Plates(new HashSet<string> { "107" }, W("107", 0, 0f), W("A", 0, 30f)).Keys);
        Assert.IsEmpty(Plates(new HashSet<string> { "107A" }, W("107", 0, 0f), W("A", 1, 30f)));
    }

    [Test]
    public void TheSameTextTwiceKeepsTheBiggerBox()
    {
        Dictionary<string, Rect> plates = Plates(new HashSet<string> { "121" }, W("121", 0, 0f, 10f), W("121", 1, 0f, 40f), W("121", 2, 0f, 20f));
        Assert.AreEqual(40f, plates["121"].height);
    }

    static bool Vote(SignReader.Votes votes, params string[] texts)
    {
        votes.Add(texts);
        return votes.Confirmed("121");
    }

    [Test]
    public void ATextCountsInTwoOfTheLastThreeFrames()
    {
        var votes = new SignReader.Votes();
        Assert.IsFalse(Vote(votes, "121"));
        Assert.IsTrue(Vote(votes, "121"));

        votes = new SignReader.Votes();
        Assert.IsFalse(Vote(votes, "121"));
        Assert.IsFalse(Vote(votes));
        Assert.IsTrue(Vote(votes, "121"));   // frames 1 and 3

        votes = new SignReader.Votes();
        Assert.IsFalse(Vote(votes, "121"));
        Assert.IsFalse(Vote(votes, "107"));
        Assert.IsFalse(Vote(votes));
        Assert.IsFalse(Vote(votes, "121"));   // frames 1 and 4: too far apart

        votes.Clear();
        Assert.IsFalse(Vote(votes, "121"));
    }

    [Test]
    public void ReadMakesASightingFromTheFrameAsCaptured()
    {
        var go = new GameObject("Sign reader test");
        try
        {
            var reader = go.AddComponent<SignReader>();
            reader.logSightings = false;
            reader.SetTexts(new[] { "121", "107A", "Lobby" });
            var sightings = new List<SignSighting>();
            reader.Sighted += sightings.Add;

            // A portrait frame, the camera turned to face world +x.
            var frame = new SignReader.Frame
            {
                time = 12.5,
                position = new Vector3(1f, 2f, 3f),
                rotation = Quaternion.Euler(0f, 90f, 0f),
                focal = new Vector2(1000f, 1000f),
                principal = Principal,
                imageRotation = 90,
            };
            // "121" centred on the principal point; quarter-turned, its digits are the box's 80 px width tall.
            const string plate = "Room|500|340|580|380\n121|600|340|680|380\n\n";
            reader.Read(plate, frame);
            reader.Read("EXIT|0|0|10|10\n\n", frame);
            Assert.IsEmpty(sightings);
            reader.Read(plate, frame);
            Assert.AreEqual(1, sightings.Count);

            SignSighting s = sightings[0];
            Assert.AreEqual("121", s.text);
            Assert.AreEqual(12.5, s.time);
            Assert.That(Vector3.Distance(s.cameraPosition, frame.position), Is.LessThan(1e-5f));
            Assert.That(Vector3.Distance(s.direction, Vector3.right), Is.LessThan(1e-4f));
            Assert.AreEqual(1000f * SignReader.PlateDigitHeight / 80f, s.distance, 1e-4f);
            Assert.AreEqual(0.3f * s.distance, s.distanceSigma, 1e-5f);
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }
}
