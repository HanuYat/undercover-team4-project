using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 소지품을 털렸을 때 본인에게만 뜨는 알림 (#303).
///
/// <b>알려야 하는 이유</b> — 소매치기는 뒤에서 스치듯 지나가고 물건은 인벤토리에서 조용히 사라진다.
/// 알림이 없으면 한참 뒤 아이템을 쓰려다 없어진 걸 알게 되고, 그때는 범인이 이미 멀리 있다.
///
/// <see cref="PlayerPenaltyView"/>의 추격 경고와 같은 구조다 — 서버가 부르고 [Rpc(SendTo.Owner)]로
/// 당사자 오너 클라에만 간다. 표시는 공용 토스트에 맡긴다 (#493).
/// </summary>
public class PlayerTheftView : NetworkBehaviour
{
    [Tooltip("털렸을 때 띄울 문구 — HudTable/Hud.Pickpocket.Stolen")]
    [SerializeField]
    private LocalizedString m_stolenToast;

    [Tooltip("문구가 떠 있는 시간(초). 지나면 저절로 사라진다")]
    [Min(0.5f)]
    [SerializeField]
    private float m_toastSeconds = 3f;

    /// <summary>서버 전용 — 물건을 실제로 뺏겼을 때만 부른다(빈손이면 알릴 것이 없다).</summary>
    public void ShowStolen()
    {
        if (IsSpawned)
            ShowStolenRpc();
        else
            ShowLocal(); // 오프라인 Play 테스트 폴백
    }

    // 서버가 호출하지만 오너 클라에서만 실행된다 — 당한 본인만 알아야 하므로 SendTo.Owner.
    [Rpc(SendTo.Owner)]
    private void ShowStolenRpc() => ShowLocal();

    // HUD가 없는 환경(데디케이티드 서버 등)에선 App.UI.Toast가 null이라 무동작 — PlayerPenaltyView와 같은 방침
    private void ShowLocal() => App.UI.Toast?.Show(m_stolenToast, m_toastSeconds);
}
