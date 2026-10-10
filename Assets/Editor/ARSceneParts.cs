using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR.ARFoundation;

// The AR Session and the XR Origin, built the same way as AR Foundation's own GameObject > XR menu items.
// The scene builder changes the camera and adds the plane manager afterwards; keep those changes out of here.
internal static class ARSceneParts
{
    // Same objects as AR Foundation's 'GameObject > XR > AR Session'.
    internal static ARSession CreateARSession()
    {
        var go = new GameObject("AR Session", typeof(ARSession), typeof(ARInputManager));
        return go.GetComponent<ARSession>();
    }

    // Same objects and settings as AR Foundation's 'GameObject > XR > XR Origin (Mobile AR)'.
    internal static XROrigin CreateXROrigin()
    {
        var originGo = new GameObject("XR Origin", typeof(XROrigin));
        var offsetGo = new GameObject("Camera Offset");
        offsetGo.transform.SetParent(originGo.transform, false);

        var cameraGo = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener),
            typeof(ARCameraManager), typeof(ARCameraBackground), typeof(TrackedPoseDriver));
        cameraGo.transform.SetParent(offsetGo.transform, false);
        cameraGo.tag = "MainCamera";

        var camera = cameraGo.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.Color;
        camera.backgroundColor = Color.black;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 20f;

        // The phone's pose: the <HandheldARInputDevice> bindings are the ones that drive a phone.
        var driver = cameraGo.GetComponent<TrackedPoseDriver>();
        var position = new InputAction("Position", binding: "<XRHMD>/centerEyePosition", expectedControlType: "Vector3");
        position.AddBinding("<HandheldARInputDevice>/devicePosition");
        var rotation = new InputAction("Rotation", binding: "<XRHMD>/centerEyeRotation", expectedControlType: "Quaternion");
        rotation.AddBinding("<HandheldARInputDevice>/deviceRotation");
        driver.positionInput = new InputActionProperty(position);
        driver.rotationInput = new InputActionProperty(rotation);

        var origin = originGo.GetComponent<XROrigin>();
        origin.CameraFloorOffsetObject = offsetGo;
        origin.Camera = camera;
        return origin;
    }
}
