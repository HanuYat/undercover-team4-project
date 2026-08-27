using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 라운드 경계의 기본 장비 지급·회수. 서버 전용. (#370)
/// <see cref="PlayerLoadout"/>에서 분리한 이유는 구동 주체가 다르기 때문이다 — 줍기/버리기는 플레이어
/// 입력이 매 순간 부르지만, 이쪽은 라운드 경계에서 매니저(PlayerSpawnManager·ShopManager)가
/// 클라별로 한 번씩 부른다. 지급 구성(<c>m_startingGear</c>)도 여기 있는 편이 찾기 쉽다.
///
/// 소지품 집합(<see cref="HeldItems"/>)과 오너 통지는 <see cref="PlayerLoadout"/>이 소유하고 여기서는
/// 빌려 쓴다: 서버가 소지품을 바꾸는 네 경로(지급·회수·줍기·버리기)가 전부 통지 하나로 수렴하는 것이
/// 그쪽의 설계 축이라, 나눠 가지면 그 수렴이 깨진다. 의존은 Supply → Loadout 한 방향뿐이다.
/// </summary>
[RequireComponent(typeof(PlayerLoadout))]
public class PlayerItemSupply : NetworkBehaviour
{
    [Header("기본 지급 장비")]
    [Tooltip("게임 시작 시 순서대로 지급할 아이템 프리팹(NetworkObject). 첫 항목이 기본 장착된다.")]
    [SerializeField]
    private List<ItemBase> m_startingGear = new List<ItemBase>();

    [Header("상점 지급 장비 (#843)")]
    [Tooltip("상점 씬에서 지급할 아이템 프리팹 — 장비 카탈로그. 게임 씬 진입 시 회수된다.")]
    [SerializeField]
    private List<ItemBase> m_shopGear = new List<ItemBase>();

    private PlayerLoadout m_loadout;

    private void Awake()
    {
        m_loadout = GetComponent<PlayerLoadout>();
    }

    public override void OnNetworkSpawn()
    {
        // 게임 씬에서 플레이어가 처음 만들어지는 경우만 여기서 지급한다 — 직접 Play(DevAutoHost)처럼
        // NGO 연결 승인이 플레이어를 만드는 흐름. 정식 루프(상점→게임)의 매 라운드 지급은
        // PlayerSpawnManager가 호출한다(둘이 겹쳐도 GrantStartingGear의 보유 검사가 막는다). (#370)
        if (App.CurrentScene == EScene.Game)
        {
            ServerGrantStartingGear();
        }
    }

    /// <summary>
    /// 기본 장비를 지급한다 — 게임 씬 진입 시 서버(PlayerSpawnManager)가 클라별로 호출한다. (#370)
    /// 상점 복귀 때 <see cref="ServerClearHeldItems"/>로 전량 회수되므로 매 라운드 같은 구성으로 시작한다.
    /// 서버 판정은 한 프레임 뒤 GrantStartingGearAsync가 한다 — 클라 호출은 거기서 걸러진다.
    /// </summary>
    public void ServerGrantStartingGear() => GrantAsync(m_startingGear, "기본 장비").Forget();

    /// <summary>
    /// 상점 장비(장비 카탈로그)를 지급한다 — 상점 씬 진입 시 서버(ShopManager)가 회수 직후 호출한다. (#843)
    /// 게임 씬 진입 때 다시 회수되므로 라운드 시작 구성은 그대로다.
    /// </summary>
    public void ServerGrantShopGear() => GrantAsync(m_shopGear, "상점 장비").Forget();

    // 호출 지점(스폰 처리·씬 로드 완료 콜백) 밖으로 한 프레임 미뤄 지급한다 — NGO 메시지 처리 중
    // 스폰하면 후속 접속 클라의 씬 동기화가 중복 스폰(같은 NetworkObjectId 재생성)으로 깨진다.
    private async UniTaskVoid GrantAsync(List<ItemBase> gear, string label)
    {
        await UniTask.NextFrame();

        // 대기 중 디스폰됐거나 더 이상 서버가 아니면 중단.
        if (this == null || !IsSpawned || !IsServer)
        {
            return;
        }

        Grant(gear, label);
    }

    // 기본 장비 프리팹을 NetworkObject로 스폰해 오너 소유로 만들고 플레이어에 부착한 뒤,
    // 오너에게 보유 목록을 동기화한다.
    private void Grant(List<ItemBase> gear, string label)
    {
        HeldItems held = m_loadout.Held;

        // 이미 뭔가 들고 있으면 지급하지 않는다 — 게임 씬 재진입·중복 호출로 같은 장비가 겹쳐 스폰되면
        // 슬롯(5칸)이 헛되이 차 이후 줍기가 전부 거부된다. 정상 흐름에서는 상점 복귀 때 전량 회수돼 빈손이다. (#370)
        if (held.Count > 0)
        {
            Debug.LogWarning($"[PlayerItemSupply] 이미 아이템을 보유 중이라 {label} 지급을 건너뛴다.", this);
            return;
        }

        int granted = 0;
        foreach (ItemBase gearPrefab in gear)
        {
            if (gearPrefab == null)
            {
                continue;
            }

            // 소지 5칸 초과분은 스폰하지 않는다 (#144/#793) — 캡을 안 두면 초과 아이템이 부착되지만
            // 슬롯에 안 들어가 장착·드롭 불가 상태로 남고, 보유 카운트가 영구히 꽉 차
            // 이후 모든 줍기가 거부된다.
            if (granted >= PlayerLoadout.k_maxHeldItems)
            {
                Debug.LogWarning(
                    $"[PlayerItemSupply] {label} 지급이 소지 한도({PlayerLoadout.k_maxHeldItems})를 초과 — 초과분 무시. 지급 목록 설정 확인."
                );
                break;
            }

            ItemBase item = Instantiate(gearPrefab);
            NetworkObject itemNetworkObject = item.GetComponent<NetworkObject>();
            itemNetworkObject.SpawnWithOwnership(OwnerClientId);
            held.Attach(itemNetworkObject);
            granted++;
        }

        Debug.Log($"[PlayerItemSupply] {label} 지급 — client {OwnerClientId}, {granted}개 ({App.CurrentScene})");
        m_loadout.ServerNotifyHeldItemsChanged();
    }

    /// <summary>
    /// 보유 아이템을 전량 회수(디스폰)한다 — 상점 복귀 시 서버(ShopManager)가 클라별로 호출한다. (#370)
    /// 아이템은 destroyWithScene:false로 스폰돼 씬을 넘어도 살아남으므로, 회수하지 않으면 다음 라운드
    /// 지급분과 겹쳐 슬롯이 찬다. 라운드 사이 이월은 오브젝트 생존이 아니라 상점 구매 목록(#182)이 맡는다.
    /// </summary>
    public void ServerClearHeldItems()
    {
        if (!IsServer)
        {
            return;
        }

        // 디스폰 자체는 플레이어 정리(#395, PlayerLoadout.OnNetworkDespawn)와 같은 경로 —
        // 여기서는 그 뒤 오너 동기화까지 한다. 플레이어는 살아 남아 다음 라운드에 다시 지급받으므로
        // 슬롯을 비워 줘야 하기 때문.
        int cleared = m_loadout.Held.DespawnAll();
        Debug.Log($"[PlayerItemSupply] 보유 아이템 회수 — client {OwnerClientId}, {cleared}개");

        // 오너 슬롯 모델에 파괴된 참조가 남지 않도록 빈 목록으로 재구성시킨다 — 안 보내면 인벤토리 UI가
        // 죽은 아이템 칸을 그대로 들고 있어 다음 라운드 지급분이 들어갈 칸이 없다.
        m_loadout.ServerNotifyHeldItemsChanged();
    }
}
