using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 상점의 치장 자판기 (#818 D) — E를 누르면 토큰 1개로 치장 하나를 무작위로 뽑는다.
/// 뽑은 것은 계정 보유함에 쌓이고 커스터마이징 창에서 잠금이 풀린다.
///
/// <b>뽑기는 로컬 권위다.</b> 토큰이 계정 소유(각자의 Cloud Save)라 호스트가 남의 잔량을 읽거나
/// 깎을 수 없다 — 서버 권위로 만들려면 UGS Cloud Code가 필요하고, 순수 코스메틱이라 조작해도
/// 조작한 사람 모자만 늘어난다. <see cref="CosmeticLocker"/>가 '순수 로컬 동작'인 것과 같은 자리다.
///
/// <b>연출이 뽑은 사람과 남에게 다르다.</b> 뽑은 사람은 아이콘이 흘러가다 가운데에 멈추는 릴
/// (<see cref="CosmeticGachaPanel"/>)을 보고, 같이 서 있는 사람은 기계 앞에 그 치장이 뜨는 것만
/// 본다 — 남이 돌릴 때마다 내 화면이 릴에 덮이면 안 된다. 그래서 결과를 서버에 한 번 올려
/// 나머지에게만 뿌린다. 값이 위조돼도 남의 화면에 잠깐 뜨는 모형이 바뀔 뿐이라 검증하지 않는다.
///
/// 콜라이더는 반드시 <c>Interactable</c> 레이어에 둘 것 — PlayerInteractor의 조준 마스크가 그 레이어만 본다.
/// </summary>
public class CosmeticGachaMachine : NetworkBehaviour, IInteractable
{
    private const string k_table = "ShopTable";

    [Header("뽑기")]
    [Tooltip("뽑을 치장 목록 — Player 프리팹·커스터마이징 창과 같은 에셋을 물릴 것")]
    [SerializeField] private AccessoryCatalog m_catalog;

    [Header("연출")]
    [Tooltip("투입구로 빨려 들어갈 토큰 그림. 비우면 투입 연출을 건너뛴다")]
    [SerializeField] private Sprite m_coinSprite;

    [Tooltip("투입구 위치 — 자판기 기준 로컬 오프셋(m). 인스펙터에서 눈으로 맞출 것")]
    [SerializeField] private Vector3 m_coinSlotOffset = new Vector3(0.42f, 0.4f, 0.2f);

    [Tooltip("토큰 한 변 크기(m)")]
    [SerializeField] private float m_coinSize = 0.14f;

    [Tooltip("토큰이 들어가는 데 걸리는 시간(초) — 이 뒤에 룰렛이 돈다")]
    [SerializeField] private float m_coinInsertSeconds = 0.5f;

    [Tooltip("캡슐·치장이 뜰 자리. 비우면 자판기 자신의 위치를 쓴다")]
    [SerializeField] private Transform m_dispenseAnchor;

    [Tooltip("먼저 굴러 나오는 캡슐. 비우면 캡슐 없이 치장부터 뜬다")]
    [SerializeField] private GameObject m_capsulePrefab;

    [Tooltip("치장 프리팹은 머리에 붙는 크기라 그대로 두면 너무 작다 — 연출용 배율")]
    [SerializeField] private float m_revealScale = 4f;

    [Tooltip("캡슐이 나와 있는 시간(초)")]
    [SerializeField] private float m_capsuleSeconds = 0.8f;

    [Tooltip("치장이 커지며 나타나는 시간(초)")]
    [SerializeField] private float m_popSeconds = 0.35f;

    [Tooltip("다 커진 뒤 돌며 머무는 시간(초)")]
    [SerializeField] private float m_holdSeconds = 2.5f;

    [Tooltip("도는 속도(도/초)")]
    [SerializeField] private float m_spinDegreesPerSecond = 90f;

    [Tooltip("뽑은 사람에게만 들리는 소리")]
    [SerializeField] private EAudioClip m_drawSound = EAudioClip.ShopPurchase;

    [Tooltip("결과 문구가 떠 있는 시간(초)")]
    [SerializeField] private float m_messageSeconds = 4f;

    // 기계 앞 모형 연출이 겹치지 않게 — 남의 결과를 잇달아 받아도 하나만 돈다
    private bool m_playing;

    // 지금 떠 있는 모형 — 연출이 끊기면(씬 전환·파괴) 같이 지운다
    private GameObject m_shown;

    public LocalizedString PromptLabel(GameObject interactor) => InteractPrompts.Gacha;

    // 내 릴이 도는 중에만 막는다. 토큰이 없다고 미리 막지 않는 것은 눌러서 사유를 볼 수 있게
    // 하려는 것이고, 이유 표시 방식은 ShopStand와 같다.
    // 기계 앞 모형(m_playing)은 남의 뽑기로도 돌므로 여기서 보지 않는다 — 남이 뽑는 동안 내가
    // 못 누르면 붐빌 때 아무도 못 뽑는다.
    public bool CanInteract(GameObject interactor) => !IsReelSpinning();

    public void Interact(GameObject interactor)
    {
        if (IsReelSpinning())
            return;

        if (m_catalog == null)
        {
            Debug.LogWarning($"[{nameof(CosmeticGachaMachine)}] 카탈로그가 연결되지 않았습니다 (#818 D)", this);
            return;
        }

        if (CosmeticInventory.Tokens <= 0)
        {
            ShowMessage("Shop.Gacha.NoToken");
            return;
        }

        if (!Roll(out EAccessorySlot slot, out int index))
        {
            Debug.LogWarning($"[{nameof(CosmeticGachaMachine)}] 뽑을 항목이 없습니다 — 카탈로그가 비었습니다 (#818 D)", this);
            return;
        }

        // <b>장부는 먼저, 연출은 나중이다.</b> 연출 끝에 담으면 그 사이에 씬을 나가거나 게임을
        // 끄면 토큰만 사라진다. 화면은 늦어도 되지만 잔량은 그렇지 않다.
        if (!CosmeticInventory.TrySpendToken())
            return; // 같은 프레임에 두 번 눌린 경합 — 위 검사와 여기 사이에서만 갈린다

        // 환급 판정은 <b>보유함이 아니라 IsOwned</b>로 한다 — 기본 지급 세트는 보유함에 비트가
        // 없으므로(카탈로그가 판정한다) Grant의 반환값만 보면 처음부터 갖고 있던 8개를 뽑을 때마다
        // "새로 얻었다"가 되어 토큰이 환급 없이 사라졌다.
        bool alreadyOwned = CosmeticInventory.IsOwned(m_catalog, slot, index);
        CosmeticInventory.Grant(slot, index); // 기본 세트여도 비트는 켜 둔다 — 판정 출처를 하나로 모은다
        bool gained = !alreadyOwned;

        if (alreadyOwned)
            CosmeticInventory.AddTokens(1); // 중복은 환급이다 (팀 결정 #818 D)

        App.Sound?.PlaySfx2D(m_drawSound);

        // 토큰이 들어가는 것을 보여 준 뒤에 돌린다 — 넣지도 않았는데 릴부터 돌면 무엇을 내고
        // 받는 것인지 읽히지 않는다. 장부는 이미 위에서 끝냈으므로 이 연출이 끊겨도 잔량은 옳다.
        DrawAsync(slot, index, gained).Forget();

        // 같이 서 있는 사람도 보게 한다 — 세션이 아니면(씬 단독 Play) 보낼 곳이 없다
        if (IsSpawned)
            ReportDrawRpc((byte)slot, index);
    }

    /// <summary>
    /// 뽑은 사람의 연출 — 토큰이 투입구로 들어가고, 그 뒤에 릴이 돈다.
    ///
    /// 릴이 결과 문구까지 띄운다 — 창이 화면을 덮으므로 토스트는 그 뒤에 가린다.
    /// 릴이 없는 씬(패널 미배치)에서는 기계 앞 모형과 토스트로 물러난다.
    /// </summary>
    private async UniTaskVoid DrawAsync(EAccessorySlot slot, int index, bool gained)
    {
        try
        {
            await PlayCoinInsertAsync();
        }
        catch (OperationCanceledException)
        {
            return; // 자판기가 사라졌다(씬 전환)
        }

        CosmeticGachaPanel reel = FindReel();
        if (reel != null)
        {
            reel.Play(slot, index, gained);
        }
        else
        {
            ShowResultMessage(slot, index, gained);
            PlayRevealAsync(slot, index).Forget();
        }
    }

    /// <summary>
    /// 토큰 한 닢이 투입구 위에 떠서 돌다가 빨려 들어간다 (#850) — 그림이 없으면 아무것도 하지 않는다.
    ///
    /// 스프라이트로 그리는 이유는 이 토큰이 <b>UI에만 있던 그림</b>이라서다 — 같은 그림을 쓰면
    /// 상점 HUD의 보유 개수와 여기서 사라지는 한 닢이 같은 물건으로 읽힌다.
    /// 늘 보는 사람 쪽을 향하게 세운다 — 자판기 앞면이 어느 축인지는 프롭마다 다르다.
    /// </summary>
    private async UniTask PlayCoinInsertAsync()
    {
        if (m_coinSprite == null || m_coinInsertSeconds <= 0f)
            return;

        Vector3 slot = transform.TransformPoint(m_coinSlotOffset);
        var coin = new GameObject("GachaCoin");
        var renderer = coin.AddComponent<SpriteRenderer>();
        renderer.sprite = m_coinSprite;

        try
        {
            float elapsed = 0f;
            while (elapsed < m_coinInsertSeconds)
            {
                await UniTask.NextFrame(destroyCancellationToken);
                elapsed += Time.deltaTime;

                float t = Mathf.Clamp01(elapsed / m_coinInsertSeconds);
                coin.transform.position = Vector3.Lerp(slot + Vector3.up * 0.35f, slot, t * t);

                // 마지막 구간에서만 사라진다 — 처음부터 줄이면 들어가는 것이 아니라 녹는 것으로 보인다
                float shrink = t < 0.75f ? 1f : 1f - ((t - 0.75f) / 0.25f);
                coin.transform.localScale = Vector3.one * (m_coinSize * shrink);

                Camera view = Camera.main;
                if (view != null)
                    coin.transform.rotation = Quaternion.LookRotation(
                        coin.transform.position - view.transform.position
                    );
            }
        }
        finally
        {
            if (coin != null)
                Destroy(coin);
        }
    }

    /// <summary>
    /// 카탈로그 전체에서 균등하게 하나 고른다 (#818 D) — <b>슬롯을 먼저 고르지 않는다</b>.
    /// 슬롯을 고른 뒤 그 안에서 고르면 항목이 5개인 마스크와 31개인 머리카락이 같은 확률을 받아,
    /// 머리카락 한 개가 나올 확률이 마스크 한 개의 6분의 1이 된다.
    ///
    /// "안 씀"(0)은 뽑히지 않는다 — 뽑을 물건이 아니다.
    /// </summary>
    private bool Roll(out EAccessorySlot slot, out int index)
    {
        var pool = new List<(EAccessorySlot Slot, int Index)>();
        foreach (EAccessorySlot candidate in Enum.GetValues(typeof(EAccessorySlot)))
        {
            int count = m_catalog.CountOf(candidate);
            for (int i = 1; i < count; i++)
                if (m_catalog.Get(candidate, i) != null)
                    pool.Add((candidate, i));
        }

        if (pool.Count == 0)
        {
            slot = default;
            index = 0;
            return false;
        }

        (slot, index) = pool[UnityEngine.Random.Range(0, pool.Count)];
        return true;
    }

    // 로컬 권위라 서버는 검사하지 않고 그대로 중계한다 — 연출 값이다 (클래스 주석 참고).
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void ReportDrawRpc(byte slot, int index, RpcParams rpcParams = default)
    {
        // 뽑은 사람은 이미 로컬에서 돌렸다 — 되보내면 두 번 돈다
        PlayDrawRpc(
            slot,
            index,
            RpcTarget.Not(rpcParams.Receive.SenderClientId, RpcTargetUse.Temp)
        );
    }

    [Rpc(SendTo.SpecifiedInParams)]
    private void PlayDrawRpc(byte slot, int index, RpcParams rpcParams)
    {
        // 내 릴이 돌고 있으면 <b>내가 뽑은 것</b>이다 — 발신자 제외가 어긋나도 두 연출이 겹치지 않게 막는다.
        // 보내는 쪽에서 이미 발신자를 빼지만, 그 판정이 틀리면 뽑은 사람 화면에 릴과 모형이 함께 뜬다.
        if (m_playing || m_catalog == null || IsReelSpinning())
            return;

        PlayRemoteDrawAsync((EAccessorySlot)slot, index).Forget();
    }

    // 남이 돌리는 것도 토큰 투입부터 보인다 — 기계 앞 모형만 뜨면 무엇 때문에 나온 것인지 모른다
    private async UniTaskVoid PlayRemoteDrawAsync(EAccessorySlot slot, int index)
    {
        try
        {
            await PlayCoinInsertAsync();
        }
        catch (OperationCanceledException)
        {
            return;
        }

        PlayRevealAsync(slot, index).Forget();
    }

    // 릴은 스스로 UI 매니저에 등록한다 — 자판기가 인스펙터로 물고 있지 않는 이유는
    // CosmeticLocker가 커스터마이징 창을 찾는 것과 같다(씬에 자판기가 늘면 배선이 여러 벌 된다).
    private static CosmeticGachaPanel FindReel() =>
        App.UI.Current != null && App.UI.Current.TryGetPanel(out CosmeticGachaPanel panel)
            ? panel
            : null;

    private static bool IsReelSpinning()
    {
        CosmeticGachaPanel reel = FindReel();
        return reel != null && reel.IsSpinning;
    }

    /// <summary>
    /// 캡슐이 나오고, 뽑힌 치장이 커지며 돌다 사라진다. 모형은 카탈로그 프리팹을 그대로 쓴다 —
    /// 머리에 붙는 것과 같은 물건이 나와야 무엇을 얻었는지 알아볼 수 있다.
    /// </summary>
    private async UniTaskVoid PlayRevealAsync(EAccessorySlot slot, int index)
    {
        m_playing = true;
        try
        {
            Transform anchor = m_dispenseAnchor != null ? m_dispenseAnchor : transform;

            if (m_capsulePrefab != null)
            {
                m_shown = Instantiate(m_capsulePrefab, anchor.position, anchor.rotation);
                await UniTask.Delay(
                    TimeSpan.FromSeconds(m_capsuleSeconds),
                    cancellationToken: destroyCancellationToken
                );
                Clear();
            }

            GameObject prefab = m_catalog.Get(slot, index);
            if (prefab == null)
                return;

            m_shown = Instantiate(prefab, anchor.position, anchor.rotation);
            await SpinAsync(m_shown.transform);
        }
        catch (OperationCanceledException)
        {
            // 자판기가 사라졌다(씬 전환) — 모형은 아래 finally가 치운다
        }
        finally
        {
            Clear();
            m_playing = false;
        }
    }

    // 커지며 나타나고, 다 커진 뒤 제자리에서 돈다. 시간 기반이라 프레임률과 무관하다.
    private async UniTask SpinAsync(Transform shown)
    {
        float elapsed = 0f;
        while (elapsed < m_popSeconds + m_holdSeconds)
        {
            await UniTask.NextFrame(destroyCancellationToken);
            if (shown == null)
                return;

            elapsed += Time.deltaTime;
            float grow = m_popSeconds <= 0f ? 1f : Mathf.Clamp01(elapsed / m_popSeconds);
            shown.localScale = Vector3.one * (m_revealScale * grow);
            shown.Rotate(Vector3.up, m_spinDegreesPerSecond * Time.deltaTime, Space.World);
        }
    }

    private void Clear()
    {
        if (m_shown != null)
            Destroy(m_shown);

        m_shown = null;
    }

    // 연출이 도는 중에 파괴되면 UniTask가 취소되지만 모형은 별개 오브젝트라 남는다
    public override void OnDestroy()
    {
        Clear();
        base.OnDestroy();
    }

    // 투입구 자리는 눈으로 맞춰야 한다 — 고를 때 그 자리에 토큰 크기의 원을 그려 준다
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.82f, 0.25f);
        Gizmos.DrawWireSphere(transform.TransformPoint(m_coinSlotOffset), m_coinSize * 0.5f);
    }

    private void ShowResultMessage(EAccessorySlot slot, int index, bool gained)
    {
        var message = new LocalizedString(
            k_table,
            gained ? "Shop.Gacha.Result" : "Shop.Gacha.Duplicate"
        )
        {
            Arguments = new object[] { CosmeticNames.Of(m_catalog.Get(slot, index)) },
        };

        App.UI.Toast?.Show(message, m_messageSeconds);
    }

    // HUD가 없는 환경(데디케이티드 서버 등)에선 App.UI.Toast가 null이라 무동작 — JailDoor와 같은 방침
    private void ShowMessage(string key) =>
        App.UI.Toast?.Show(new LocalizedString(k_table, key), m_messageSeconds);
}
