using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

public static partial class BuildNavigateScene
{
    // The room-number plate reader, on the AR camera beside its camera manager, so it reads the frames the user sees
    // and the camera's pose when each was taken. Built switched off: the positioning code turns reading on. With it,
    // an occlusion manager whose depth the Localizer turns on indoors: never drawn (no occlusion), only read, for
    // plate distances and scan matching. The plane manager (already on the XR Origin) gives walls to fall back on.
    static SignReader CreateSignReader(Camera arCamera)
    {
        var occlusion = arCamera.gameObject.AddComponent<AROcclusionManager>();
        occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Disabled;
        occlusion.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;

        var reader = arCamera.gameObject.AddComponent<SignReader>();
        reader.cameraManager = arCamera.GetComponent<ARCameraManager>();
        reader.arCamera = arCamera;
        reader.occlusionManager = occlusion;
        reader.planeManager = arCamera.GetComponentInParent<ARPlaneManager>();
        reader.reading = false;
        return reader;
    }

    // Steps and heading for dead reckoning, switched off until the positioning code sets running.
    static StepCounter CreateStepCounter(GameObject host)
    {
        var steps = host.AddComponent<StepCounter>();
        steps.running = false;
        return steps;
    }
}
