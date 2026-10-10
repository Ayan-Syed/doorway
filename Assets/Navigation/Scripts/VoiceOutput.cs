using System;
using UnityEngine;
using UnityEngine.Scripting;

// Text to speech with Android's own engine (android.speech.tts.TextToSpeech): offline, in the voice the phone
// is set to. No plugin: JNI calls, like Haptics.
//
// When an utterance ends isn't reported here (UtteranceProgressListener is an abstract class, which a proxy
// can't implement), so IsSpeaking asks the engine, a few times a second. The conversation waits on it before
// listening again, so the microphone never hears the app's own voice.
// In the editor the words go to the console, and IsSpeaking lasts about as long as saying them would.
public class VoiceOutput : MonoBehaviour
{
    [Tooltip("1 is the engine's normal pace.")]
    public float speechRate = 1.05f;

    // TextToSpeech.SUCCESS, QUEUE_FLUSH, QUEUE_ADD.
    const int Success = 0, QueueFlush = 0, QueueAdd = 1;
    // The engine can take this long to report it has started.
    const float StartGrace = 0.5f;
    const float PollSeconds = 0.1f;
    const float EditorWordsPerSecond = 2.6f;

    volatile int m_Status = -1;   // set on Android's thread by onInit
    bool m_Ready;
    string m_Waiting;             // said before the engine was ready
    float m_SpokeAt = -10f;
    float m_EditorUntil;
    float m_NextPoll;
    bool m_Speaking;
    int m_Count;

#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject m_Tts;

    void Awake()
    {
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                m_Tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, new InitListener(this));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Voice] No text to speech: {e.Message}");
        }
    }

    [Preserve]
    class InitListener : AndroidJavaProxy
    {
        readonly VoiceOutput m_Owner;

        public InitListener(VoiceOutput owner) : base("android.speech.tts.TextToSpeech$OnInitListener")
        {
            m_Owner = owner;
        }

        [Preserve] public void onInit(int status) => m_Owner.m_Status = status;
    }
#endif

    /// <summary>Still saying something.</summary>
    public bool IsSpeaking
    {
        get
        {
            float now = Time.unscaledTime;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (m_Waiting != null)
                return true;
            if (now < m_SpokeAt + StartGrace)
                return true;
            if (m_Tts != null && m_Ready && now >= m_NextPoll)
            {
                m_NextPoll = now + PollSeconds;
                try
                {
                    m_Speaking = m_Tts.Call<bool>("isSpeaking");
                }
                catch (Exception)
                {
                    m_Speaking = false;
                }
            }
            return m_Speaking;
#else
            return now < m_EditorUntil;
#endif
        }
    }

    /// <summary>Says it, cutting off whatever was being said, or after it with interrupt false.</summary>
    public void Say(string text, bool interrupt = true)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        m_SpokeAt = Time.unscaledTime;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!m_Ready)
        {
            m_Waiting = interrupt || m_Waiting == null ? text : m_Waiting + " " + text;
            return;
        }
        Speak(text, interrupt);
#else
        Debug.Log($"[Voice] {text}");
        float seconds = text.Split(' ').Length / EditorWordsPerSecond;
        m_EditorUntil = (interrupt ? Time.unscaledTime : Mathf.Max(Time.unscaledTime, m_EditorUntil)) + seconds;
#endif
    }

    public void Stop()
    {
        m_Waiting = null;
        m_EditorUntil = 0f;
        m_SpokeAt = -10f;
#if UNITY_ANDROID && !UNITY_EDITOR
        if (m_Tts != null && m_Ready)
            m_Tts.Call<int>("stop");
        m_Speaking = false;
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    void Update()
    {
        if (m_Ready || m_Status < 0 || m_Tts == null)
            return;
        if (m_Status != Success)
        {
            Debug.LogWarning($"[Voice] Text to speech didn't start ({m_Status}).");
            m_Tts = null;
            m_Waiting = null;
            return;
        }
        m_Ready = true;
        try
        {
            using (var locales = new AndroidJavaClass("java.util.Locale"))
            using (AndroidJavaObject us = locales.GetStatic<AndroidJavaObject>("US"))
                m_Tts.Call<int>("setLanguage", us);
            m_Tts.Call<int>("setSpeechRate", speechRate);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Voice] Text to speech settings: {e.Message}");
        }
        if (m_Waiting != null)
        {
            string waiting = m_Waiting;
            m_Waiting = null;
            Speak(waiting, true);
        }
    }

    void Speak(string text, bool interrupt)
    {
        if (m_Tts == null)
            return;
        try
        {
            using (var parameters = new AndroidJavaObject("android.os.Bundle"))
                m_Tts.Call<int>("speak", text, interrupt ? QueueFlush : QueueAdd, parameters, "doorway-" + ++m_Count);
            m_SpokeAt = Time.unscaledTime;
            m_Speaking = true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Voice] Couldn't speak: {e.Message}");
        }
    }

    // The voice stops when the app goes behind another.
    void OnApplicationPause(bool paused)
    {
        if (paused)
            Stop();
    }

    void OnDestroy()
    {
        if (m_Tts == null)
            return;
        try
        {
            m_Tts.Call<int>("stop");
            m_Tts.Call("shutdown");
        }
        catch (Exception)
        {
            // Going away anyway.
        }
        m_Tts.Dispose();
        m_Tts = null;
    }
#endif
}
