using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// NPC 머리 위에 붙는 월드공간 스캔 정보 카드. (#233)
/// 위치 추종·빌보드·누운 자세 보정·표시 토글은 <see cref="NpcWorldCard"/>가 담당하고,
/// 이 클래스는 카드에 무엇을 쓸지만 맡는다.
///
/// 표시 내용(실제/??)은 각 클라이언트의 ScanResultPresenter가 로컬로 세팅한다 —
/// 네트워크 동기화 없음(개인별 관리, GDD 5-4). NPC는 각 클라마다 로컬 인스턴스가 있으므로
/// 클라 A는 실제값, 클라 B는 ??를 각자 자기 화면의 같은 NPC에 독립적으로 찍을 수 있다.
/// </summary>
public class ScanInfoView : NpcWorldCard
{
    [Header("텍스트")]
    [SerializeField]
    private TMP_Text m_nameText;

    [Header("타입 아이콘")]
    [Tooltip("스캔된 NPC의 타입(인간/안드로이드) 아이콘")]
    [SerializeField]
    private Image m_typeIcon;

    [SerializeField]
    private Sprite m_humanIcon;

    [SerializeField]
    private Sprite m_androidIcon;

    [Tooltip("미스캔 NPC에 표시할 타입 아이콘 — 비우면 미스캔 상태에서 아이콘이 꺼진다")]
    [SerializeField]
    private Sprite m_unknownTypeIcon;

    [Header("생사 상태 아이콘")]
    [Tooltip("스캔된 NPC가 살아있을 때 표시할 아이콘")]
    [SerializeField]
    private Image m_statusIcon;

    [SerializeField]
    private Sprite m_aliveIcon;

    [SerializeField]
    private Sprite m_deadIcon;

    // 미스캔 NPC의 미확인 필드 표기 (#233)
    private const string k_masked = "??";

    // 이름 라벨은 카드마다 같은 문구이고 카드는 NPC 수만큼 있다 — SerializeField로 두면
    // NPC 프리팹마다 같은 키를 다시 배선해야 하고 하나만 빠지면 그 NPC만 옛 표기로 남는다. (#497)
    // 언어 변경 갱신은 ScanResultPresenter가 로케일 변경에 걸고 다시 채우는 것으로 처리한다.
    private const string k_hudTable = "HudTable";
    private const string k_nameKey = "Hud.Scan.FieldName";

    /// <summary>
    /// 스캔 완료 NPC — 실제 프로필 값을 표시하고 카드를 켠다.
    /// 세력은 표시하지 않는다(팀 UI로 대체돼 스캔 카드에서는 뺐다). 타입·생사는 문자열이 아니라
    /// 아이콘으로 낸다 — 카드 정보량을 줄여 한눈에 읽히게 하기 위함이다.
    /// </summary>
    /// <param name="isDead">이 NPC가 죽었는가 — <see cref="NpcDeath.IsDead"/>(전 피어 동기화 상태).</param>
    public void ShowReal(string citizenName, OfficialRecords.CitizenType typeView, bool isDead)
    {
        m_nameText.text = LocalizedStrings.Get(k_hudTable, k_nameKey, citizenName);
        SetTypeIcon(typeView);
        SetStatusIcon(isDead);
        SetCardActive(true);
    }

    /// <summary>미스캔 NPC — 이름은 ??로, 아이콘은 미확인 표기로 채우고 카드를 켠다.</summary>
    public void ShowMasked()
    {
        m_nameText.text = LocalizedStrings.Get(k_hudTable, k_nameKey, k_masked);
        SetTypeIcon(null);
        SetStatusIcon(null);
        SetCardActive(true);
    }

    // type이 null이면 미스캔 — 미확인 아이콘(비었으면 꺼짐)으로 표시한다.
    private void SetTypeIcon(OfficialRecords.CitizenType? type)
    {
        if (m_typeIcon == null) return;

        Sprite sprite = type switch
        {
            OfficialRecords.CitizenType.Human => m_humanIcon,
            OfficialRecords.CitizenType.Android => m_androidIcon,
            _ => m_unknownTypeIcon,
        };

        m_typeIcon.sprite = sprite;
        m_typeIcon.enabled = sprite != null;
    }

    // isDead가 null이면 미스캔 — 생사도 스캔 전까지는 모른다는 뜻으로 아이콘을 끈다.
    private void SetStatusIcon(bool? isDead)
    {
        if (m_statusIcon == null) return;

        Sprite sprite = isDead switch
        {
            true => m_deadIcon,
            false => m_aliveIcon,
            null => null,
        };

        m_statusIcon.sprite = sprite;
        m_statusIcon.enabled = sprite != null;
    }
}
