using System;
using UnityEngine;

/// <summary>
/// 감정표현 휠 8칸의 배치 — 어느 칸에 어떤 감정표현을 넣어 뒀는가. (#219)
///
/// <b>로컬 관심사다.</b> 네트워크로 올리지 않는다 — 남이 볼 필요가 있는 것은 "지금 무엇을
/// 재생 중인가"(<see cref="PlayerEmote"/>)뿐이고, 내가 몇 번 칸에 뭘 넣어 뒀는지는 아무도
/// 알 필요가 없다.
///
/// <b>인덱스가 아니라 id 문자열을 담는 이유:</b> 카탈로그 순서가 바뀌어도 사용자가 공들여
/// 맞춘 구성이 살아남아야 한다. 재생 동기화 쪽은 반대로 인덱스를 쓴다 — 그쪽은 1바이트로
/// 끝내는 것이 이득이고 같은 빌드끼리만 통신하므로 순서가 바뀔 일이 없다.
///
/// MonoBehaviour가 아닌 이유는 <see cref="LoadoutSlots{T}"/>와 같다 — 순수 인덱스 연산이라
/// Unity·Netcode와 무관하게 단위 테스트할 수 있다.
///
/// <b>카탈로그를 모른다.</b> 저장된 id가 지금 카탈로그에 있는지는 읽는 쪽이 판정한다.
/// 여기서 검증하면 이 클래스가 카탈로그에 묶여 순수 로직이 아니게 된다.
/// </summary>
public class EmoteLoadout
{
    /// <summary>휠 칸 수 — EmoteWheelGeometry.k_slotCount와 같아야 한다.</summary>
    public const int k_slotCount = EmoteWheelGeometry.k_slotCount;

    private const string k_prefsKey = "Emote.Loadout";

    // id에 들어갈 수 없는 문자여야 한다. 카탈로그 id는 영숫자·밑줄만 쓴다는 전제.
    private const char k_separator = '|';

    private readonly string[] m_slots = new string[k_slotCount];

    /// <summary>칸에 든 감정표현 id — 비었거나 범위 밖이면 null.</summary>
    public string GetSlot(int slot) => slot >= 0 && slot < k_slotCount ? m_slots[slot] : null;

    /// <summary>칸을 채우거나(id) 비운다(null·빈 문자열). 범위 밖이면 아무것도 하지 않는다.</summary>
    public void SetSlot(int slot, string emoteId)
    {
        if (slot < 0 || slot >= k_slotCount)
            return;

        m_slots[slot] = string.IsNullOrEmpty(emoteId) ? null : emoteId;
    }

    /// <summary>PlayerPrefs에 담을 한 줄 문자열로 만든다.</summary>
    public string Serialize()
    {
        var parts = new string[k_slotCount];
        for (int slot = 0; slot < k_slotCount; slot++)
            parts[slot] = m_slots[slot] ?? string.Empty;

        return string.Join(k_separator.ToString(), parts);
    }

    /// <summary>
    /// 저장 문자열에서 구성을 복원한다. 칸 수가 맞지 않아도 살아남는다 —
    /// 모자라면 남은 칸을 비우고, 넘치면 버린다. 저장 포맷이 바뀌거나 값이 잘렸을 때
    /// 예외로 죽는 대신 기본 구성으로 계속 굴러가는 쪽이 낫다.
    /// </summary>
    public void Deserialize(string raw)
    {
        Array.Clear(m_slots, 0, k_slotCount);

        if (string.IsNullOrEmpty(raw))
            return;

        string[] parts = raw.Split(k_separator);
        int count = Mathf.Min(parts.Length, k_slotCount);
        for (int slot = 0; slot < count; slot++)
            SetSlot(slot, parts[slot]);
    }

    /// <summary>구성을 로컬에 저장한다 — 로비에서 편집을 마칠 때 부른다.</summary>
    public void Save()
    {
        PlayerPrefs.SetString(k_prefsKey, Serialize());
        PlayerPrefs.Save();
    }

    /// <summary>저장된 구성을 읽는다. 저장된 적이 없으면 전 칸이 빈 상태로 남는다.</summary>
    public void Load()
    {
        Deserialize(PlayerPrefs.GetString(k_prefsKey, string.Empty));
    }
}
