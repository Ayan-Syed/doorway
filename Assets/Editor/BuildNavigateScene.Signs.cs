using UnityEngine;
using UnityEngine.XR.ARFoundation;

public static partial class BuildNavigateScene
{
    // The room-number plate reader, on the AR camera beside its camera manager, so it reads the frames the user sees
    // and the camera's pose when each was taken. Built switched off: the positioning code turns reading on.
    static SignReader CreateSignReader(Camera arCamera)
    {
        var reader = arCamera.gameObject.AddComponent<SignReader>();
        reader.cameraManager = arCamera.GetComponent<ARCameraManager>();
        reader.arCamera = arCamera;
        reader.reading = false;
        return reader;
    }
}
