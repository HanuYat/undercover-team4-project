using Unity.Netcode;

/// <summary>
/// 처치 집계 — 이 플레이어가 막타를 쳐서 죽인 횟수. 서버 권위, 오너에게만 통보한다. (#869)
///
/// 가해자 해석은 <c>GetComponent</c> 하나로 끝난다 — 가해자가 NPC·차량·환경이면 이 컴포넌트가
/// 없어 저절로 무동작이다 (<see cref="BombDevice"/>가 <see cref="IDamageable"/>을 찾는 것과 같은 방식).
/// 집계 지점은 둘: NPC 사망(<see cref="NpcDeath.ServerEnterDead"/>)과 동료 확인사살
/// (<see cref="PlayerHealth.TakeDamage"/>의 <c>ServerFinishOff</c> 분기). 다운 방치로 저절로
/// 넘어가는 사망은 막타를 친 사람이 없어 집계하지 않는다.
///
/// 새 매니저를 두지 않는 이유: 필요한 상태가 전부 <b>플레이어 하나당 하나</b>라 플레이어
/// 오브젝트가 제자리다 — <see cref="WrongfulArrestPenalty"/>의 중앙 사전과는 성격이 다르다.
/// 그쪽은 판정자가 서버 하나뿐이라 중앙에 모으지만, 처치는 가해자 각자가 자기 몫만 알면 된다.
///
/// <b>누적 수는 화면에 띄우지 않는다</b>(2026-08-26 팀 결정) — <see cref="KillCount"/>는 정산 등
/// 라운드가 끝난 뒤의 UI가 나중에 읽어갈 값이고, 라운드 중 오너 알림은 이번 처치 대상 이름만 보여준다.
/// </summary>
public class PlayerKillCredit : NetworkBehaviour
{
    private int m_killCount; // 서버·오프라인의 진실값 — 지금은 라운드 종료 후 정산 몫으로만 쓴다

    /// <summary>이번 라운드 처치 수 — 서버 전용 참조. 정산 등 라운드 종료 후 UI가 읽어갈 값이다 (#869).</summary>
    public int KillCount => m_killCount;

    /// <summary>
    /// 처치 집계 — 사망 판정 지점(서버)에서 가해자 쪽에 부른다. 서버(또는 오프라인) 전용.
    /// </summary>
    /// <param name="victimName">화면에 띄울 처치 대상 이름 — NPC는 스캔 표시 이름, 동료는 닉네임.</param>
    /// <param name="friendlyFire">동료를 죽였는가 — 오너 알림의 색·문구가 갈린다.</param>
    public void ServerCreditKill(string victimName, bool friendlyFire)
    {
        if (IsSpawned && !IsServer)
            return;

        m_killCount++;
        NotifyOwner(victimName, friendlyFire);
    }

    /// <summary>라운드 사이 초기화. 서버(또는 오프라인) 전용 — 상점 진입 지점(ShopManager)에서 부른다.</summary>
    public void ServerResetRound()
    {
        if (IsSpawned && !IsServer)
            return;

        m_killCount = 0;
    }

    // 오너 화면에만 알린다 — Baton.NotifyHit과 같은 구조(오프라인은 RPC 경로가 없어 로컬 발행).
    private void NotifyOwner(string victimName, bool friendlyFire)
    {
        if (!IsSpawned)
        {
            ApplyKillMarker(victimName, friendlyFire);
            return;
        }

        NotifyKillRpc(victimName, friendlyFire);
    }

    [Rpc(SendTo.Owner)]
    private void NotifyKillRpc(string victimName, bool friendlyFire) =>
        ApplyKillMarker(victimName, friendlyFire);

    // 로컬 HUD·사운드라 오너 스폰 전이거나 HUD 없는 구성에서는 null이다 (Baton.ApplyHitMarker 관례).
    private static void ApplyKillMarker(string victimName, bool friendlyFire)
    {
        App.UI.Crosshair?.ShowKill(victimName, friendlyFire);
        App.Sound?.PlaySfx2D(friendlyFire ? EAudioClip.KillFriendly : EAudioClip.KillConfirm);
    }
}
