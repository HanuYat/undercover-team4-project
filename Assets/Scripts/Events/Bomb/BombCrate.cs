using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 씬에 미리 놓아둔 폭탄 상자 — 추격 폭탄이 여기서 나온다. (GDD 6-4, #399)
///
/// <b>상자는 이벤트가 만들지 않는다.</b> 레벨에 미리 배치돼 평소에도 도시 소품으로 서 있고, 이벤트가
/// 추첨되면 그중 한 곳이 열리며 폭탄이 나온다 — 폭탄과 함께 상자가 허공에서 생기면 "저 상자는 폭탄이다"가
/// 등장 순간에야 보여서, 미리 알아보고 피하거나 경계할 여지가 없다. 후보 지점 역할도 이 컴포넌트가 겸한다
/// (<see cref="BombChaseEvent"/>가 <see cref="All"/>에서 하나를 고른다).
///
/// <b>여는 연출에는 동기화가 없다.</b> 각 피어가 "내 앞에 등장 중(<see cref="BombState.Emerging"/>)인
/// 폭탄이 있는가"만 보고 스스로 연다 — 폭탄의 위치도 상태도 이미 전 피어에서 같으므로 같은 순간에 같은
/// 그림이 나온다. 어느 상자가 뽑혔는지를 따로 실어 보낼 필요가 없다.
/// </summary>
public class BombCrate : MonoBehaviour
{
    /// <summary>씬에 살아 있는 상자들 — <see cref="BombChaseEvent"/>가 등장 지점으로 고른다.</summary>
    public static readonly List<BombCrate> All = new List<BombCrate>();

    [Tooltip("폭탄이 나올 자리 — 비우면 이 오브젝트의 위치를 쓴다. 상자 메시의 피벗이 구석에 있으면 지정할 것")]
    [SerializeField]
    private Transform m_spawnPoint;

    [Tooltip("이 거리(m) 안에 등장 중인 폭탄이 있으면 '내 상자에서 나온다'로 보고 연다")]
    [SerializeField]
    private float m_matchRadius = 1.5f;

    [Header("1단계 — 들썩")]
    [Tooltip("상자가 흔들리는 시간(초) — 아래 밀려남과 합쳐 BombDevice의 등장 시간보다 짧게 둘 것")]
    [SerializeField]
    private float m_shakeSeconds = 0.8f;

    [Tooltip("흔들림 높이(m)")]
    [SerializeField]
    private float m_shakeHeight = 0.06f;

    [Tooltip("흔들림 좌우 기울기(도)")]
    [SerializeField]
    private float m_shakeTilt = 4f;

    [Tooltip("초당 흔들림 횟수")]
    [SerializeField]
    private float m_shakeFrequency = 9f;

    [Header("2단계 — 밀려남")]
    [Tooltip("상자가 밀려나는 시간(초)")]
    [SerializeField]
    private float m_shoveSeconds = 0.45f;

    [Tooltip("뒤로 밀려나는 거리(m) — 폭탄을 완전히 벗어날 만큼. 짧으면 상자가 폭탄을 덮은 채로 남는다")]
    [SerializeField]
    private float m_shoveDistance = 1.4f;

    [Tooltip("밀려나며 살짝 떠오르는 높이(m) — 바닥을 긁는 게 아니라 밀쳐진 것으로 보이게")]
    [SerializeField]
    private float m_shoveHop = 0.22f;

    [Tooltip("밀려나며 기우는 각도(도) — 크게 주면 상자가 세로로 서 버린다(깊이가 높이보다 길다)")]
    [SerializeField]
    private float m_shoveTilt = 24f;

    private Vector3 m_closedPosition;
    private Quaternion m_closedRotation;
    private bool m_opening;
    private float m_elapsed;

    /// <summary>폭탄이 나올 위치 — 이벤트가 여기에 스폰한다.</summary>
    public Vector3 SpawnPosition => m_spawnPoint != null ? m_spawnPoint.position : transform.position;

    /// <summary>폭탄이 나올 방향 — 상자가 향한 쪽으로 세운다.</summary>
    public Quaternion SpawnRotation => m_spawnPoint != null ? m_spawnPoint.rotation : transform.rotation;

    private void Awake()
    {
        m_closedPosition = transform.localPosition;
        m_closedRotation = transform.localRotation;
    }

    private void OnEnable() => All.Add(this);

    private void OnDisable() => All.Remove(this);

    private void Update()
    {
        BombDevice bomb = BombDevice.Active;

        // 내 상자에서 나오는 폭탄인가 — 라운드당 폭탄은 1개라 위치만 맞으면 확정이다
        bool mine = bomb != null
            && (bomb.transform.position - SpawnPosition).sqrMagnitude <= m_matchRadius * m_matchRadius;

        if (!mine)
        {
            // 폭탄이 사라졌다(폭발 후 정리·라운드 종료) — 다음 라운드를 위해 닫아 둔다
            if (m_opening)
                Close();
            return;
        }

        if (!m_opening)
        {
            if (bomb.State != BombState.Emerging)
                return; // 아직 나오기 전이거나(스폰 직후) 이미 다 나온 뒤 — 열 이유가 없다

            m_opening = true;
            m_elapsed = 0f;
        }

        m_elapsed += Time.deltaTime;

        if (m_elapsed < m_shakeSeconds)
        {
            TickShake(m_elapsed);
            return;
        }

        TickShove(Mathf.Clamp01((m_elapsed - m_shakeSeconds) / m_shoveSeconds));
    }

    // 상자가 제자리에서 들썩인다 — 절댓값 사인이라 바닥을 치고 튀어오르는 리듬이 된다
    private void TickShake(float t)
    {
        float wave = Mathf.Abs(Mathf.Sin(t * m_shakeFrequency * Mathf.PI));
        transform.localPosition = m_closedPosition + Vector3.up * (wave * m_shakeHeight);
        transform.localRotation = m_closedRotation * Quaternion.Euler(
            0f, 0f, Mathf.Sin(t * m_shakeFrequency * Mathf.PI * 0.5f) * m_shakeTilt);
    }

    /// <summary>
    /// 상자가 뒤로 밀려나며 기운다 — 폭탄이 안에서 밀어젖힌 그림.
    /// </summary>
    /// <remarks>
    /// <b>넘어뜨리지 않는다.</b> 이 상자는 깊이가 높이보다 길어서, 모서리를 축으로 90° 눕히면 오히려
    /// <b>세로로 곧추선 더 큰 상자</b>가 된다 — "쓰러졌다"가 아니라 "일어섰다"로 읽힌다. 그래서 크게
    /// 돌리는 대신 뒤로 밀어내고 살짝만 기울인다.
    /// </remarks>
    private void TickShove(float t)
    {
        float eased = 1f - (1f - t) * (1f - t); // 밀쳐진 물건의 감속 — 처음 빠르고 끝에서 잦아든다
        transform.localPosition = m_closedPosition
            + Vector3.back * (m_shoveDistance * eased)
            + Vector3.up * (Mathf.Sin(t * Mathf.PI) * m_shoveHop);
        transform.localRotation = m_closedRotation * Quaternion.Euler(
            -m_shoveTilt * eased, m_shoveTilt * 0.5f * eased, 0f);
    }

    // 닫힌 자세로 되돌린다 — 폭탄이 치워진 뒤라 보고 있는 사람이 없다고 보고 즉시 되돌린다
    private void Close()
    {
        m_opening = false;
        m_elapsed = 0f;
        transform.localPosition = m_closedPosition;
        transform.localRotation = m_closedRotation;
    }

    // 씬 뷰에서 폭탄이 나올 자리를 눈으로 확인할 수 있게 기즈모를 그린다.
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(SpawnPosition, 0.4f);
    }
}
