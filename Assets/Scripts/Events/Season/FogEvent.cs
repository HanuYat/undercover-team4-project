using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 안개 (돌발 이벤트 · 전역 · 날씨) — 시야가 제한되는 기상 이변. (GDD 6-4, #227)
/// 활성 플래그를 <b>스스로 소유</b>해 서버 권위로 켜고 끄며, NetworkVariable로 전 클라에 동기화한다.
/// 실제 표현(거리 안개·파티클)은 이 플래그를 구독하는 <see cref="FogView"/>가 각 피어에서 담당한다.
/// 서버 권위(#56): 프레임워크가 ServerBegin/Tick/Reset을 서버에서만 부르므로 여기서 다시 검사하지 않는다.
/// <b>라운드 지속형이다</b> (#700) — 준비 단계에 뽑혀 라운드 끝까지 유지되고, 걷는 것은
/// <see cref="ServerReset"/> 하나뿐이다. 근거는 <see cref="IRoundWeather"/>.
/// </summary>
[RequireComponent(typeof(SuddenEventManager))]
public class FogEvent : NetworkBehaviour, IRoundWeather
{
    // 안개 전역 상태 — 서버만 쓰고 모든 클라가 읽는다. (DeviceBlackoutEvent.m_blackoutSynced와 동일 이중 구조)
    private readonly NetworkVariable<bool> m_fogSynced = new NetworkVariable<bool>();
    private bool m_fog; // 서버·오프라인의 진실값 (비네트워크 Play 폴백)

    public string DisplayName => "안개";

    public WeatherKind Kind => WeatherKind.Fog;

    // 안개가 켜져 있는 동안이 곧 이벤트 진행 중 — 라운드 지속형이라 이 값은 라운드 내내 true다.
    // (매니저가 IsActive를 읽는 것은 서버·오프라인에서뿐이므로 서버 진실값을 그대로 준다)
    public bool IsActive => m_fog;

    /// <summary>
    /// 조용히 시작한다 (팀 확정 2026-08-13) — 날씨는 <b>보면 안다</b>. 하늘이 바뀌고 시야가 줄어드는 것
    /// 자체가 알림이라, 토스트를 얹으면 같은 사실을 두 번 말하는 셈이다. 돌발 이벤트 토스트는
    /// "지금 대응할 일이 생겼다"를 위해 아껴 둔다 — 날씨까지 끼면 그 신호가 묽어진다.
    /// </summary>
    public bool AnnounceOnBegin => false;

    /// <summary>안개가 활성인지 — 서버·오프라인은 실참조, 원격 피어는 동기화값으로 판정.</summary>
    public bool IsFog => IsSpawned && !IsServer ? m_fogSynced.Value : m_fog;

    /// <summary>안개 상태가 바뀔 때 발행 — 거리 안개·파티클 표현(<see cref="FogView"/>)이 구독한다.</summary>
    public event Action<bool> OnFogChanged;

    public override void OnNetworkSpawn()
    {
        // 원격 피어는 서버의 Set 경로를 타지 않으므로, 동기화값 변화를 이벤트로 중계한다.
        m_fogSynced.OnValueChanged += HandleFogSyncedChanged;

        // 늦게 접속한 클라: 이미 안개가 진행 중이면 현재 상태를 즉시 반영한다.
        if (!IsServer && m_fogSynced.Value)
            OnFogChanged?.Invoke(true);
    }

    public override void OnNetworkDespawn()
    {
        m_fogSynced.OnValueChanged -= HandleFogSyncedChanged;
    }

    // 서버(호스트 포함)는 SetFog에서 직접 발행하므로 여기선 원격 클라만 중계 (이중 발행 방지)
    private void HandleFogSyncedChanged(bool previous, bool current)
    {
        if (IsServer)
            return;
        OnFogChanged?.Invoke(current);
    }

    // 전역 이벤트라 특별한 선행 조건이 없다 — 프레임워크가 라운드 준비 단계에 1회 호출한다
    public bool CanTrigger() => true;

    public void ServerBegin() => SetFog(true);

    // 비어 있다 — 안개는 켜고 끄는 것이 전부인데 라운드 지속형이 되며 시간 경과 해제까지 사라졌다 (#700)
    public void ServerTick() { }

    public void ServerReset()
    {
        SetFog(false);
    }

    // 안개 상태 설정 — 서버(또는 오프라인)에서만 호출된다.
    // 동기화 변수와 로컬 진실값을 함께 갱신하고, 표현 계층에 이벤트로 알린다.
    private void SetFog(bool value)
    {
        if (m_fog == value)
            return; // 중복 트리거·중복 해제 무시

        m_fog = value;
        if (IsSpawned && IsServer)
            m_fogSynced.Value = value; // OnValueChanged로 원격 클라에 중계
        OnFogChanged?.Invoke(value); // 서버·오프라인 로컬 발행 (원격은 위 동기화 콜백이 담당)
    }
}
