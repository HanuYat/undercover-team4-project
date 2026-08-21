using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 납치 결말의 맨홀 — 뚜껑을 여닫는다. (#775)
///
/// 서버가 여닫고 전 피어가 같은 값을 보고 그린다 (#56). 뚜껑 참조는 <b>선택</b>이다 —
/// 비워 두면 연출만 빠지고 결말은 그대로 난다(<see cref="AbductionEvent"/>의 대기는 계속 돈다).
///
/// 맨홀 지점(<c>AbductionEvent.m_outskirtPoints</c>)이나 그 자식에 붙인다.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class AbductionManhole : NetworkBehaviour
{
    [Tooltip("열릴 뚜껑 — 비워 두면 연출 없이 결말만 난다")]
    [SerializeField] private Transform m_lid;

    [Tooltip("뚜껑이 옆으로 밀려나는 거리(m)")]
    [Min(0f)]
    [SerializeField] private float m_slideDistance = 0.9f;

    [Tooltip("뚜껑이 다 밀리는 데 걸리는 시간(초) — 이벤트의 뚜껑 대기 시간보다 짧게 둘 것")]
    [Min(0.05f)]
    [SerializeField] private float m_slideSeconds = 1.2f;

    [Tooltip("뚜껑 여는 소리 — 없으면 무음")]
    [SerializeField] private AudioSource m_openSound;

    private readonly NetworkVariable<bool> m_open = new NetworkVariable<bool>();

    // 오프라인(네트워크 미사용) Play용 사본 — NetworkVariable이 돌지 않는다 (NpcDutyAgent와 같은 관례)
    private bool m_openLocal;

    private Vector3 m_closedPosition; // 뚜껑의 닫힌 로컬 좌표 — 여는 이동의 기준
    private float m_progress;         // 0=닫힘, 1=열림
    private bool m_wasOpen;

    private bool IsOpen => IsSpawned ? m_open.Value : m_openLocal;

    private void Awake()
    {
        if (m_lid != null)
            m_closedPosition = m_lid.localPosition;
    }

    private void Update()
    {
        bool open = IsOpen;

        // 여는 순간에만 소리를 낸다 — 닫을 때는 무음(구조 성공은 조용히 되돌린다)
        if (open != m_wasOpen)
        {
            m_wasOpen = open;
            if (open && m_openSound != null)
                m_openSound.Play();
        }

        if (m_lid == null)
            return;

        float target = open ? 1f : 0f;
        if (Mathf.Approximately(m_progress, target))
            return;

        m_progress = Mathf.MoveTowards(
            m_progress, target, Time.deltaTime / Mathf.Max(m_slideSeconds, 0.05f));
        m_lid.localPosition = m_closedPosition + Vector3.right * (m_slideDistance * m_progress);
    }

    /// <summary>뚜껑을 연다 — 맨홀 도착 시. 서버(또는 오프라인) 전용.</summary>
    public void ServerOpen() => SetOpen(true);

    /// <summary>뚜껑을 닫는다 — 구조 성공·라운드 정리. 서버(또는 오프라인) 전용.</summary>
    public void ServerClose() => SetOpen(false);

    private void SetOpen(bool value)
    {
        if (IsSpawned && !IsServer)
            return;

        m_openLocal = value;
        if (IsSpawned)
            m_open.Value = value;
    }
}
