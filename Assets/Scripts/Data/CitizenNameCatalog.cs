using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Localization.Settings;

/// <summary>
/// 시민 이름 풀 (#752) — 한글·영문 목록을 나눠 담는다. 팩토리가 라운드 시작에 한 번 읽어 섞는다.
///
/// <b>어느 목록을 쓸지는 서버가 정하고, 그 판의 이름 언어는 호스트 언어로 굳는다.</b> 이름은
/// <see cref="CitizenData.Name"/>으로 동기화되는 대조 판별자라(수배 리스트 #58 · 인명부 #223)
/// 클라가 각자 자기 언어로 풀면 같은 NPC를 서로 다른 이름으로 보게 된다. 이름 위조(#223)가
/// <b>글자를 바꾸는 것</b>이라 인덱스만 보내고 받는 쪽에서 조회하는 방식(#497 결정 (g))도 쓸 수 없다.
/// </summary>
[CreateAssetMenu(fileName = "CitizenNames", menuName = "Scriptable Objects/CitizenNameCatalog")]
public class CitizenNameCatalog : ScriptableObject
{
    [Tooltip("한국어 로케일에서 쓸 이름")]
    [SerializeField] private string[] m_korean;

    [Tooltip("그 밖의 로케일에서 쓸 이름")]
    [SerializeField] private string[] m_english;

    public int KoreanCount => m_korean != null ? m_korean.Length : 0;

    public int EnglishCount => m_english != null ? m_english.Length : 0;

    /// <summary>지금 언어에 맞는 목록. 그쪽이 비어 있으면 다른 쪽으로 폴백하고, 둘 다 비면 빈 배열이다.</summary>
    public string[] Resolve()
    {
        bool korean = IsKoreanLocale();
        string[] first = korean ? m_korean : m_english;
        string[] second = korean ? m_english : m_korean;

        if (first != null && first.Length > 0)
            return first;

        // 한쪽만 채워 둔 에셋에서도 이름이 나오게 한다 — 언어를 늘리는 중에 빈 화면을 보지 않으려는 것이다
        if (second != null && second.Length > 0)
            return second;

        return Array.Empty<string>();
    }

    // LocalizationSettings가 아직 없을 수 있다(에디터 초기화 전·테스트) — LocalizedStrings와 같은 가드다
    private static bool IsKoreanLocale()
    {
        if (!LocalizationSettings.HasSettings)
            return false;

        var locale = LocalizationSettings.SelectedLocale;
        return locale != null && locale.Identifier.Code.StartsWith("ko");
    }

#if UNITY_EDITOR
    // 같은 이름이 두 번 들어가면 한 라운드에 동명이인이 생겨 인명부 대조(#223)가 무의미해진다.
    // 풀은 섞기만 하고 중복을 걸러내지 않으므로 넣는 시점에 잡는다. (#752)
    private void OnValidate()
    {
        WarnDuplicates(m_korean, nameof(m_korean));
        WarnDuplicates(m_english, nameof(m_english));
    }

    private void WarnDuplicates(string[] names, string listName)
    {
        if (names == null)
            return;

        var seen = new HashSet<string>();
        for (int i = 0; i < names.Length; i++)
        {
            if (!string.IsNullOrEmpty(names[i]) && !seen.Add(names[i]))
                Debug.LogWarning($"[{name}] {listName}에 중복된 이름이 있다: {names[i]}", this);
        }
    }
#endif
}
