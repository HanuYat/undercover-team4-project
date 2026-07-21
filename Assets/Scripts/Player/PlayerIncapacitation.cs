using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 행동불능(무력화) 공통 기반. (#105)
/// HP 0 다운(#105)과 오검거 광장 매달기(#101)는 트리거만 다르고 결과=무력화로 동일하므로,
/// 무력화 상태 자체를 이 한 곳에서 서버 권위로 관리한다.
/// 이동·아이템·상호작용 컴포넌트가 <see cref="IsIncapacitated"/>를 읽어 각자 행동을 막는다.
///
/// 단, 두 무력화는 <b>구조 가능 여부</b>가 다르다: HP 다운은 동료가 구조(PlayerReviver)로 살릴 수 있지만,
/// 오검거 매달기는 30초 뒤 자동 복귀만 하는 페널티라 구조 대상이 아니다. <see cref="IsRevivable"/>로 구분하고,
/// 구조 대상 히트박스도 구조 가능한 무력화에서만 켠다. (#101)
/// </summary>
public class PlayerIncapacitation : NetworkBehaviour
{
    // 다운 중에만 활성화되는 구조 대상 히트박스(Interactable 레이어). 평소 비활성 — 조준 타겟팅용. (#105)
    // 플레이어 몸(CharacterController)은 Default 레이어라 PlayerInteractor의 Interactable 마스크에 안 잡히므로,
    // 다운 시 이 트리거 콜라이더를 켜서 구조자가 조준할 수 있게 한다. (구조 가능한 무력화에서만 — #101)
    [SerializeField]
    private GameObject m_reviveHitbox;

    // 서버 권위 무력화 플래그 — 서버만 쓰고 모든 클라가 읽는다. (PlayerData.m_syncedHp와 동일 패턴)
    private readonly NetworkVariable<bool> m_isIncapacitatedSynced = new NetworkVariable<bool>();
    // 현재 무력화가 구조 가능한지(HP 다운=true, 오검거 매달기=false). 무력화 아닐 때 값은 의미 없음. (#101)
    private readonly NetworkVariable<bool> m_isRevivableSynced = new NetworkVariable<bool>();

    private bool m_isIncapacitated; // 서버·오프라인의 진실값 (비네트워크 Play 테스트 폴백)
    private bool m_isRevivable;     // 서버·오프라인의 진실값

    /// <summary>무력화(행동불능) 여부. 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정. (PlayerEscorter.IsEscorting 관례)</summary>
    public bool IsIncapacitated =>
        IsSpawned && !IsServer ? m_isIncapacitatedSynced.Value : m_isIncapacitated;

    /// <summary>현재 무력화가 구조(리바이브) 가능한지. 오검거 매달기(#101)는 false — 30초 자동 복귀만 한다.</summary>
    public bool IsRevivable =>
        IsSpawned && !IsServer ? m_isRevivableSynced.Value : m_isRevivable;

    /// <summary>무력화 상태가 바뀔 때 발행 — 애니메이션·UI 훅용. (다운 애니 연결은 후속 이슈)</summary>
    public event Action<bool> OnIncapacitatedChanged;

    /// <summary>
    /// 서버·오프라인에서 '아무' 플레이어의 무력화 상태가 바뀔 때 발행 — 전원 다운(전멸) 판정 등 전역 로직용. (#105)
    /// 서버 권위 경로(<see cref="SetIncapacitated"/>)에서만 발행되므로 클라이언트에서는 울리지 않는다.
    /// </summary>
    public static event Action OnAnyIncapacitatedChanged;

    public override void OnNetworkSpawn()
    {
        // 원격 피어는 서버 Set 경로를 타지 않으므로, 동기화값 변화를 이벤트로 중계한다.
        m_isIncapacitatedSynced.OnValueChanged += HandleSyncedChanged;
        m_isRevivableSynced.OnValueChanged += HandleRevivableSyncedChanged;

        // 늦게 접속한 클라: 이미 다운된 플레이어의 현재 상태를 즉시 반영한다.
        // (OnValueChanged는 '변화' 시에만 발생하므로 스폰 시 한 번 맞춰줘야 한다)
        RefreshReviveHitbox();
    }

    public override void OnNetworkDespawn()
    {
        m_isIncapacitatedSynced.OnValueChanged -= HandleSyncedChanged;
        m_isRevivableSynced.OnValueChanged -= HandleRevivableSyncedChanged;
    }

    // 서버(호스트 포함)는 SetIncapacitated에서 이벤트를 직접 발행하므로 여기선 원격 클라만 중계 (이중 발행 방지)
    private void HandleSyncedChanged(bool previous, bool current)
    {
        if (IsServer)
            return;
        RefreshReviveHitbox();
        OnIncapacitatedChanged?.Invoke(current);
    }

    // 구조 가능 여부만 바뀐 경우(무력화 상태 자체는 그대로) — 히트박스만 다시 맞춘다. 원격 클라만.
    private void HandleRevivableSyncedChanged(bool previous, bool current)
    {
        if (IsServer)
            return;
        RefreshReviveHitbox();
    }

    // 구조 대상 히트박스 = "구조 가능한 무력화" 중에만 켠다. 서버·원격·오프라인 모든 인스턴스에서 실행된다.
    private void RefreshReviveHitbox()
    {
        UpdateReviveHitbox(IsIncapacitated && IsRevivable);
    }

    private void UpdateReviveHitbox(bool value)
    {
        if (m_reviveHitbox != null)
            m_reviveHitbox.SetActive(value);
    }

    /// <summary>
    /// 무력화 진입 — 서버(또는 오프라인)에서만. HP0 다운(#105)·매달기(#101) 등이 호출.
    /// revivable=true면 동료 구조 대상(HP 다운), false면 구조 불가(오검거 매달기 — 자동 복귀만).
    /// </summary>
    public void Incapacitate(bool revivable = true)
    {
        if (IsSpawned && !IsServer)
            return; // 서버 권위 방어 (PlayerEscorter 관례)
        SetIncapacitated(true, revivable);
    }

    /// <summary>무력화 해제(구조·복구) — 서버(또는 오프라인)에서만.</summary>
    public void Recover()
    {
        if (IsSpawned && !IsServer)
            return;
        SetIncapacitated(false, false);
    }

    // 서버 권위 값 변경 + 로컬 이벤트 발행을 함께 처리 — 서버(또는 오프라인)에서만 호출된다.
    private void SetIncapacitated(bool value, bool revivable)
    {
        if (m_isIncapacitated == value && m_isRevivable == revivable)
            return; // 중복 트리거 무시
        m_isIncapacitated = value;
        m_isRevivable = revivable;
        if (IsSpawned && IsServer)
        {
            m_isIncapacitatedSynced.Value = value;
            m_isRevivableSynced.Value = revivable;
        }
        RefreshReviveHitbox();
        OnIncapacitatedChanged?.Invoke(value);
        OnAnyIncapacitatedChanged?.Invoke(); // 전역 훅 — RoundManager가 전원 다운(전멸) 여부를 재검사
    }
}
