using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.ARCore;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;

// One click (or one -executeMethod) for the Android, ARCore and identity settings the app relies on. The rules
// come from the ARCore XR Plugin's build checks (ARCoreBuildProcessor) and Google's ARCore requirements.
// The render pipeline is intentionally left alone (Built-in), and so is Active Input Handling: it stays at Both,
// because GPS uses the legacy Input.location and the AR camera needs the Input System.
//   Unity.exe -batchmode -quit -projectPath <project> -executeMethod ProjectSetup.ApplyFromCommandLine
public static class ProjectSetup
{
    const string ARCoreLoaderType = "UnityEngine.XR.ARCore.ARCoreLoader";
    const string XRSettingsAssetPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
    const string ARCoreSettingsPath = "Assets/XR/Settings/ARCoreSettings.asset";

    // ARCore with OpenGLES3 needs at least API 25 on Unity 6.3 (ARCoreProjectValidationRules).
    const AndroidSdkVersions MinAndroidApiLevel = AndroidSdkVersions.AndroidApiLevel25;
    // Pinned, not "highest installed" (the default), so every laptop builds the same target.
    const AndroidSdkVersions TargetAndroidApiLevel = (AndroidSdkVersions)36;

    // One package name for every laptop, so a build from anyone's machine installs over the last one
    // (as long as it's signed with the same key). The default was com.DefaultCompany.Doorway.
    const string CompanyName = "Doorway";
    const string ProductName = "Doorway";
    const string ApplicationId = "com.ayansyed.doorway";

    [MenuItem("Doorway/Apply Project Settings")]
    public static void Apply()
    {
        if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android)
            EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android);

        PlayerSettings.companyName = CompanyName;
        PlayerSettings.productName = ProductName;
        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationId);

        // ARCore apps on Google Play must ship 64-bit, and ARM64 needs IL2CPP (Mono has no ARM64 on Android).
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

        // Raised, never lowered.
        if (PlayerSettings.Android.minSdkVersion < MinAndroidApiLevel)
            PlayerSettings.Android.minSdkVersion = MinAndroidApiLevel;
        PlayerSettings.Android.targetSdkVersion = TargetAndroidApiLevel;

        // ARCore needs OpenGLES3 or Vulkan. OpenGLES3 alone is the long-tested path and works from API 25;
        // ARCore on Vulkan needs API 29.
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.OpenGLES3 });

        // New Unity 6 projects start on GameActivity. The ARCore plugin never mentions it, so use the classic
        // Activity entry point that ARCore has always been tested with.
        PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;

        // Portrait only, and the app's own launch screen instead of Unity's (the scene builder sets these too).
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
        PlayerSettings.SplashScreen.show = false;

        EnableARCoreLoader();
        ConfigureARCore();
        AssetDatabase.SaveAssets();

        Debug.Log("[Navigation] Project settings applied: Android target, IL2CPP, ARM64 only, " +
                  $"min {PlayerSettings.Android.minSdkVersion}, OpenGLES3 only, Activity entry point, ARCore loader on, " +
                  $"ARCore Required, Depth Optional, {PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android)}.");
    }

    // For -executeMethod: applies the settings and exits with 0, or 1 on any error.
    public static void ApplyFromCommandLine()
    {
        try
        {
            Apply();
            EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            EditorApplication.Exit(1);
        }
    }

    // What would make an Android build ship without AR or under another name; empty when the settings are right.
    public static List<string> Problems()
    {
        var problems = new List<string>();
        if (PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android) != ApplicationId)
            problems.Add($"the package name isn't {ApplicationId}");
        if (PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) != ScriptingImplementation.IL2CPP)
            problems.Add("the scripting backend isn't IL2CPP");
        if ((PlayerSettings.Android.targetArchitectures & AndroidArchitecture.ARM64) == 0)
            problems.Add("ARM64 isn't a target architecture");
        if (!ARCoreLoaderAssigned())
            problems.Add("ARCore isn't the Android XR plug-in");
        return problems;
    }

    static bool ARCoreLoaderAssigned()
    {
        if (!EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget)
            || perTarget == null)
            return false;
        XRGeneralSettings android = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        if (android == null || android.AssignedSettings == null)
            return false;
        foreach (XRLoader loader in android.AssignedSettings.activeLoaders)
        {
            if (loader != null && loader.GetType().FullName == ARCoreLoaderType)
                return true;
        }
        return false;
    }

    // AR Foundation: ARCore must be the XR provider for Android, started with the app.
    static void EnableARCoreLoader()
    {
        XRGeneralSettingsPerBuildTarget perTarget = FindOrCreateXRSettings();
        if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
            perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);

        XRGeneralSettings androidSettings = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        androidSettings.InitManagerOnStart = true;
        EditorUtility.SetDirty(androidSettings);

        if (!XRPackageMetadataStore.AssignLoader(androidSettings.AssignedSettings, ARCoreLoaderType, BuildTargetGroup.Android))
            Debug.LogError("[Navigation] Could not enable ARCore. Open Edit > Project Settings > XR Plug-in Management, " +
                           "Android tab, and tick 'Google ARCore'.");
    }

    // ARCore itself is Required (the app is nothing without it), but Depth is Optional: no code uses depth
    // (there's no AROcclusionManager), and Required would shut out ARCore phones without the Depth API.
    static void ConfigureARCore()
    {
        ARCoreSettings settings = ARCoreSettings.currentSettings;
        if (settings == null)
        {
            settings = AssetDatabase.LoadAssetAtPath<ARCoreSettings>(ARCoreSettingsPath);
            if (settings == null)
            {
                UIBuild.EnsureFolder(Path.GetDirectoryName(ARCoreSettingsPath).Replace('\\', '/'));
                settings = ScriptableObject.CreateInstance<ARCoreSettings>();
                AssetDatabase.CreateAsset(settings, ARCoreSettingsPath);
            }
            ARCoreSettings.currentSettings = settings;
        }
        settings.requirement = ARCoreSettings.Requirement.Required;
        settings.depth = ARCoreSettings.Requirement.Optional;
        EditorUtility.SetDirty(settings);
    }

    static XRGeneralSettingsPerBuildTarget FindOrCreateXRSettings()
    {
        if (EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget)
            && perTarget != null)
            return perTarget;

        string[] found = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget");
        if (found.Length > 0)
            perTarget = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(AssetDatabase.GUIDToAssetPath(found[0]));

        if (perTarget == null)
        {
            UIBuild.EnsureFolder(Path.GetDirectoryName(XRSettingsAssetPath).Replace('\\', '/'));
            perTarget = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            AssetDatabase.CreateAsset(perTarget, XRSettingsAssetPath);
        }
        EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, perTarget, true);
        return perTarget;
    }
}

// Stops any Android build, a manual File > Build included, whose settings would ship an app without AR or under
// another package name. BuildApk applies the settings itself; elsewhere, use Doorway > Apply Project Settings.
class ProjectSetupBuildCheck : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.Android)
            return;
        List<string> problems = ProjectSetup.Problems();
        if (problems.Count > 0)
            throw new BuildFailedException("[Navigation] Not building: " + string.Join("; ", problems) +
                                           ". Run Doorway > Apply Project Settings.");
    }
}
