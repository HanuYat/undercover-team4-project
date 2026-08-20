using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 이름 위조 (#223) — 표시 이름의 글자를 같은 종류로 바꿔 정본과 어긋나게 만든다.
///
/// <b>한글은 자모 단위로 바꾼다</b> (#752). 알파벳 규칙을 그대로 적용하면 <c>char.IsLetter('김')</c>이
/// true라 한글이 위조 위치로 뽑히고 라틴 자음으로 치환돼 <c>"김서준" → "x서준"</c>이 된다 —
/// 본부가 눈으로 즉시 위조를 판별해버려 대조 자체가 무의미해진다.
///
/// 위조범은 이름과 문양 중 <b>하나만</b> 오염된다 (#222 (a)①) — 본부가 "이름이 안 맞나 문양이
/// 안 맞나"를 매번 새로 대조하게 만들기 위해서다. 문양 쪽은 세력 variant를 다루므로
/// <see cref="CitizenProfileFactory"/>가 들고, 이름 쪽 규칙만 여기 있다.
///
/// Unity 의존이 <see cref="Random"/>뿐이라 단독 호출·테스트가 가능하다.
/// </summary>
public static class NameForgery
{
    // 한글 음절은 유니코드 AC00~D7A3에 (초성 19 × 중성 21 × 종성 28) 순서로 규칙적으로 배열돼 있다.
    // 그래서 코드값 산술만으로 분해·재조립이 된다 — 자모 표를 따로 두지 않는다. (#752)
    private const char k_hangulFirst = '\uAC00';
    private const char k_hangulLast = '\uD7A3';
    private const int k_jungCount = 21;
    private const int k_jongCount = 28;

    // 자모 후보는 <b>이름에 쓰이는 것으로 좁힌다.</b> 전체 자모에서 고르면 쌍자음·복합모음이 섞여
    // "김뻐준"·"김서좬"처럼 이름으로 읽히지 않는 글자가 나오는데, 그러면 본부가 인명부와 대조하지 않고
    // 눈으로 걸러버려 위조가 무의미해진다 — 라틴 문자가 섞였을 때와 같은 실패다. (#752)
    private static readonly int[] s_choCandidates =
    {
        0, 2, 3, 5, 6, 7, 9, 11, 12, 14, 15, 16, 17, 18, // ㄱㄴㄷㄹㅁㅂㅅㅇㅈㅊㅋㅌㅍㅎ (쌍자음 제외)
    };

    private static readonly int[] s_jungCandidates =
    {
        0, 1, 2, 4, 5, 6, 7, 8, 12, 13, 17, 18, 20, // ㅏㅐㅑㅓㅔㅕㅖㅗㅛㅜㅠㅡㅣ (복합모음 제외)
    };

    // 홑받침만 — 겹받침(ㄳㄵㄺ…)은 이름에 거의 없다. 0(종성 없음)은 애초에 후보가 아니다
    private static readonly int[] s_jongCandidates = { 1, 4, 8, 16, 17, 19, 21 }; // ㄱㄴㄹㅁㅂㅅㅇ

    private static readonly char[] s_vowels = { 'a', 'e', 'i', 'o', 'u' };

    private static readonly char[] s_consonants =
    {
        'b',
        'c',
        'd',
        'f',
        'g',
        'h',
        'j',
        'k',
        'l',
        'm',
        'n',
        'p',
        'q',
        'r',
        's',
        't',
        'v',
        'w',
        'x',
        'y',
        'z',
    };

    /// <summary>
    /// 이름의 글자 중 count글자를 같은 종류의 다른 글자로 치환한다. (#223)
    /// 알파벳은 모음↔모음·자음↔자음(대소문자 보존), 한글은 자모 하나를 같은 자리에서 바꾼다 (#752).
    /// 발음 가능한 자연스러운 이름을 유지한 채 정본과 어긋나게 만든다 — 공백/기호는 건너뛴다.
    /// count가 이름의 글자 수보다 크면 가능한 만큼만 오염한다.
    /// </summary>
    public static string Corrupt(string name, int count)
    {
        if (string.IsNullOrEmpty(name) || count <= 0)
            return name;

        // 알파벳 위치만 모아 셔플 → 앞에서 count개 = 중복 없는 오염 위치
        var positions = new List<int>();
        for (int i = 0; i < name.Length; i++)
            if (char.IsLetter(name[i]))
                positions.Add(i);
        if (positions.Count == 0)
            return name;

        for (int i = positions.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (positions[i], positions[j]) = (positions[j], positions[i]);
        }

        int corruptCount = Mathf.Min(count, positions.Count);
        char[] chars = name.ToCharArray();
        for (int k = 0; k < corruptCount; k++)
            chars[positions[k]] = SubstituteSameClass(chars[positions[k]]);

        return new string(chars);
    }

    /// <summary>한 글자를 같은 종류의 다른 글자로 치환한다 — 한글이면 자모, 알파벳이면 모음/자음. (#223/#752)</summary>
    private static char SubstituteSameClass(char original)
    {
        if (IsHangulSyllable(original))
            return SubstituteHangul(original);

        char lower = char.ToLowerInvariant(original);
        char[] pool = Array.IndexOf(s_vowels, lower) >= 0 ? s_vowels : s_consonants;

        char replacement;
        do
        {
            replacement = pool[Random.Range(0, pool.Length)];
        } while (replacement == lower);

        return char.IsUpper(original) ? char.ToUpperInvariant(replacement) : replacement;
    }

    private static bool IsHangulSyllable(char c) => c >= k_hangulFirst && c <= k_hangulLast;

    /// <summary>
    /// 한글 한 글자에서 <b>자모 하나만</b> 바꾼다 — 초성·중성·(있으면)종성 중 하나를 같은 자리의
    /// 다른 값으로 돌린다. "김서준" → "김서춘" / "김소준" 처럼 읽히는 이름이 유지된다. (#752)
    ///
    /// <b>없던 종성은 새로 붙이지 않는다</b> — "서" → "선"처럼 원래 이름에 없던 소리가 생기면
    /// 글자 수가 같아도 어색해진다. 종성은 이미 있는 것만 다른 종성으로 바꾼다.
    /// </summary>
    private static char SubstituteHangul(char original)
    {
        int index = original - k_hangulFirst;
        int cho = index / (k_jungCount * k_jongCount);
        int jung = index / k_jongCount % k_jungCount;
        int jong = index % k_jongCount;

        // 종성이 없으면 초성·중성 둘 중에서 고른다
        switch (Random.Range(0, jong > 0 ? 3 : 2))
        {
            case 0:
                cho = OtherFrom(cho, s_choCandidates);
                break;
            case 1:
                jung = OtherFrom(jung, s_jungCandidates);
                break;
            default:
                jong = OtherFrom(jong, s_jongCandidates);
                break;
        }

        return (char)(k_hangulFirst + (cho * k_jungCount + jung) * k_jongCount + jong);
    }

    // 후보 중 current가 아닌 값 하나. 원래 글자가 후보 밖(쌍자음 등)이어도 상관없다 —
    // 고르는 쪽만 좁히면 되고, 그 글자는 다른 자리가 뽑혔을 때 그대로 남는다.
    private static int OtherFrom(int current, int[] candidates)
    {
        int picked;
        do
        {
            picked = candidates[Random.Range(0, candidates.Length)];
        } while (picked == current);

        return picked;
    }
}
