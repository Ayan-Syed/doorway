using UnityEngine;

// A short vibration "tick". Handheld.Vibrate is a long buzz with no length setting, so this asks Android's
// Vibrator for a one-shot effect instead, falling back to Handheld.Vibrate where that isn't available
// (Android 7 and older). Referencing Handheld.Vibrate also makes Unity add the VIBRATE permission.
public static class Haptics
{
#if UNITY_ANDROID && !UNITY_EDITOR
    const int OneShotSdk = 26;           // VibrationEffect arrived in Android 8 (API 26)
    const int DefaultAmplitude = -1;     // VibrationEffect.DEFAULT_AMPLITUDE
    // This many failures in a row and it stops for the run: the phone can't do it, and a long buzz for every tap
    // (the fallback) is worse than no tick at all.
    const int MaxFailures = 3;

    static int s_Sdk = -1;
    static AndroidJavaObject s_Vibrator;
    static int s_Failures;
#endif

    public static void Tick(long milliseconds = 25)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (s_Failures >= MaxFailures)
            return;
        try
        {
            if (s_Sdk < 0)
                using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                    s_Sdk = version.GetStatic<int>("SDK_INT");

            // Too old for one-shot effects: that's the phone, not a passing error, so the long buzz it is.
            if (s_Sdk < OneShotSdk)
            {
                Handheld.Vibrate();
                return;
            }

            if (s_Vibrator == null)
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    s_Vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");

            using (var effects = new AndroidJavaClass("android.os.VibrationEffect"))
            using (var effect = effects.CallStatic<AndroidJavaObject>("createOneShot", milliseconds, DefaultAmplitude))
                s_Vibrator.Call("vibrate", effect);
            s_Failures = 0;
        }
        catch (System.Exception e)
        {
            // Buzz this once, and try the short tick again next time (v7 gave up on it for good after one error).
            s_Vibrator?.Dispose();
            s_Vibrator = null;
            if (++s_Failures < MaxFailures)
                Handheld.Vibrate();
            else
                Debug.LogWarning($"[Navigation] Haptics off: {MaxFailures} vibrations in a row failed ({e.Message}).");
        }
#endif
    }
}
