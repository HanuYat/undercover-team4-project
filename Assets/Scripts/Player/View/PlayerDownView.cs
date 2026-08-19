using Unity.Netcode;

/// <summary>
/// 다운 유예 화면 어두워짐 — 오너 전용. (#725)
/// 어두워짐 = 1 - RemainingUntilDie / DieAfterDownSeconds, 이 한 식이 전부다. 구조 채널링 중에는
/// RemainingUntilDie가 얼어붙으므로(<see cref="PlayerIncapacitation"/>) 화면도 저절로 멈춘다 —
/// 여기서 따로 보간·타이머를 두지 않는다(PlayerHitView 관례와 달리 순간 이벤트가 아니라 매 프레임
/// 값을 그대로 반영하는 상태 표시라서다).
/// </summary>
[RequireComponent(typeof(PlayerIncapacitation))]
public class PlayerDownView : NetworkBehaviour
{
    private PlayerIncapacitation m_incapacitation;

    // 스폰 전(오프라인)에는 IsOwner가 늘 false다 — 그때는 자기 화면이 곧 내 화면이므로 오너로 본다.
    // (PlayerHitView.IsLocalOwner와 동일 관례)
    private bool IsLocalOwner => !IsSpawned || IsOwner;

    private void Awake()
    {
        m_incapacitation = GetComponent<PlayerIncapacitation>();
    }

    public override void OnNetworkDespawn()
    {
        // 씬 전환·리스폰으로 뷰가 사라질 때 화면이 어두운 채로 남지 않게 (PlayerHitView 관례)
        if (IsLocalOwner)
            App.UI.DamageVignette?.SetDownDarkness(0f);

        base.OnNetworkDespawn();
    }

    private void Update()
    {
        if (!IsLocalOwner)
            return;

        if (m_incapacitation == null || !m_incapacitation.IsDowned)
        {
            App.UI.DamageVignette?.SetDownDarkness(0f);
            return;
        }

        float total = m_incapacitation.DieAfterDownSeconds;
        float darkness = total > 0f ? 1f - m_incapacitation.RemainingUntilDie / total : 1f;
        App.UI.DamageVignette?.SetDownDarkness(darkness);
    }
}
