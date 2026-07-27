using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 스턴 오버레이 (#292) — 기절을 FSM 상태 전이가 아니라 <see cref="CurrentState"/> 위에 얹는
/// 동기화 플래그로 다룬다. 상태 enum이 바뀌지 않으므로 호송·수감·페널티 링크와 각 상태의
/// 타이머가 스턴에 끊기지 않고, 풀리면 하던 일을 그대로 재개한다.
///
/// 서버 권위 + 오프라인 폴백 — 서버(또는 오프라인)만 값을 바꾸고 클라이언트는 동기화 값을 읽는다.
/// m_networkState·m_syncedHp와 같은 이중 구조다 (#56 패턴).
///
/// <b>기절 경로는 둘이다.</b> 테이저와 체력 0(#366)은 이 오버레이를 쓰고, 넉백 착지는
/// <see cref="NpcState.Stunned"/> 상태 전이를 그대로 쓴다 — 넉백은 비행 전 상태 전이로 이전
/// 상태의 Exit()이 에이전트를 정리하게 만드는 구조라 오버레이로 옮기면 그 정리가 빠진다.
/// 그래서 "기절인가?"를 묻는 판정은 <see cref="NpcStateRules.IsIncapacitated"/>로 모은다.
/// </summary>
public partial class NpcController
{
    // 서버 권위 스턴 플래그 — 서버만 쓰고 모든 클라이언트가 읽는다. m_stunned가 서버·오프라인의 진실값.
    private readonly NetworkVariable<bool> m_syncedStunned = new NetworkVariable<bool>();
    private bool m_stunned;

    /// <summary>스턴 오버레이가 걸려 있는가. 세션 중에는 동기화 값이라 클라이언트에서도 읽을 수 있다. (#292)</summary>
    public bool IsStunned => IsSpawned ? m_syncedStunned.Value : m_stunned;

    // 서버 진실값과 동기화 변수에 함께 기록한다 — 오프라인에서는 NetworkVariable에 쓰지 않는다 (#56 상태 패턴과 동일)
    private void SetStunned(bool value)
    {
        m_stunned = value;
        if (IsSpawned && IsServer)
            m_syncedStunned.Value = value;
    }
}
