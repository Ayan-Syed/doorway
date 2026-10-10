using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.Android;
#endif

// Speech to text with Android's own recogniser (android.speech.SpeechRecognizer): offline where the phone has the
// language pack, else through Google's service. No plugin: JNI calls, like Haptics and UserLocation.
//
// The recogniser must be made and driven on Android's main thread, and its callbacks arrive there, not on Unity's.
// So every call goes through runOnUiThread, and every callback only queues an Action that Update runs.
// The manifest needs RECORD_AUDIO and a <queries> entry for the recognition service (Editor/VoiceManifest.cs).
//
// In the editor there is no recogniser: Listen() "hears" editorUtterance after a moment, to try the conversation.
public class VoiceInput : MonoBehaviour
{
    [Tooltip("In the editor, Listen() hears this instead of the microphone.")]
    public string editorUtterance = "take me to room 107";

    /// <summary>Between Listen() and a result or a failure.</summary>
    public bool IsListening { get; private set; }
    /// <summary>The microphone level while listening, 0 to 1.</summary>
    public float Level { get; private set; }

    /// <summary>What was heard: the recogniser's alternatives, best first.</summary>
    public event Action<IReadOnlyList<string>> Heard;
    /// <summary>The words so far, while the user is still talking.</summary>
    public event Action<string> Partial;
    /// <summary>Nothing usable was heard, and why, in a few words for the user.</summary>
    public event Action<string> Failed;

    // Android's answer to the permission request, as in UserLocation.
    const int Granted = 1, Denied = 2;
    // SpeechRecognizer.ERROR_*.
    const int ErrorNetworkTimeout = 1, ErrorNetwork = 2, ErrorAudio = 3, ErrorServer = 4, ErrorSpeechTimeout = 6,
              ErrorNoMatch = 7, ErrorBusy = 8, ErrorPermissions = 9, ErrorLanguageNotSupported = 12,
              ErrorLanguageUnavailable = 13;
    const float EditorDelay = 0.8f;

    readonly Queue<Action> m_Queue = new Queue<Action>();
    volatile int m_Answer;
    bool m_Asking;
    bool m_PreferOffline = true;
    float m_EditorHeardAt = -1f;

#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject m_Activity;
    AndroidJavaObject m_Recognizer;
    int m_Available = -1;
#endif

    /// <summary>The phone has a speech recogniser (the editor pretends it has).</summary>
    public bool Available
    {
        get
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Available < 0)
            {
                try
                {
                    using (var recognizer = new AndroidJavaClass("android.speech.SpeechRecognizer"))
                        m_Available = recognizer.CallStatic<bool>("isRecognitionAvailable", Activity) ? 1 : 0;
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Voice] No speech recogniser: {e.Message}");
                    m_Available = 0;
                }
            }
            return m_Available == 1;
#else
            return true;
#endif
        }
    }

    /// <summary>Starts listening, asking for the microphone first if it must. Ends in Heard or Failed.</summary>
    public void Listen()
    {
        if (IsListening || m_Asking)
            return;
        IsListening = true;
        Level = 0f;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
        {
            m_Asking = true;
            var callbacks = new PermissionCallbacks();
            callbacks.PermissionGranted += _ => m_Answer = Granted;
            callbacks.PermissionDenied += _ => m_Answer = Denied;
            Permission.RequestUserPermission(Permission.Microphone, callbacks);
            return;
        }
        StartRecognizer();
#else
        m_EditorHeardAt = Time.unscaledTime + EditorDelay;
#endif
    }

    /// <summary>Stops listening and takes what was said so far (Heard, or Failed if nothing).</summary>
    public void Stop()
    {
        if (!IsListening)
            return;
#if UNITY_ANDROID && !UNITY_EDITOR
        OnUiThread(() => m_Recognizer?.Call("stopListening"));
#else
        m_EditorHeardAt = Time.unscaledTime;
#endif
    }

    /// <summary>Stops listening and drops what was said: no Heard, no Failed.</summary>
    public void Cancel()
    {
        if (!IsListening)
            return;
        IsListening = false;
        Level = 0f;
        m_EditorHeardAt = -1f;
#if UNITY_ANDROID && !UNITY_EDITOR
        OnUiThread(() => m_Recognizer?.Call("cancel"));
#endif
    }

    void Update()
    {
        if (m_Asking && m_Answer != 0)
        {
            m_Asking = false;
            bool granted = m_Answer == Granted;
            m_Answer = 0;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (granted)
                StartRecognizer();
            else
                Fail("Microphone is off · allow it in Settings");
#endif
        }

        if (m_EditorHeardAt >= 0f && Time.unscaledTime >= m_EditorHeardAt)
        {
            m_EditorHeardAt = -1f;
            Hear(new List<string> { editorUtterance });
        }

        // Callbacks may queue more while one runs; take what's there now.
        int count;
        lock (m_Queue)
            count = m_Queue.Count;
        for (int i = 0; i < count; i++)
        {
            Action action;
            lock (m_Queue)
                action = m_Queue.Dequeue();
            action();
        }
    }

    void Hear(List<string> alternatives)
    {
        IsListening = false;
        Level = 0f;
        if (alternatives.Count == 0 || string.IsNullOrWhiteSpace(alternatives[0]))
            Failed?.Invoke("Didn't catch that");
        else
            Heard?.Invoke(alternatives);
    }

    void Fail(string why)
    {
        IsListening = false;
        Level = 0f;
        Failed?.Invoke(why);
    }

    // From any thread: runs on Unity's, in the next Update.
    void Post(Action action)
    {
        lock (m_Queue)
            m_Queue.Enqueue(action);
    }

    // What to tell the user for each of Android's errors. Offline recognition that fails for want of the language
    // pack is tried online next time.
    void OnError(int error)
    {
        if (!IsListening)
            return;   // cancelled
        switch (error)
        {
            case ErrorNoMatch:
            case ErrorSpeechTimeout:
                Fail("Didn't catch that");
                break;
            case ErrorPermissions:
                Fail("Microphone is off · allow it in Settings");
                break;
            case ErrorBusy:
                Fail("Speech recognition is busy · try again");
                break;
            case ErrorAudio:
                Fail("Couldn't use the microphone");
                break;
            case ErrorNetwork:
            case ErrorNetworkTimeout:
            case ErrorServer:
            case ErrorLanguageNotSupported:
            case ErrorLanguageUnavailable:
                if (m_PreferOffline)
                {
                    m_PreferOffline = false;
                    Fail("Say that again");
                }
                else
                    Fail("Speech recognition needs the internet here");
                break;
            default:
                Fail("Didn't catch that");
                break;
        }
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject Activity
    {
        get
        {
            if (m_Activity == null)
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    m_Activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            return m_Activity;
        }
    }

    void OnUiThread(Action action)
    {
        Activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                string message = e.Message;
                Post(() =>
                {
                    Debug.LogWarning($"[Voice] Speech recogniser: {message}");
                    if (IsListening)
                        Fail("Couldn't start listening");
                });
            }
        }));
    }

    void StartRecognizer()
    {
        bool offline = m_PreferOffline;
        OnUiThread(() =>
        {
            if (m_Recognizer == null)
            {
                using (var recognizers = new AndroidJavaClass("android.speech.SpeechRecognizer"))
                    m_Recognizer = recognizers.CallStatic<AndroidJavaObject>("createSpeechRecognizer", Activity);
                m_Recognizer.Call("setRecognitionListener", new Listener(this));
            }
            using (var intent = new AndroidJavaObject("android.content.Intent",
                       "android.speech.action.RECOGNIZE_SPEECH"))
            {
                Put(intent, "android.speech.extra.LANGUAGE_MODEL", "free_form");
                Put(intent, "android.speech.extra.LANGUAGE", "en-US");
                Put(intent, "android.speech.extra.PARTIAL_RESULTS", true);
                Put(intent, "android.speech.extra.MAX_RESULTS", 5);
                Put(intent, "android.speech.extra.PREFER_OFFLINE", offline);
                m_Recognizer.Call("startListening", intent);
            }
        });
    }

    // Intent.putExtra returns the Intent; the extra reference is let go at once.
    static void Put(AndroidJavaObject intent, string key, object value)
    {
        using (intent.Call<AndroidJavaObject>("putExtra", key, value)) { }
    }

    // A Bundle's recognised strings (SpeechRecognizer.RESULTS_RECOGNITION), best first.
    static List<string> Strings(AndroidJavaObject bundle)
    {
        var strings = new List<string>();
        if (bundle == null)
            return strings;
        using (AndroidJavaObject list = bundle.Call<AndroidJavaObject>("getStringArrayList", "results_recognition"))
        {
            if (list == null)
                return strings;
            int count = list.Call<int>("size");
            for (int i = 0; i < count; i++)
                using (AndroidJavaObject item = list.Call<AndroidJavaObject>("get", i))
                    if (item != null)
                        strings.Add(item.Call<string>("toString"));
        }
        return strings;
    }

    void OnDestroy()
    {
        if (m_Recognizer == null)
            return;
        AndroidJavaObject recognizer = m_Recognizer;
        m_Recognizer = null;
        OnUiThread(() =>
        {
            recognizer.Call("destroy");
            recognizer.Dispose();
        });
    }

    // android.speech.RecognitionListener. Runs on Android's main thread: reads what it needs, then posts.
    // Every method of the interface is here, since a call to a missing one throws. Java calls them by
    // reflection, so they're public and kept from IL2CPP's stripping.
    [Preserve]
    class Listener : AndroidJavaProxy
    {
        readonly VoiceInput m_Owner;

        public Listener(VoiceInput owner) : base("android.speech.RecognitionListener")
        {
            m_Owner = owner;
        }

        [Preserve] public void onReadyForSpeech(AndroidJavaObject parameters) { }
        [Preserve] public void onBeginningOfSpeech() { }
        [Preserve] public void onBufferReceived(AndroidJavaObject buffer) { }
        [Preserve] public void onEndOfSpeech() { }
        [Preserve] public void onEvent(int eventType, AndroidJavaObject parameters) { }
        [Preserve] public void onSegmentResults(AndroidJavaObject segment) { }
        [Preserve] public void onEndOfSegmentedSession() { }
        [Preserve] public void onLanguageDetection(AndroidJavaObject results) { }

        // About -2 to 10 dB.
        [Preserve] public void onRmsChanged(float rms)
        {
            float level = Mathf.Clamp01((rms + 2f) / 12f);
            m_Owner.Post(() => m_Owner.Level = level);
        }

        [Preserve] public void onError(int error)
        {
            m_Owner.Post(() => m_Owner.OnError(error));
        }

        [Preserve] public void onResults(AndroidJavaObject results)
        {
            List<string> heard = Strings(results);
            m_Owner.Post(() =>
            {
                if (m_Owner.IsListening)
                    m_Owner.Hear(heard);
            });
        }

        [Preserve] public void onPartialResults(AndroidJavaObject partial)
        {
            List<string> heard = Strings(partial);
            if (heard.Count > 0 && !string.IsNullOrEmpty(heard[0]))
                m_Owner.Post(() =>
                {
                    if (m_Owner.IsListening)
                        m_Owner.Partial?.Invoke(heard[0]);
                });
        }
    }
#endif
}
