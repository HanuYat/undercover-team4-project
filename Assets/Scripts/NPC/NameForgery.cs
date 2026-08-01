using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

/// <summary>
/// 이름 위조 (#223) — 표시 이름의 글자를 같은 종류로 바꿔 정본과 어긋나게 만든다.
///
/// 위조범은 이름과 문양 중 <b>하나만</b> 오염된다 (#222 (a)①) — 본부가 "이름이 안 맞나 문양이
/// 안 맞나"를 매번 새로 대조하게 만들기 위해서다. 문양 쪽은 세력 variant를 다루므로
/// <see cref="CitizenProfileFactory"/>가 들고, 이름 쪽 규칙만 여기 있다.
///
/// Unity 의존이 <see cref="Random"/>뿐이라 단독 호출·테스트가 가능하다.
/// </summary>
public static class NameForgery
{
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
    /// 이름의 알파벳 중 count글자를 같은 종류(모음↔모음, 자음↔자음)의 다른 글자로 치환한다. (#223)
    /// 발음 가능한 자연스러운 이름을 유지한 채 정본과 어긋나게 만든다 — 대소문자 보존, 공백/기호는 건너뛴다.
    /// count가 이름의 알파벳 수보다 크면 가능한 만큼만 오염한다.
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

    /// <summary>알파벳 한 글자를 같은 종류(모음↔모음, 자음↔자음)의 다른 글자로 치환한다. 대소문자 보존. (#223)</summary>
    private static char SubstituteSameClass(char original)
    {
        char lower = char.ToLowerInvariant(original);
        char[] pool = Array.IndexOf(s_vowels, lower) >= 0 ? s_vowels : s_consonants;

        char replacement;
        do
        {
            replacement = pool[Random.Range(0, pool.Length)];
        } while (replacement == lower);

        return char.IsUpper(original) ? char.ToUpperInvariant(replacement) : replacement;
    }
}
