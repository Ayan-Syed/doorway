using UnityEngine;

// One room-number sign read in a camera frame: SignReader (on-device text recognition) makes these, and Navigator
// turns them into position fixes. Everything is in AR world space at the moment the frame was captured, so a
// sighting stays valid however late it arrives.
public struct SignSighting
{
    /// <summary>The sign's text as the plan spells it (a PlanSign.text), e.g. "121" or "107A".</summary>
    public string text;
    /// <summary>Time.realtimeSinceStartupAsDouble when the camera frame was captured.</summary>
    public double time;
    /// <summary>The AR camera's world position when the frame was captured.</summary>
    public Vector3 cameraPosition;
    /// <summary>World-space unit ray from the camera through the centre of the text.</summary>
    public Vector3 direction;
    /// <summary>Metres from the camera to the sign along the ray, from the digits' height in pixels.</summary>
    public float distance;
    /// <summary>One standard deviation of that distance, metres.</summary>
    public float distanceSigma;
    /// <summary>The digits' height in image pixels, and the focal length in the same pixels: with a known distance
    /// (once the position is confident) they measure the plates' real digit height.</summary>
    public float digitPixels, focalPixels;
    /// <summary>True when the distance came from ARCore depth or a detected wall, not from the digits' size.</summary>
    public bool distanceMeasured;
}
