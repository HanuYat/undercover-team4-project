using Unity.Netcode;

/// <summary>
/// 서버 권위 채널링 상호작용의 공통 기반 — 오너 화면 채널링 게이지 피드백을 제공한다. (#184)
/// 채널링은 서버가 돌리되 게이지는 '그 행동을 시작한 오너의 화면'에만 떠야 하므로,
/// 호스트 오너·오프라인은 로컬로 즉시 구동하고, 원격 오너에게는 SendTo.Owner RPC로 전달한다.
/// (데디케이티드 서버 등 HUD가 없는 환경에선 App.UI.Gauge가 null이라 무동작 — 안전)
///
/// 스캐너 등 아이템(ItemBase)과 검거·연행 허브(PlayerEscorter)가 이 기반을 공유한다 —
/// 두 경로가 같은 게이지 피드백 코드를 복붙하던 것을 단일 출처로 모은 것이다.
/// </summary>
public abstract class ChanneledInteractionBehaviour : NetworkBehaviour
{
    /// <summary>채널링 게이지 표시 — 오너 화면에. 서버·오프라인은 로컬, 원격 오너에겐 RPC.</summary>
    protected void NotifyChannelGaugeStart(float seconds)
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            ChannelGaugeStartRpc(seconds);
            return;
        }
        App.UI.Gauge?.Show(seconds);
    }

    /// <summary>채널링 게이지 숨김 — 완료·취소·거리이탈 등 어떤 종료 경로에서도 반드시 호출.</summary>
    protected void NotifyChannelGaugeEnd()
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            ChannelGaugeEndRpc();
            return;
        }
        App.UI.Gauge?.Hide();
    }

    [Rpc(SendTo.Owner)]
    private void ChannelGaugeStartRpc(float seconds) => App.UI.Gauge?.Show(seconds);

    [Rpc(SendTo.Owner)]
    private void ChannelGaugeEndRpc() => App.UI.Gauge?.Hide();
}
