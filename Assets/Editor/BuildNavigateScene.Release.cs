using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using Debug = UnityEngine.Debug;

// The per-build settings that must never end up in ProjectSettings.asset, set for one build and put back after it:
//   the version: -versionCode N, else the number of commits (it only grows, so each build installs over the last);
//   the signing key: from the environment, so every laptop signs with the one shared key and a build from any of
//   them installs over another's. The keystore and its passwords stay out of git:
//     DOORWAY_KEYSTORE (path to the .jks), DOORWAY_KEYSTORE_PASS, DOORWAY_KEY_ALIAS, DOORWAY_KEY_PASS.
//   Without DOORWAY_KEYSTORE the build is signed with this laptop's debug key.
public static partial class BuildNavigateScene
{
    sealed class ReleaseSettings : IDisposable
    {
        readonly int m_VersionCode = PlayerSettings.Android.bundleVersionCode;
        readonly string m_Version = PlayerSettings.bundleVersion;
        readonly bool m_CustomKeystore = PlayerSettings.Android.useCustomKeystore;
        readonly string m_Keystore = PlayerSettings.Android.keystoreName;
        readonly string m_Alias = PlayerSettings.Android.keyaliasName;

        public ReleaseSettings()
        {
            int code = VersionCodeArgument();
            PlayerSettings.Android.bundleVersionCode = code;
            PlayerSettings.bundleVersion = $"1.0.{code}";

            string keystore = Environment.GetEnvironmentVariable("DOORWAY_KEYSTORE");
            if (string.IsNullOrEmpty(keystore))
            {
                PlayerSettings.Android.useCustomKeystore = false;
                Debug.LogWarning($"[Navigation] Version {code}, signed with this laptop's debug key (no DOORWAY_KEYSTORE). " +
                                 "A phone with another laptop's build must uninstall it first.");
                return;
            }
            string keystorePass = Environment.GetEnvironmentVariable("DOORWAY_KEYSTORE_PASS");
            string alias = Environment.GetEnvironmentVariable("DOORWAY_KEY_ALIAS");
            string keyPass = Environment.GetEnvironmentVariable("DOORWAY_KEY_PASS");
            if (!File.Exists(keystore) || string.IsNullOrEmpty(keystorePass) || string.IsNullOrEmpty(alias))
                throw new InvalidOperationException($"DOORWAY_KEYSTORE is set, but {keystore} doesn't exist or " +
                                                    "DOORWAY_KEYSTORE_PASS or DOORWAY_KEY_ALIAS is empty.");
            PlayerSettings.Android.useCustomKeystore = true;
            PlayerSettings.Android.keystoreName = keystore;
            PlayerSettings.Android.keystorePass = keystorePass;
            PlayerSettings.Android.keyaliasName = alias;
            PlayerSettings.Android.keyaliasPass = string.IsNullOrEmpty(keyPass) ? keystorePass : keyPass;
            Debug.Log($"[Navigation] Version {code}, signed with the shared key ({alias}).");
        }

        public void Dispose()
        {
            PlayerSettings.Android.bundleVersionCode = m_VersionCode;
            PlayerSettings.bundleVersion = m_Version;
            PlayerSettings.Android.useCustomKeystore = m_CustomKeystore;
            PlayerSettings.Android.keystoreName = m_Keystore;
            PlayerSettings.Android.keyaliasName = m_Alias;
            PlayerSettings.Android.keystorePass = "";
            PlayerSettings.Android.keyaliasPass = "";
        }
    }

    static int VersionCodeArgument()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-versionCode" && int.TryParse(args[i + 1], out int n) && n > 0)
                return n;
        }
        try
        {
            var start = new ProcessStartInfo("git", "rev-list --count HEAD")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (Process git = Process.Start(start))
            {
                string output = git.StandardOutput.ReadToEnd();
                git.WaitForExit();
                if (git.ExitCode == 0 && int.TryParse(output.Trim(), out int count) && count > 0)
                    return count;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Navigation] No git for the version code ({e.Message}); using 1.");
        }
        return 1;
    }
}
