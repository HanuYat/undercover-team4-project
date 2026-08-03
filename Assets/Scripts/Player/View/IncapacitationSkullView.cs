using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 다운·기능 정지 상태를 <b>채워지는 해골</b>로 보여주는 표시. (#493)
///
/// 다운되면 해골이 60초에 걸쳐 0 → 100%로 차오르고(노랑 → 빨강), 다 차면 기능 정지(Die)라
/// 진한 회색으로 굳는다. "채운 것이 곧 끝"이라 숫자를 읽지 않아도 남은 시간이 보인다.
/// 진행도는 <see cref="PlayerIncapacitation.DownProgress01"/>이 준다 — 전체 시간 상수를 이쪽에서
/// 들고 있지 않으므로 인스펙터에서 다운 지속시간을 바꿔도 게이지가 어긋나지 않는다.
///
/// <b>같은 컴포넌트를 두 자리에 쓴다</b>:
///   · 플레이어의 월드 자식 — 전 피어에서 돈다. 구조하러 오는 쪽이 멀리서 상태를 본다.
///   · HUD(<see cref="DownedSkullHud"/>) — 다운된 본인 화면. 자기 머리 위 아이콘은 누워 있으면
///     안 보이므로, 본인에게도 같은 진행도를 보여준다.
/// 후자는 대상이 자기 부모가 아니라 로컬 플레이어라 <see cref="Bind"/>로 밖에서 넣어 준다.
///
/// 순수 로컬 연출이라 아무것도 동기화하지 않는다 — 각 피어가 동기화된 상태(다운 원인·Die 기한)를
/// 보고 스스로 그린다. <see cref="RopeDragView"/>와 같은 방침.
/// </summary>
public class IncapacitationSkullView : MonoBehaviour
{
    [Header("표시")]
    [Tooltip("해골 아이콘 — Image Type을 Filled로, Fill Method는 Vertical(아래→위)로 설정한다")]
    [SerializeField]
    private Image m_fill;

    [Tooltip("아이콘 루트 — 상태가 아닐 때 통째로 끈다. 비우면 m_fill의 오브젝트를 쓴다")]
    [SerializeField]
    private GameObject m_root;

    [Header("대상")]
    [Tooltip("비우면 부모에서 찾는다 (플레이어의 자식으로 둔 경우). HUD 쪽은 Bind로 넣는다")]
    [SerializeField]
    private PlayerIncapacitation m_target;

    [Header("색")]
    [Tooltip("방금 다운됐을 때")]
    [SerializeField]
    private Color m_downedStartColor = new Color(1f, 0.85f, 0.2f);

    [Tooltip("기능 정지가 임박했을 때")]
    [SerializeField]
    private Color m_downedEndColor = new Color(1f, 0.25f, 0.15f);

    [Tooltip("기능 정지(Die) — 다 찬 채로 굳는다")]
    [SerializeField]
    private Color m_deadColor = new Color(0.35f, 0.35f, 0.38f);

    [Header("월드 배치")]
    [Tooltip("카메라를 향하게 회전시킨다 — 플레이어의 월드 자식으로 둘 때 켠다. HUD에서는 끈다")]
    [SerializeField]
    private bool m_faceCamera;

    private void Awake()
    {
        if (m_root == null && m_fill != null)
            m_root = m_fill.gameObject;

        // 부모에서 찾는다 — 플레이어의 자식으로 둔 경우. HUD 쪽은 Bind가 채운다.
        if (m_target == null)
            m_target = GetComponentInParent<PlayerIncapacitation>();

        SetVisible(false);
    }

    /// <summary>표시 대상을 밖에서 지정한다 — HUD처럼 부모가 플레이어가 아닌 경우.</summary>
    public void Bind(PlayerIncapacitation target)
    {
        m_target = target;
        if (target == null)
            SetVisible(false);
    }

    private void LateUpdate()
    {
        if (m_fill == null || m_target == null)
        {
            SetVisible(false);
            return;
        }

        bool downed = m_target.IsDowned;
        bool dead = m_target.IsDead;

        if (!downed && !dead)
        {
            SetVisible(false);
            return;
        }

        SetVisible(true);

        // Die는 다 찬 채로 굳는다 — 다운 게이지가 끝까지 간 결과라 100%가 그대로 이어진다
        float progress = dead ? 1f : m_target.DownProgress01;
        m_fill.fillAmount = progress;
        m_fill.color = dead
            ? m_deadColor
            : Color.Lerp(m_downedStartColor, m_downedEndColor, progress);

        if (m_faceCamera)
            FaceCamera();
    }

    // 카메라를 향해 돌린다. LateUpdate에서 하는 이유: 카메라가 Update에서 움직이므로
    // 여기서 맞춰야 이번 프레임의 최종 시점을 본다(한 프레임 늦게 따라붙지 않는다).
    private void FaceCamera()
    {
        Camera camera = Camera.main;
        if (camera == null)
            return;

        // 카메라의 정면 방향을 그대로 쓴다 — 대상을 바라보게 하면 화면 가장자리에서 기울어 보인다
        transform.rotation = camera.transform.rotation;
    }

    private void SetVisible(bool visible)
    {
        if (m_root != null && m_root.activeSelf != visible)
            m_root.SetActive(visible);
    }
}
