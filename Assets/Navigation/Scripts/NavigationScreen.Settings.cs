using System;
using UnityEngine;
using UnityEngine.UI;

// Settings: your marker on the map, light or dark, the 3D map, and a pretend location for demos.
public partial class NavigationScreen
{
    // Only from Home or Place.
    void OpenSettings()
    {
        if (m_SwallowSettingsTap)
        {
            m_SwallowSettingsTap = false;   // the long press that turned the debug overlay on or off
            return;
        }
        if (m_Step > Step.Place)
            return;
        StyleLeftBuilding();
        if (searchField.isFocused)
            searchField.DeactivateInputField();
        SetFloorMenu(false);
        m_SettingsOpen = true;
        settingsPanel.blocksRaycasts = true;
        StyleSettings();
        Haptics.Tick(15);
        if (settingsPanel.gameObject.activeSelf)
            settingsSheet.MoveTo(1, false);   // it was still sliding shut
        else
        {
            settingsPanel.gameObject.SetActive(true);
            settingsSheet.Present(1);
        }
        if (!Application.isPlaying)
            settingsScrim.alpha = 1f;
    }

    void CloseSettings()
    {
        if (!m_SettingsOpen)
            return;
        m_SettingsOpen = false;
        settingsPanel.blocksRaycasts = false;   // The map takes taps again at once, while the sheet slides away.
        settingsSheet.MoveTo(0, !Application.isPlaying);
        if (!Application.isPlaying)
            settingsPanel.gameObject.SetActive(false);   // on a phone, Update does it once the sheet is down
    }

    // 0 is the blue dot; n is markerSprites[n - 1]. It's the dot on the map and the user on the minimap.
    void SetMarker(int marker, bool save)
    {
        int sprites = markerSprites == null ? 0 : markerSprites.Length;
        m_Marker = Mathf.Clamp(marker, 0, sprites);
        Sprite sprite = m_Marker == 0 ? null : markerSprites[m_Marker - 1];
        map.SetMarker(sprite);
        miniMap.SetMarker(sprite);
        if (save && Application.isPlaying)
        {
            PlayerPrefs.SetInt(MarkerKey, m_Marker);
            PlayerPrefs.Save();
            Haptics.Tick(15);
        }
        StyleSettings();
    }

    // Auto, Light or Dark (0, 1, 2).
    void SetAppearance(int mode)
    {
        m_Appearance = mode;
        if (Application.isPlaying)
        {
            PlayerPrefs.SetInt(AppearanceKey, mode);
            PlayerPrefs.Save();
            Haptics.Tick(15);
        }
        ApplyAppearance(false);
        StyleSettings();
    }

    // Recolours everything for day or night, if that changed (or always, with force).
    void ApplyAppearance(bool force)
    {
        bool night = m_Appearance == AppearanceDark || m_Appearance == AppearanceAuto && SystemIsDark();
        if (night == m_Night && !force)
            return;
        m_Night = night;
        Theme.Set(night ? nightThemeName : themeName);
        if (themeColors != null)
            themeColors.Apply(night);
        map.Restyle();
        miniMap.Restyle();
        homeList.Restyle();
        startList.Restyle();
        StyleChips();
        navigator.ApplyTheme(Theme.Current);
        if (uiCamera != null)
            uiCamera.backgroundColor = Theme.Current.mapGround;
        StyleSettings();
        StyleEnd(true);
    }

    // While on Auto, the phone may switch between light and dark (at sunset, say) with the app in front, where
    // focus never changes. Reading the setting is a few JNI calls, so every couple of seconds is plenty.
    void UpdateSystemTheme()
    {
        if (m_Appearance != AppearanceAuto || Time.unscaledTime < m_NextThemeCheck)
            return;
        m_NextThemeCheck = Time.unscaledTime + SystemThemeSeconds;
        ApplyAppearance(false);
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    static bool s_DarkModeFailed;
#endif

    // Android's night bit in the UI mode. Light everywhere else, so the editor's Auto is Light.
    static bool SystemIsDark()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (s_DarkModeFailed)
            return false;   // it failed once: don't fill the log every couple of seconds
        try
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var resources = activity.Call<AndroidJavaObject>("getResources"))
            using (var configuration = resources.Call<AndroidJavaObject>("getConfiguration"))
                return (configuration.Get<int>("uiMode") & UiModeNightMask) == UiModeNightYes;
        }
        catch (Exception e)
        {
            s_DarkModeFailed = true;
            Debug.LogWarning($"[Navigation] Couldn't read dark mode: {e.Message}");
        }
#endif
        return false;
    }

    // Loading it is instant; a tap animates the tilt.
    void Set3D(bool on, bool save)
    {
        m_Map3D = on;
        map.SetTilt(on ? MapTilt : 0f, !save);
        miniMap.SetTilt(on ? MiniMapTilt : 0f, !save);
        if (save && Application.isPlaying)
        {
            PlayerPrefs.SetInt(Map3DKey, on ? 1 : 0);
            PlayerPrefs.Save();
            Haptics.Tick(15);
        }
        StyleSettings();
    }

    // For demos away from the building: the phone's location becomes its first entrance.
    void SetPretend(bool on, bool save)
    {
        PlanPlace entrance = PretendEntrance();
        m_Pretend = on && entrance != null;
        // Loading with it off never pretends; turning it off by tap stops pretending.
        if (location != null && Application.isPlaying && (m_Pretend || save))
            location.Pretend(m_Pretend ? Plan.NodePosition(entrance.doors[0].node) : (Vector2?)null);
        if (save && Application.isPlaying)
        {
            PlayerPrefs.SetInt(PretendKey, m_Pretend ? 1 : 0);
            PlayerPrefs.Save();
            Haptics.Tick(15);
        }
        StyleSettings();
    }

    // The first entrance in the plan with a door.
    PlanPlace PretendEntrance()
    {
        return Plan.places.Find(p => p.kind == "exit" && p.doors.Count > 0);
    }

    // What people call a building: "BA1" for "Business Administration I".
    static string ShortName(string building)
    {
        if (string.IsNullOrEmpty(building))
            return building;
        string[] words = building.Split(' ');
        if (words.Length < 2)
            return building;
        string initials = "";
        int number = 0;
        foreach (string word in words)
        {
            int roman = Array.IndexOf(RomanNumerals, word);
            if (roman >= 0)
                number = roman + 1;
            else if (word.Length > 0 && char.IsUpper(word[0]))
                initials += word[0];
        }
        return number > 0 ? initials + number : initials;
    }

    void StyleSettings()
    {
        Theme theme = Theme.Current;
        for (int i = 0; i < markerBackgrounds.Length; i++)
            markerBackgrounds[i].color = i == m_Marker ? theme.primaryContainer : theme.surfaceVariant;
        for (int i = 0; i < appearanceBackgrounds.Length; i++)
        {
            bool on = i == m_Appearance;
            appearanceBackgrounds[i].color = on ? theme.primaryContainer : Color.clear;
            if (i < appearanceLabels.Length)
                appearanceLabels[i].color = on ? theme.primary : theme.onSurfaceVariant;
        }
        StyleSwitch(switchTrack, switchKnob, m_Map3D);
        StyleSwitch(pretendTrack, pretendKnob, m_Pretend);
    }

    static void StyleSwitch(Image track, Image knob, bool on)
    {
        Theme theme = Theme.Current;
        track.color = on ? theme.primary : theme.surfaceVariant;
        knob.color = on ? theme.onPrimary : theme.outline;
        RectTransform rect = knob.rectTransform;
        rect.anchoredPosition = new Vector2(on ? KnobTravel : -KnobTravel, rect.anchoredPosition.y);
    }
}
