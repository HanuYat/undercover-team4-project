using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 플레이어 치장 (#818) — 순수 코스메틱. <b>서버가 스폰 시점에 심고</b> 전 피어가 각자 붙인다.
///
/// 값의 주인이 서버인 이유는 <see cref="PlayerCosmetics"/>와 같다 (#790): 오너는 스폰 메시지를 받은
/// 뒤에야 쓸 수 있어 스폰 페이로드에 실을 방법이 없고, 그러면 남의 로봇이 맨머리로 한 번 보인 뒤
/// 왕복 지연만큼 늦게 모자가 생긴다.
///
/// <b>미배정 가드가 없다</b> — 인덱스 0이 "안 씀"이라 기본값 자체가 안전한 상태다. 색은 0이 곧 첫
/// 색이라 그 구분이 불가능해 별도 플래그를 둬야 했다.
///
/// 부착물은 <see cref="NetworkObject"/>가 아니다 — 각 피어가 복제된 인덱스를 보고 로컬로 만든다.
/// </summary>
public class PlayerAccessories : NetworkBehaviour
{
    [Tooltip("치장 카탈로그 — 로비 선택 칸과 반드시 같은 에셋을 물릴 것")]
    [SerializeField] private AccessoryCatalog m_catalog;

    [Tooltip("부착 기준 본 — 3인칭 몸통 리그의 머리. 1인칭 팔 리그를 물리지 말 것")]
    [SerializeField] private Transform m_headBone;

    [Tooltip("레이어를 물려받을 3인칭 몸 렌더러 — 뼈가 아니라 이쪽을 따라간다 (Apply 주석 참고)")]
    [SerializeField] private Renderer m_bodyRenderer;

    private readonly NetworkVariable<AccessorySet> m_accessories = new(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    /// <summary>이 플레이어가 고른 치장 — 상황판이 얼굴을 찾을 때 읽는다. (#818)</summary>
    public AccessorySet Accessories => m_accessories.Value;

    // 슬롯별로 지금 붙어 있는 인스턴스 — 값이 바뀌면 지우고 다시 만든다
    private readonly GameObject[] m_spawned = new GameObject[Enum.GetValues(
        typeof(EAccessorySlot)
    ).Length];

    public override void OnNetworkSpawn()
    {
        // 여기서 쓴 값은 스폰 페이로드에 실려 나간다 — 그래서 남의 화면도 첫 프레임부터 옳다 (#790)
        if (IsServer)
            m_accessories.Value = ResolveSpawnAccessories();

        if (IsOwner)
        {
            GameSettings.OnAccessoryChanged += HandleOwnerAccessoryChanged;

            // 안전망 — 명부 보고가 스폰을 앞지르지 못한 경합에서만 한 박자 늦게 고쳐진다.
            // 값이 같으면 NetworkVariable이 스스로 무시하므로 정상 경로에서는 대역폭을 먹지 않는다.
            if (!IsServer)
                ReportAccessoriesRpc(AccessorySet.FromSettings());
        }

        m_accessories.OnValueChanged += HandleAccessoriesChanged;
        Apply(); // late-join은 값을 복제만 받고 OnValueChanged를 못 받는다
    }

    public override void OnNetworkDespawn()
    {
        m_accessories.OnValueChanged -= HandleAccessoriesChanged;

        // 오너만 구독했지만 무조건 뗀다 — 아니면 죽은 로봇을 가리키는 static 구독이 쌓인다
        GameSettings.OnAccessoryChanged -= HandleOwnerAccessoryChanged;
    }

    /// <summary>
    /// 서버가 아는 이 플레이어의 치장 — 명부가 로비 입장 때 받아 둔 값이다.
    /// 명부가 없거나(세션 없이 씬 직접 Play) 보고가 아직 안 닿았으면 오너는 로컬 설정으로,
    /// 남은 기본값(전부 안 씀)으로 떨어진다 — 틀린 것을 입히는 것보다 맨머리가 낫다.
    /// </summary>
    private AccessorySet ResolveSpawnAccessories()
    {
        SessionRoster roster = App.Game.Roster;
        if (roster != null && roster.TryGetEntry(OwnerClientId, out LobbyPlayerEntry entry))
            return entry.Accessories;

        return IsOwner ? AccessorySet.FromSettings() : default;
    }

    private void HandleOwnerAccessoryChanged(EAccessorySlot _)
    {
        if (!IsOwner || !IsSpawned)
            return;

        AccessorySet set = AccessorySet.FromSettings();
        if (IsServer)
            m_accessories.Value = set; // 호스트 자신 — RPC를 돌 필요가 없다
        else
            ReportAccessoriesRpc(set);
    }

    // 값의 주인이 서버라 원격 오너는 보고만 한다. 코스메틱이라 서버가 검증하지 않는다.
    [Rpc(SendTo.Server)]
    private void ReportAccessoriesRpc(AccessorySet set) => m_accessories.Value = set;

    private void HandleAccessoriesChanged(AccessorySet previous, AccessorySet current) => Apply();

    // 몸 렌더러가 안 물려 있으면 이 오브젝트(플레이어 루트)의 레이어로 떨어진다 — 뼈보다 안전하다.
    private int LayerSource() =>
        m_bodyRenderer != null ? m_bodyRenderer.gameObject.layer : gameObject.layer;

    private void Apply()
    {
        if (m_catalog == null || m_headBone == null)
        {
            Debug.LogWarning(
                $"[{nameof(PlayerAccessories)}] 카탈로그 또는 머리 본이 연결되지 않았습니다 (#818)",
                this
            );
            return;
        }

        AccessorySet set = m_accessories.Value;

        foreach (EAccessorySlot slot in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int i = (int)slot;

            if (m_spawned[i] != null)
            {
                Destroy(m_spawned[i]);
                m_spawned[i] = null;
            }

            GameObject prefab = m_catalog.Get(slot, set[slot]);
            if (prefab == null)
                continue; // 0(안 씀)이거나 범위 밖 — 아무것도 붙이지 않는다

            m_spawned[i] = Instantiate(prefab, m_headBone, false);
            m_spawned[i].name = prefab.name;

            // 레이어는 <b>몸 렌더러</b>를 따라간다 — 붙인 자리인 머리 본이 아니다. 뼈는 래그돌
            // 충돌용으로 Ragdoll 레이어에 있고 플레이어 카메라가 그 레이어를 컬링하므로, 뼈를
            // 따라가면 아무에게도 안 보인다(#818에서 실제로 그랬다). 몸 렌더러는 남에겐 Default,
            // 오너에겐 OwnBody라(PlayerLook.ApplyOwnerView) 1인칭에서 내 모자가 안 뜨는 것도 맞는다.
            PlayerLook.SetLayerRecursively(m_spawned[i].transform, LayerSource());
        }
    }
}
