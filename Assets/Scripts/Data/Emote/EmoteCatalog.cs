using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 감정표현 전체 목록. (#219)
///
/// <b>리스트 인덱스가 네트워크 계약이다.</b> 재생 동기화는 이 인덱스를 sbyte로 실어 1바이트로
/// 끝낸다 — 모든 피어가 같은 빌드의 같은 에셋을 갖고 있으므로 문자열 id를 보낼 이유가 없다.
///
/// 그래서 <b>배포 후에는 순서를 바꾸지 않는다.</b> 항목은 뒤에 추가하고, 없앨 때는 자리를
/// 비운다(null 허용 — 조회가 무시한다). 개발 중에는 자유롭게 바꿔도 되지만, 순서가 바뀐
/// 빌드끼리는 서로 다른 감정표현을 재생하게 된다.
///
/// 로비 구성 저장은 반대로 id를 쓰므로(<see cref="EmoteLoadout"/>) 순서 변경에 견딘다.
/// 두 표현을 잇는 것이 <see cref="IndexOf"/>다.
/// </summary>
[CreateAssetMenu(fileName = "EmoteCatalog", menuName = "Scriptable Objects/Emote Catalog")]
public class EmoteCatalog : ScriptableObject
{
    [Tooltip("인덱스가 네트워크 계약 — 배포 후에는 순서를 바꾸지 말 것. 추가는 뒤에")]
    [SerializeField]
    private EmoteDefinition[] m_emotes;

    // id → 인덱스. 첫 조회 때 만든다 — 에셋 로드 시점에 만들면 도메인 리로드마다 비용이 든다.
    private Dictionary<string, int> m_indexById;

    public int Count => m_emotes?.Length ?? 0;

    public bool IsValidIndex(int index) => index >= 0 && index < Count && m_emotes[index] != null;

    /// <summary>인덱스로 정의를 얻는다 — 범위 밖이거나 비워 둔 자리면 null.</summary>
    public EmoteDefinition Get(int index) => IsValidIndex(index) ? m_emotes[index] : null;

    /// <summary>id로 인덱스를 찾는다 — 없으면 -1. 로비 구성(id)을 재생(인덱스)으로 잇는 지점.</summary>
    public int IndexOf(string id)
    {
        if (string.IsNullOrEmpty(id) || Count == 0)
            return -1;

        if (m_indexById == null)
        {
            m_indexById = new Dictionary<string, int>(Count);
            for (int i = 0; i < m_emotes.Length; i++)
            {
                if (m_emotes[i] == null || string.IsNullOrEmpty(m_emotes[i].Id))
                    continue;

                // 중복 id는 앞선 것을 남긴다 — 뒤엣것을 덮으면 어느 쪽이 이겼는지 알기 어렵다
                if (!m_indexById.ContainsKey(m_emotes[i].Id))
                    m_indexById.Add(m_emotes[i].Id, i);
            }
        }

        return m_indexById.TryGetValue(id, out int index) ? index : -1;
    }
}
