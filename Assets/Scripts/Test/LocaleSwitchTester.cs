using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Localization.Settings;

/// <summary>F10으로 ko-KR ↔ en 토글 — Localization 검증용 임시 스위처 (#251)</summary>
public class LocaleSwitchTester : MonoBehaviour
{
    private void Update()
    {
        if (Keyboard.current != null && Keyboard.current.f10Key.wasPressedThisFrame)
        {
            var locales = LocalizationSettings.AvailableLocales.Locales;
            int current = locales.IndexOf(LocalizationSettings.SelectedLocale);
            var next = locales[(current + 1) % locales.Count];

            LocalizationSettings.SelectedLocale = next;
            PlayerPrefLocaleSelector.Save(next);
        }
    }
}
