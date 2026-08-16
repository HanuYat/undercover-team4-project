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

    // 뒤에 계정 식별자가 붙는다 — 아래 생성자 주석 참고.
    private const string k_prefsKeyPrefix = "Emote.Loadout.";

    // 계정을 모르는 경로(로그인 전·세션 없이 씬 직접 Play)가 쓸 자리.
    private const string k_unknownOwner = "local";

    // id에 들어갈 수 없는 문자여야 한다. 카탈로그 id는 영숫자·밑줄만 쓴다는 전제.
    private const char k_separator = '|';

    private readonly string[] m_slots = new string[k_slotCount];

    private readonly string m_prefsKey;

    /// <summary>
    /// <paramref name="ownerId"/>는 저장 칸을 가르는 계정 식별자다 — 사용처는 UGS PlayerId를 넘긴다.
    ///
    /// <b>왜 나눠야 하는가:</b> PlayerPrefs는 한 PC에 하나뿐인 저장소라 그 PC의 모든 인스턴스가
    /// 같은 값을 놓고 쓴다. 전역 키 하나로 두면 MPPM 가상 플레이어와 호스트가 서로의 구성을
    /// 덮어써, 로컬 2인 테스트에서 "저장이 안 된다"로 보인다(#640). 한 PC를 여러 계정이 쓸 때도
    /// 같은 문제다. AuthBootstrap이 닉네임·관문 통과를 프로필별로 나눠 두는 것과 같은 이유다.
    ///
    /// <b>왜 여기서 직접 읽지 않는가:</b> 이 어셈블리(Undercover.Emote)는 UGS도 App도 참조하지
    /// 않는다 — 순수 인덱스 연산이라 Unity·Netcode와 무관하게 단위 테스트할 수 있다는 성질을
    /// 지키기 위해, 계정을 아는 쪽이 넘겨주는 형태로 둔다.
    ///
    /// 비워 두면 계정 없는 자리(<c>local</c>)에 저장한다 — 로그인 전 편집도 어딘가에는 남아야 한다.
    /// </summary>
    public EmoteLoadout(string ownerId = null)
    {
        m_prefsKey =
            k_prefsKeyPrefix + (string.IsNullOrWhiteSpace(ownerId) ? k_unknownOwner : ownerId);
    }

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

    /// <summary>
    /// 구성을 로컬에 저장한다 — 칸을 만질 때마다 부른다.
    ///
    /// <paramref name="flush"/>를 끄면 디스크 쓰기(<see cref="PlayerPrefs.Save"/>)를 미룬다.
    /// 값은 이미 PlayerPrefs에 들어가 있어 앱이 정상 종료하거나 다시 읽을 때 그대로 나오므로,
    /// 칸을 누를 때마다 디스크를 두드리지 않기 위한 것이다. 편집을 마칠 때 한 번 flush한다.
    /// </summary>
    public void Save(bool flush = true)
    {
        PlayerPrefs.SetString(m_prefsKey, Serialize());

        if (flush)
            PlayerPrefs.Save();
    }

    /// <summary>저장된 구성을 읽는다. 저장된 적이 없으면 전 칸이 빈 상태로 남는다.</summary>
    public void Load()
    {
        Deserialize(PlayerPrefs.GetString(m_prefsKey, string.Empty));
    }
}
