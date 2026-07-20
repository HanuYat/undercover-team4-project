using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 유치장 자물쇠 — 잠김/열림 상태만 갖는다. (GDD 7-2, #231)
/// <see cref="JailZone"/>과 같은 오브젝트에 둔다.
///
/// <b>누가 왜 여는지는 모른다</b> — 범인 탈출 이벤트(JailbreakEvent)가 열고, 새 수감자가 들어오면
/// 유치장이 다시 잠근다. 문 열림 애니메이션·본부 경보 UI는 <see cref="OnLockChanged"/>를 구독해 붙인다.
///
/// 상태는 서버 권위로 정해 NetworkVariable로 전 피어에 동기화한다 (#56) —
/// 자물쇠가 열린 것은 본부 화면에서 보여야 하므로 클라이언트도 읽을 수 있어야 한다.
/// </summary>
public class JailLock : NetworkBehaviour
{
    // 서버 권위 잠금 상태 — 기본은 잠김
    private readonly NetworkVariable<bool> m_locked = new NetworkVariable<bool>(true);

    // 오프라인(네트워크 없이 Play) 폴백용 로컬 값 — JailZone의 수용 인원과 동일 구조
    private bool m_localLocked = true;

    /// <summary>자물쇠가 잠겨 있는가. 네트워크 세션 중에는 동기화된 값이라 클라이언트에서도 읽을 수 있다.</summary>
    public bool IsLocked => IsSpawned ? m_locked.Value : m_localLocked;

    /// <summary>잠금 상태 변경 — 서버·클라이언트 모든 피어에서 발생한다. 문 연출·본부 경보 UI가 구독.</summary>
    public event Action<bool> OnLockChanged;

    public override void OnNetworkSpawn()
    {
        m_locked.OnValueChanged += HandleLockedChanged;
    }

    public override void OnNetworkDespawn()
    {
        m_locked.OnValueChanged -= HandleLockedChanged;
    }

    private void HandleLockedChanged(bool previous, bool current)
    {
        OnLockChanged?.Invoke(current);
    }

    /// <summary>자물쇠 해제 — 침입자가 자물쇠에 도달했을 때 탈출 이벤트가 호출한다. 서버(또는 오프라인) 전용.</summary>
    public void ServerUnlock()
    {
        if (SetLocked(false))
            Debug.Log("[유치장] 자물쇠 해제됨");
    }

    /// <summary>재잠금 — 새 수감자가 들어오거나(JailZone.Admit) 라운드가 정리될 때. 서버(또는 오프라인) 전용.</summary>
    public void ServerRelock()
    {
        if (SetLocked(true))
            Debug.Log("[유치장] 자물쇠 재잠금");
    }

    // 서버 진실값과 동기화 변수에 함께 기록한다. 값이 실제로 바뀐 경우에만 true를 돌려준다
    // (재잠금은 수감할 때마다 불리므로 로그가 도배되지 않게 한다). JailZone.SetInmateCount와 같은 구조 —
    // 오프라인에서는 NetworkVariable에 쓰지 않고 이벤트를 직접 발행한다.
    private bool SetLocked(bool value)
    {
        if (IsSpawned && !IsServer)
            return false; // 상태는 서버 권위 — 클라이언트에서 불려도 무시한다

        if (IsLocked == value)
            return false;

        m_localLocked = value;

        if (IsSpawned && IsServer)
            m_locked.Value = value; // OnValueChanged를 거쳐 모든 피어에서 이벤트 발생
        else if (!IsSpawned)
            OnLockChanged?.Invoke(value);

        return true;
    }
}
