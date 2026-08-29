using TMPro;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 감옥 간판 — 컨테이너 위에 붙어 <b>여기가 감옥이라는 것</b>과 <b>지금 몇 명 들어 있는지</b>를 보여준다. (#537)
///
/// <b>왜 필요해졌나.</b> 감옥이 격리 공간이 되면서(#537) 도시에 남은 것은 문과 벽뿐이라, 밖에서는
/// 안이 보이지 않는다 — "저 상자가 감옥이다"도, "지금 몇 명 잡아 뒀다"도 읽을 방법이 없어졌다.
/// 본부는 CCTV 채널로 안을 보지만 현장 인원에게는 그 화면이 없다. 간판이 그 자리를 메운다.
///
/// <b>전 피어에서 각자 갱신한다.</b> <see cref="JailZone.InmateCount"/>가 이미 NetworkVariable로
/// 동기화돼 있고(#56) <see cref="JailZone.OnInmateCountChanged"/>가 모든 피어에서 발생하므로,
/// 간판을 위해 새로 동기화할 것이 없다 — 각 피어가 자기 화면의 글자를 자기가 고쳐 쓴다.
///
/// 씬 배치: 월드 스페이스 Canvas 아래 <see cref="TMP_Text"/> 하나를 두고 이 컴포넌트를 붙인다.
/// 글자가 뒤집혀 보이지 않게 Canvas의 Z축(forward)이 <b>읽는 사람 쪽</b>을 향하게 둘 것.
/// </summary>
public class JailSignboard : MonoBehaviour
{
    [Header("감옥 (비우면 씬에서 자동 탐색)")]
    [SerializeField] private JailZone m_jailZone;

    [Header("표시 대상 (비우면 자신·자식에서 자동 탐색)")]
    [SerializeField] private TMP_Text m_label;

    [Tooltip("표시 형식 — WorldTable/World.Jail.Signboard ({0}에 현재 수감 인원이 들어간다)")]
    [SerializeField] private LocalizedString m_format;

    private void Awake()
    {
        if (m_label == null)
            m_label = GetComponentInChildren<TMP_Text>();

        // 비워두면 App에서 받는다 — 감옥 시설은 실행 순서가 앞서 있어 여기서 이미 읽힌다 (#592)
        if (m_jailZone == null)
            m_jailZone = GetComponentInParent<JailZone>();
        if (m_jailZone == null)
            m_jailZone = App.Game.Jail;

        if (m_label == null)
            Debug.LogWarning("JailSignboard: 표시할 TMP_Text가 없다 — 간판이 갱신되지 않는다", this);
    }

    // 구독은 Start에서 — 감옥의 Awake(NetworkVariable 초기화)가 끝난 뒤가 보장된다 (아키텍처 규칙 R6)
    private void Start()
    {
        if (m_jailZone == null)
            Debug.LogWarning("JailSignboard: 감옥(JailZone)을 찾지 못했다", this);

        // 인자를 먼저 넣고 구독한다 — 순서가 뒤집히면 구독 시점의 첫 발화가 "{0}" 그대로 나간다
        // (localization.md 결정 (e), SignalDecoder.ShowLocal과 같은 관례).
        SetCount(m_jailZone != null ? m_jailZone.InmateCount : 0);

        // StringChanged 구독은 즉시 1회 발화하므로 이것이 초기 표시를 겸한다. 간판은 라운드 내내
        // 떠 있어 도중에 언어를 바꾸면 다시 그려야 하고, 그 갱신도 같은 구독으로 들어온다.
        m_format.StringChanged += HandleStringChanged;

        if (m_jailZone != null)
            m_jailZone.OnInmateCountChanged += Refresh;
    }

    private void OnDestroy()
    {
        if (m_jailZone != null)
            m_jailZone.OnInmateCountChanged -= Refresh;

        m_format.StringChanged -= HandleStringChanged;
    }

    private void Refresh(int count)
    {
        SetCount(count);
        m_format.RefreshString(); // 인원만 바뀌었으므로 지금 언어로 다시 조립한다
    }

    private void SetCount(int count) => m_format.Arguments = new object[] { count };

    private void HandleStringChanged(string text)
    {
        if (m_label != null)
            m_label.text = text;
    }
}
