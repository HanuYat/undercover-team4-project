using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// NpcController의 커스터디 파트 — 연행(#59)·유치장 수감(#228)·석방·수갑 소모/반환(#229)·인계 방치(#230).
/// "잡힌 뒤의 신병 처리" 전이와 참조를 든다. 이동만 했고 동작 변화는 없다 (도메인 partial 분리).
/// FSM 전이는 전부 서버 권위 — 클라이언트 호출은 무시한다.
/// </summary>
public partial class NpcController
{
    [Header("연행 (#59)")]
    [Tooltip("연행 중 플레이어와 유지하는 추종 거리(m)")]
    [SerializeField] private float m_escortFollowDistance = 1.2f;
    [Tooltip("이 거리(m)보다 뒤처지면 속도를 올려 따라잡는다")]
    [SerializeField] private float m_escortBoostDistance = 4f;
    [SerializeField] private float m_escortBoostMultiplier = 1.5f;
    [Tooltip("이 거리(m)를 넘으면 연행이 풀리고 그 자리에서 체포 상태로 멈춘다")]
    [SerializeField] private float m_escortBreakDistance = 8f;

    [Header("인계 방치 (#230)")]
    [Tooltip("체포된 채 이 시간(초) 동안 인계되지 않으면 수갑을 풀고 도주한다 — 방치 전략 차단")]
    [SerializeField] private float m_capturedEscapeSeconds = 30f;
    [Tooltip("도주 직전 이 시간(초) 동안 소란을 낸다 — 수갑 풀려는 소동으로 현장·본부에 예고")]
    [SerializeField] private float m_capturedEscapeWarningSeconds = 5f;

    public float EscortFollowDistance => m_escortFollowDistance;
    public float EscortBoostDistance => m_escortBoostDistance;
    public float EscortBoostMultiplier => m_escortBoostMultiplier;
    public float EscortBreakDistance => m_escortBreakDistance;
    public float CapturedEscapeSeconds => m_capturedEscapeSeconds;
    public float CapturedEscapeWarningSeconds => m_capturedEscapeWarningSeconds;

    /// <summary>연행 중 따라갈 대상(체포한 플레이어). 연행 중이 아니면 null. 서버에서만 유효.</summary>
    public Transform EscortTarget { get; private set; }

    /// <summary>수감 중 걸어갈 유치장 수용 지점. 수감 중이 아니면 null. 서버에서만 유효. (#228)</summary>
    public Transform JailCell { get; private set; }

    /// <summary>유치장 수용 지점 도달 — 유치장(JailZone)이 구독해 수용 인원을 올린다. 서버에서만 발생. (#228)</summary>
    public event Action<NpcController> OnJailed;

    /// <summary>
    /// 본부 인계 판정이 끝났는가 — <see cref="MarkDelivered"/>로 ArrestJudge가 세팅한다. (#230)
    /// 판정 완료분은 인계 방치 타이머에서 빠지고(본부에서 탈출하면 안 된다), 인계존 재진입 시 중복 판정도 막는다.
    /// 서버(또는 오프라인)에서만 유효 — 판정·인계존 게이트가 모두 서버 전용이라 동기화하지 않는다.
    /// 판정 후 본부에 남는 NPC의 처리는 유치장(#228)이 가져간다.
    /// </summary>
    public bool IsDelivered { get; private set; }

    /// <summary>인계 판정 완료로 표시 — ArrestJudge 전용. 서버(또는 오프라인)에서만 호출된다. (#230)</summary>
    public void MarkDelivered()
    {
        if (IsSpawned && !IsServer)
            return;

        IsDelivered = true;
    }

    /// <summary>
    /// 인계 판정 완료 표시를 되돌린다 — 범인 탈출 이벤트(#231) 전용. 서버(또는 오프라인)에서만 호출된다.
    ///
    /// <b>재검거의 핵심이다.</b> <see cref="HqDropoffZone"/>은 <see cref="IsDelivered"/>가 켜진 NPC를
    /// 인계존에서 통째로 무시하므로(중복 판정 방지, #230), 이 플래그를 되돌리지 않으면 탈출한 범인을
    /// 다시 잡아 와도 판정이 아예 나지 않는다.
    /// </summary>
    public void ClearDelivered()
    {
        if (IsSpawned && !IsServer)
            return;

        IsDelivered = false;
    }

    /// <summary>연행 시작 — 체포 성공 직후 호출. NPC가 target(플레이어)을 따라 이동한다. (#59)</summary>
    // TODO: 아이템(수갑) 네트워크화 시 클라 입력 → ServerRpc 경로로 호출되도록 연결 (#56에서는 서버 가드만)
    public void StartEscort(Transform target)
    {
        // FSM 전이는 서버 권위 — 클라이언트에서 직접 부르면 무시한다
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = target;
        m_stateMachine.ChangeState(NpcState.Escorted);
    }

    /// <summary>연행 중단 — 그 자리에서 체포(Captured) 상태로 멈춘다. (#59)</summary>
    public void StopEscort()
    {
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null;
        m_stateMachine.ChangeState(NpcState.Captured);
    }

    // ---- 유치장 (#228) ----

    /// <summary>
    /// 수감 — 인계존 판정에서 진범·경범죄로 확정된 NPC를 유치장으로 보낸다. (CustodyRouter 경유, GDD 7-2)
    /// cell(수용 지점)까지 스스로 걸어가 그 자리에 수용된다. cell이 null이면 그 자리에서 수용된 것으로 처리한다.
    /// </summary>
    public void SendToJail(Transform cell)
    {
        // FSM 전이는 서버 권위 — StartEscort와 동일하게 클라이언트 호출은 무시한다
        if (IsSpawned && !IsServer)
            return;

        EscortTarget = null; // 판정 시점에 연행은 이미 풀렸지만, 참조가 남아 있으면 여기서 끊는다
        JailCell = cell;
        m_stateMachine.ChangeState(NpcState.Jailed);
    }

    /// <summary>수용 지점 도달 통보 — NpcJailedState 전용. 유치장이 이 이벤트로 수용 인원을 센다.</summary>
    public void NotifyJailed() => OnJailed?.Invoke(this);

    /// <summary>
    /// 수갑 해제 — 오검거로 판정된 무고한 시민을 풀어준다. 배회(Idle)로 복귀한다. (GDD 7-2/7-3, #228)
    /// 체포(Captured) 상태에서만 유효 — 판정 직후 ArrestJudge가 연행을 풀어 Captured로 만들어 둔 상태를 이어받는다.
    /// 오검거 카운트·페널티는 여기서 다루지 않는다 (ArrestJudge.OnArrestJudged를 구독하는 #101 담당).
    /// </summary>
    public void ReleaseFromCustody()
    {
        if (IsSpawned && !IsServer)
            return;
        if (m_stateMachine.CurrentState != NpcState.Captured)
            return;

        JailCell = null;
        m_stateMachine.ChangeState(NpcState.Idle);
    }

    // ---- 수갑 소모·반환 (#229) ----

    /// <summary>
    /// 이 NPC에 수갑이 채워져 있는가 — 체포 성공 시 잡은 플레이어에게서 옮겨온 수갑(PlayerLoadout.ConsumeHandcuffsTo).
    /// 재연행 시 수갑을 또 소모하지 않도록 PlayerEscorter가 이걸로 첫 연행 여부를 가른다. 부착 자식 기준.
    /// </summary>
    public bool HasHandcuffs => FindHeldHandcuffs() != null;

    // 채워진 수갑을 찾는다 — 없으면 null. 소모 시 NPC 루트 직속 자식으로 붙으므로(ConsumeHandcuffsTo)
    // 깊은 캐릭터 리그를 통째로 훑는 GetComponentInChildren 대신 루트 직속 자식만 본다(PlayerLoadout과 같은 패턴).
    private Handcuffs FindHeldHandcuffs()
    {
        for (int i = 0; i < transform.childCount; i++)
        {
            if (transform.GetChild(i).TryGetComponent(out Handcuffs cuffs))
            {
                return cuffs;
            }
        }

        return null;
    }

    /// <summary>
    /// 채워진 수갑을 발밑 바닥에 떨어뜨려 반환한다 — 인계존 판정 후 ArrestJudge.Judge가 호출. (#229)
    /// 판정 위치(현재 NPC 위치)에 놓이며, 부모에서 분리되는 순간 WorldItemPickup이 다시 월드 표시·줍기를 켠다.
    /// 서버(또는 오프라인)에서만. 수갑이 없으면(오검거 아닌 직접 스폰 테스트 등) 무동작.
    /// </summary>
    public void DropHandcuffs()
    {
        if (IsSpawned && !IsServer)
        {
            return;
        }

        Handcuffs cuffs = FindHeldHandcuffs();
        if (cuffs == null)
        {
            return;
        }

        NetworkObject cuffsNetworkObject = cuffs.NetworkObject;
        if (cuffsNetworkObject == null)
        {
            return;
        }

        // 부모(NPC)에서 분리 — 소유권은 커스터디 진입 때 이미 서버로 돌아와 있어 월드 상태 그대로다.
        cuffsNetworkObject.TrySetParent((Transform)null, true);
        cuffsNetworkObject.transform.position = transform.position;
    }
}
