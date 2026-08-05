using UnityEngine;

/// <summary>
/// 등장 연출 — 폭탄이 상자 안에서 나오는 그림. (#399 표현 계층)
///
/// <b>순수 표현이다.</b> 등장 시간 동안 폭탄은 무장 전(<see cref="BombState.Emerging"/>)이라 움직이지도
/// 터지지도 않는다. 그 사실은 <see cref="BombDevice"/>가 서버 권위로 정해 전 피어에 전파하고, 이 뷰는
/// 상태가 Emerging으로 바뀐 순간부터 자기 시계로 애니메이션을 돌린다 — 진행도를 따로 동기화하지 않는다
/// (피어 간 몇 프레임 어긋나도 보이는 것만 다르고, 무장 시각은 서버가 쥔 하나뿐이다).
///
/// 3단계로 끊는다: <b>들썩</b>(상자가 흔들려 "저기서 뭔가 나온다"를 먼저 알린다) → <b>넘어짐</b>(상자가
/// 옆으로 쓰러지며 로봇이 드러난다) → <b>기동</b>(로봇이 한 번 들썩이고 추격을 시작한다).
/// 들썩임이 먼저 오는 것이 중요하다 — 예고 없이 폭탄이 튀어나오면 달아날 준비를 할 수 없다.
///
/// 상자는 연출이 끝나면 <b>폭탄에서 떼어내</b> 그 자리에 남긴다. 붙어 있으면 폭탄을 따라다니고,
/// 지우면 "여기서 나왔다"는 흔적이 사라진다. 떼어낸 상자의 수명은 이 뷰가 쥔다(폭탄이 사라질 때 함께).
/// </summary>
[RequireComponent(typeof(BombDevice))]
public class BombEmergeView : MonoBehaviour
{
    [Tooltip("폭탄 본체 모델 — 등장 중 살짝 들썩인다")]
    [SerializeField]
    private Transform m_model;

    [Tooltip("폭탄을 덮고 있는 상자 — 등장이 끝나면 떼어내 그 자리에 남긴다")]
    [SerializeField]
    private Transform m_crate;

    [Header("1단계 — 들썩")]
    [Tooltip("상자가 흔들리는 시간(초)")]
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

    [Header("2단계 — 상자가 밀려남")]
    [Tooltip("상자가 밀려나는 시간(초)")]
    [SerializeField]
    private float m_shoveSeconds = 0.45f;

    [Tooltip("상자가 뒤로 밀려나는 거리(m) — 로봇을 완전히 벗어날 만큼. 짧으면 상자가 폭탄을 덮은 채로 남는다")]
    [SerializeField]
    private float m_shoveDistance = 1.35f;

    [Tooltip("밀려나며 살짝 떠오르는 높이(m) — 바닥을 긁는 게 아니라 밀쳐진 것으로 보이게")]
    [SerializeField]
    private float m_shoveHop = 0.22f;

    [Tooltip("밀려나며 기우는 각도(도) — 크게 주면 상자가 옆으로 서 버린다(깊이가 높이보다 길다)")]
    [SerializeField]
    private float m_shoveTilt = 24f;

    [Header("3단계 — 기동")]
    [Tooltip("로봇이 일어서며 들썩이는 높이(m)")]
    [SerializeField]
    private float m_startupHop = 0.12f;

    private BombDevice m_device;
    private bool m_playing;
    private bool m_finished;
    private float m_elapsed;

    private Vector3 m_cratePosition;
    private Quaternion m_crateRotation;
    private Vector3 m_modelPosition;

    private void Awake()
    {
        m_device = GetComponent<BombDevice>();

        if (m_crate != null)
        {
            m_cratePosition = m_crate.localPosition;
            m_crateRotation = m_crate.localRotation;
        }
        if (m_model != null)
            m_modelPosition = m_model.localPosition;
    }

    private void OnDestroy()
    {
        // 떼어낸 상자는 더 이상 폭탄의 자식이 아니라 스스로 사라지지 않는다 — 여기서 같이 치운다.
        // (폭발 잔류 시간이 끝나 BombChaseEvent가 폭탄을 디스폰할 때 함께 정리된다)
        if (m_finished && m_crate != null)
            Destroy(m_crate.gameObject);
    }

    private void Update()
    {
        if (m_finished)
            return;

        if (!m_playing)
        {
            if (m_device.State != BombState.Emerging)
            {
                // 등장 연출을 건너뛰고 시작한 폭탄(테스트 자동 무장 등) — 상자만 치우고 끝낸다
                if (m_device.State != BombState.Idle)
                    Finish();
                return;
            }

            m_playing = true;
            m_elapsed = 0f;
        }

        m_elapsed += Time.deltaTime;

        if (m_elapsed < m_shakeSeconds)
        {
            TickShake(m_elapsed);
            return;
        }

        float afterShake = m_elapsed - m_shakeSeconds;
        if (afterShake < m_shoveSeconds)
        {
            TickShove(Mathf.Clamp01(afterShake / m_shoveSeconds));
            return;
        }

        TickShove(1f); // 밀려난 자세로 고정
        TickStartup(afterShake - m_shoveSeconds);

        // 무장(Armed)으로 넘어가면 연출 종료 — 상자를 떼어내 그 자리에 남긴다.
        // 상태를 기준으로 끝내므로 폭탄의 등장 시간과 이 연출의 길이가 조금 달라도 어긋나지 않는다.
        if (m_device.State != BombState.Emerging)
            Finish();
    }

    // 상자가 제자리에서 들썩인다 — 절댓값 사인이라 바닥을 치고 튀어오르는 리듬이 된다
    private void TickShake(float t)
    {
        if (m_crate == null)
            return;

        float wave = Mathf.Abs(Mathf.Sin(t * m_shakeFrequency * Mathf.PI));
        m_crate.localPosition = m_cratePosition + Vector3.up * (wave * m_shakeHeight);
        m_crate.localRotation = m_crateRotation * Quaternion.Euler(
            0f, 0f, Mathf.Sin(t * m_shakeFrequency * Mathf.PI * 0.5f) * m_shakeTilt);
    }

    /// <summary>
    /// 상자가 뒤로 밀려나며 기운다 — 로봇이 안에서 밀어젖힌 그림.
    /// </summary>
    /// <remarks>
    /// <b>넘어뜨리지 않는다.</b> 이 상자는 깊이(1.43m)가 높이(0.95m)보다 길어서, 모서리를 축으로 90°
    /// 눕히면 오히려 <b>세로로 곧추선 더 큰 상자</b>가 된다 — "쓰러졌다"가 아니라 "일어섰다"로 읽힌다.
    /// 그래서 크게 돌리는 대신 뒤로 밀어내고 살짝만 기울인다. 밀려나는 거리는 로봇을 완전히 벗어나야
    /// 하므로(상자 깊이 절반 + 로봇 깊이 절반) <see cref="m_shoveDistance"/>를 줄일 때 주의할 것.
    /// </remarks>
    private void TickShove(float t)
    {
        if (m_crate == null)
            return;

        float eased = 1f - (1f - t) * (1f - t); // 밀쳐진 물건의 감속 — 처음 빠르고 끝에서 잦아든다
        m_crate.localPosition = m_cratePosition
            + Vector3.back * (m_shoveDistance * eased)
            + Vector3.up * (Mathf.Sin(t * Mathf.PI) * m_shoveHop);
        m_crate.localRotation = m_crateRotation * Quaternion.Euler(
            -m_shoveTilt * eased, m_shoveTilt * 0.5f * eased, 0f);
    }

    // 로봇이 한 번 들썩이고 자세를 잡는다 — 감쇠하는 사인이라 마지막엔 제자리에 선다
    private void TickStartup(float t)
    {
        if (m_model == null)
            return;

        float decay = Mathf.Exp(-t * 5f);
        float hop = Mathf.Sin(t * 12f) * m_startupHop * decay;
        m_model.localPosition = m_modelPosition + Vector3.up * Mathf.Max(0f, hop);
    }

    private void Finish()
    {
        m_finished = true;

        if (m_model != null)
            m_model.localPosition = m_modelPosition;

        if (m_crate == null)
            return;

        // 폭탄을 따라다니지 않게 떼어낸다. 씬 루트로 올려두면 폭탄이 굴러가도 상자는 등장 지점에 남는다.
        m_crate.SetParent(null, true);
    }
}
