using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

// What voice needs in the Android manifest, added to every Gradle project Unity generates:
// - RECORD_AUDIO, for VoiceInput (Unity adds it only for its own Microphone class, which the app doesn't use).
// - <queries> for the speech recognition and text to speech services: from Android 11, an app can't see (bind to)
//   another app's service unless its manifest says it looks for it, and SpeechRecognizer.isRecognitionAvailable and
//   TextToSpeech would quietly find nothing.
// This is unityLibrary's manifest; Gradle merges it into the app's.
public class VoiceManifest : IPostGenerateGradleAndroidProject
{
    const string AndroidNamespace = "http://schemas.android.com/apk/res/android";
    const string RecordAudio = "android.permission.RECORD_AUDIO";
    static readonly string[] Services = { "android.speech.RecognitionService", "android.intent.action.TTS_SERVICE" };

    public int callbackOrder => 10;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string file = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(file))
        {
            Debug.LogError($"[Voice] No manifest at {file}: voice won't work in this build.");
            return;
        }
        var document = new XmlDocument();
        document.Load(file);
        XmlElement manifest = document.DocumentElement;
        if (manifest == null)
            return;

        if (!HasNamed(manifest, "uses-permission", RecordAudio))
        {
            XmlElement permission = document.CreateElement("uses-permission");
            permission.SetAttribute("name", AndroidNamespace, RecordAudio);
            manifest.AppendChild(permission);
        }

        XmlElement queries = manifest["queries"];
        if (queries == null)
        {
            queries = document.CreateElement("queries");
            manifest.AppendChild(queries);
        }
        foreach (string service in Services)
        {
            if (HasAction(queries, service))
                continue;
            XmlElement intent = document.CreateElement("intent");
            XmlElement action = document.CreateElement("action");
            action.SetAttribute("name", AndroidNamespace, service);
            intent.AppendChild(action);
            queries.AppendChild(intent);
        }
        document.Save(file);
    }

    static bool HasNamed(XmlElement parent, string tag, string name)
    {
        foreach (XmlNode node in parent.ChildNodes)
            if (node is XmlElement element && element.Name == tag &&
                element.GetAttribute("name", AndroidNamespace) == name)
                return true;
        return false;
    }

    static bool HasAction(XmlElement queries, string name)
    {
        foreach (XmlElement action in queries.GetElementsByTagName("action"))
            if (action.GetAttribute("name", AndroidNamespace) == name)
                return true;
        return false;
    }
}
