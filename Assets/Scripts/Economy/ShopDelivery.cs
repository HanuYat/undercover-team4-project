using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 상점 구매품의 본부 택배 배달 (#182) — Main Scene 본부에 배치한다. 서버가 게임 씬 진입 시
/// 팀 구매 목록(ShopPurchases)을 훑어 이번 라운드 몫을 다시 지급한다.
///  · <b>소지형</b> — 택배 지점 바닥에 부모 없이 서버 스폰. 아무나 WorldItemPickup으로 주워 간다.
///  · <b>설치형</b> — 본부에 배치된 씬 인스턴스의 SetInstalled(true) 호출. 인벤토리 경로를 타지 않는다 (#108).
///
/// <b>매 라운드 다시 배달한다.</b> 상점 복귀 시 ShopManager.DespawnDroppedItems(바닥에 남은 것)와
/// PlayerItemSupply.ServerClearHeldItems(들고 있던 것)가 전량 회수하므로(#370), 회수 → 재배달이 성립한다.
/// </summary>
public class ShopDelivery : MonoBehaviour
{
    [Header("소지형 배달")]
    [Tooltip("여러 개가 겹치지 않게 흩뿌리는 반경(m)")]
    [SerializeField] private float m_spreadRadius = 0.4f;

    [Header("설치형 접합점 (#108)")]
    // 품목별 필드를 두지 않는다 — 각 설치물이 자기 EInstallable(Id)을 들고 있어 대조만 하면 된다.
    // 설치형이 늘면 여기 끌어다 놓기만 하고 코드는 건드리지 않는다.
    [Tooltip("본부에 배치된 설치형 씬 인스턴스들 — 구매한 것만 켜진다")]
    [SerializeField] private InstallableItem[] m_installables;

    [Header("배달 지점")]
    // 택배 지점은 이 오브젝트가 아니라 별도 오브젝트다 — 옮기고 싶으면 그 오브젝트를 옮긴다.
    [Tooltip("소지형이 떨어질 지점 — 비워 두면 이 오브젝트 위치를 쓴다")]
    [SerializeField] private Transform m_deliveryZone;

    private void Start()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null || !manager.IsServer)
            return;

        DeliverAsync().Forget();
    }

    // 씬 로드 콜백 안에서 바로 스폰하지 않는다 — NGO 메시지 처리 도중에 스폰하면 뒤이어 접속하는
    // 클라이언트의 씬 동기화가 중복 스폰(같은 NetworkObjectId 재생성)으로 깨진다.
    private async UniTaskVoid DeliverAsync()
    {
        await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());

        ShopPurchases purchases = App.Game.ShopPurchases;
        if (purchases == null)
        {
            // 세션 없이 게임 씬을 직접 Play하는 경우(DevAutoHost) — 상주 홀더가 없다. 배달할 것도 없다.
            Debug.Log("[상점 배달] 구매 목록 홀더가 없어 배달 없음 (세션 밖 직접 Play)");
            return;
        }

        DeliverCarried(purchases);
        DeliverInstallables(purchases);
    }

    // 구매한 소지형을 택배 지점에 부모 없이 스폰한다. 별도 배선이 필요 없다 —
    // WorldItemPickup이 Awake에서 월드 모델·줍기 박스·바닥 하이라이트(#330)를 스스로 구성한다.
    private void DeliverCarried(ShopPurchases purchases)
    {
        // 지점이 별도 오브젝트라 배선이 빠질 수 있다 — 빠져도 배달은 되게 하고 경고만 남긴다.
        // 여기서 막으면 뒤따르는 설치형 배달까지 예외로 통째로 날아간다.
        if (m_deliveryZone == null)
        {
            Debug.LogWarning("[상점 배달] 배달 지점이 배정되지 않았다 — 이 오브젝트 위치에 떨어뜨린다", this);
            m_deliveryZone = transform;
        }

        int index = 0;
        foreach (ItemBase itemPrefab in purchases.Carried)
        {
            if (itemPrefab == null)
                continue;

            ItemBase item = Instantiate(itemPrefab, ResolveDropPosition(index++), Quaternion.identity);
            // 아이템은 씬을 넘어 살아남는 관례(PlayerLoadout 지급과 동일) — 회수는 상점 복귀 시 일괄 처리한다.
            item.NetworkObject.Spawn(destroyWithScene: false);
        }

        if (index > 0)
            Debug.Log($"[상점 배달] 소지형 {index}개를 본부 택배 지점에 배달");
    }

    // 설치형은 스폰이 아니라 본부 씬 인스턴스를 켜는 것 — 지목 대상이 프리팹 자산이 아니라 그 씬 오브젝트다.
    private void DeliverInstallables(ShopPurchases purchases)
    {
        // 배선 누락을 드러내려고 배열이 아니라 구매 목록 쪽을 순회한다 — 산 물건에 대응하는 씬 인스턴스가 없으면 로그한다.
        foreach (EInstallable purchased in purchases.Installables)
        {
            InstallableItem instance = FindInstance(purchased);
            if (instance == null)
            {
                Debug.LogWarning($"[상점 배달] {purchased}을(를) 샀지만 씬 인스턴스가 배정되지 않았다", this);
                continue;
            }

            instance.SetInstalled(true);
            Debug.Log($"[상점 배달] 설치형 설치: {purchased}");
        }
    }

    private InstallableItem FindInstance(EInstallable id)
    {
        foreach (InstallableItem installable in m_installables)
        {
            if (installable != null && installable.Id == id)
                return installable;
        }

        return null;
    }

    // 같은 자리에 겹쳐 쌓이면 조준으로 골라 줍기 어렵다 — 지점 둘레에 흩뿌린다.
    private Vector3 ResolveDropPosition(int index)
    {
        Vector3 basePosition = m_deliveryZone.position;
        if (index == 0)
            return basePosition;

        float angle = (index % 6) * 60f * Mathf.Deg2Rad;
        float radius = m_spreadRadius * (1f + index / 6);
        return basePosition + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
    }
}
