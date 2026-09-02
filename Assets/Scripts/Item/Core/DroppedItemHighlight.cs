using EPOOutline;
using UnityEngine;

/// <summary>
/// 바닥에 떨어진 아이템의 상시 윤곽선 (#330) — 배경에 묻혀 안 보이던 아이템을 눈에 띄게 한다.
/// <see cref="WorldItemPickup"/>이 런타임에 부착하고 들림/놓임 전환마다 <see cref="SetGrounded"/>로 켜고 끈다.
/// 순수 로컬 표현 — 아이템 오브젝트는 전 클라에 복제되므로 각 클라가 자기 화면 몫의 윤곽선을 켠다
/// (윤곽선 렌더는 플레이어 카메라의 EPO Outliner가 담당 — CCTV에는 안 그려진다).
///
/// 조준 피드백(#184, InteractionFeedback)과 <b>같은 Outlinable을 공유</b>한다: 겨냥 중에는 조준 색이
/// 이 기본색을 덮고, 조준이 풀리면 InteractionFeedback이 꺼버리는 대신 <see cref="Restore"/>를 불러
/// 기본색으로 되돌린다 — 두 시스템이 색 소유권을 주고받는 구조다.
/// </summary>
public class DroppedItemHighlight : MonoBehaviour
{
    // 상시 하이라이트 색 — 사이버펑크 홀로 톤(시안). 조준 피드백의 E 기본색(노랑)·아이템별 색과
    // 확실히 구분되어 "겨냥해서 행동 가능"과 "저기 아이템이 있다"가 색으로 갈린다. [임시 — 아트 확정 시 조정]
    private static readonly Color k_restingColor = new Color(0.25f, 0.85f, 1f, 0.9f);

    private Outlinable m_outlinable;
    private bool m_grounded;

    /// <summary>
    /// 초기화 — 루트에 Outlinable을 만들고 자식 렌더러를 윤곽선 대상으로 수집한다.
    /// WorldItemPickup이 월드 비주얼 생성 직후 1회 호출한다.
    /// </summary>
    public void Initialize()
    {
        m_outlinable = GetComponent<Outlinable>();
        if (m_outlinable == null)
        {
            // 런타임 AddComponent는 Reset()이 호출되지 않으므로 렌더러 수집을 직접 한다 (조준 피드백과 동일)
            m_outlinable = gameObject.AddComponent<Outlinable>();
            InteractionFeedback.AddOutlineTargets(m_outlinable, gameObject);
        }

        Apply();
    }

    /// <summary>들림/놓임 전환 — 바닥에 있을 때만 하이라이트를 켠다. WorldItemPickup이 호출.</summary>
    public void SetGrounded(bool grounded)
    {
        m_grounded = grounded;
        Apply();
    }

    /// <summary>
    /// 기본색 복원 — 조준 피드백이 이 대상에서 조준을 뗄 때 호출한다(끄는 대신).
    /// 들려 있는 상태면 끈 채로 둔다.
    /// </summary>
    public void Restore() => Apply();

    private void Apply()
    {
        if (m_outlinable == null)
            return;

        m_outlinable.OutlineParameters.Color = k_restingColor;

        // 감옥 방 안에 떨어진 아이템은 방 안에서만 보이게 한다 (#537) — 상시 하이라이트라
        // 조준과 무관하게 켜져 있어, 이게 없으면 도시에서 그 윤곽이 벽을 뚫고 보인다.
        InteractionFeedback.ApplyJailOutlineLayer(m_outlinable, transform.position);

        m_outlinable.enabled = m_grounded;
    }
}
