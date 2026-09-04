using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 일회성 연출의 단일 창구 (#532) — "무슨 일이 일어났는가"(<see cref="EFx"/>) 하나만 받아
/// 먼지(<see cref="EffectManager"/>)와 소리(<see cref="SoundManager"/>)를 함께 재생하고,
/// 서버 판정이면 전 피어 전파까지 맡는다. <see cref="App"/>.Game.Fx로 접근한다.
///
/// <b>왜 매니저를 하나 더 두는가</b> — 전에는 아이템마다 같은 세 덩어리를 복사했다:
/// 전파 RPC 하나, 오프라인 폴백 분기 하나, 파티클·소리를 각각 부르는 함수 하나.
/// 아이템이 정말 정해야 하는 건 "무슨 일이 일어났는가"뿐인데 전파 방식이 매번 따라붙었다.
/// 이 매니저가 그 셋을 가져가고 아이템에는 호출 한 줄만 남는다.
///
/// <b>어떤 순간에 어떤 먼지·소리가 나는지는 인스펙터의 조합표에 있다</b> — 표를 고치면 아이템 코드를
/// 건드리지 않고 연출이 바뀐다. 같은 먼지를 여러 소리에 붙일 수 있으므로(진압봉 먼지 1종 · 타격음 3종)
/// 이펙트 프리팹을 종류별로 복제하지 않는다.
///
/// 재생 자체는 두 로컬 매니저가 한다 — 이쪽은 <b>조합과 전파만</b> 안다. 그래서 EffectManager는
/// 여전히 네트워크를 모르고, SoundManager는 여전히 파티클을 모른다.
///
/// 인게임 씬 오브젝트라 로비·상점에는 없다 — 사용처는 <c>App.Game.Fx?.PlayEverywhere(...)</c>로 가드할 것.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class FxManager : NetworkedManagerBase
{
    [Serializable]
    public class Entry
    {
        [Tooltip("이 조합을 가리키는 키 — 코드에서 App.Game.Fx.PlayEverywhere(Id, ...)로 부른다")]
        public EFx Id;

        [Tooltip("함께 낼 파티클. None이면 소리만 난다")]
        public EEffect Effect;

        [Tooltip("함께 낼 소리. None이면 파티클만 난다")]
        public EAudioClip Sound;
    }

    [Tooltip("순간 → 파티클·소리 조합표. 같은 Id가 둘 이상이면 먼저 오는 것만 쓰이고 경고한다")]
    [SerializeField] private Entry[] m_entries;

    private readonly Dictionary<EFx, Entry> m_index = new();

    // 배선 사고를 알리되 매 프레임 도배하지 않는다 — 타격은 초당 여러 번 들어올 수 있다.
    private readonly HashSet<EFx> m_warned = new();

    protected override void Awake()
    {
        base.Awake(); // App.Game.Fx 등록 (R5)

        BuildIndex();
    }

    /// <summary>
    /// 연출을 전 피어에서 1회 재생한다 — <b>서버 판정 지점에서만 부른다</b>.
    /// 세션 밖(부트스트랩 없는 씬 직접 Play)에서는 전파할 상대가 없어 부른 자리에서 그대로 낸다.
    /// </summary>
    /// <param name="id">조합표의 키. <see cref="EFx.None"/>이면 무동작이다(의도적 '연출 없음')</param>
    /// <param name="position">재생 위치 — 월드 좌표</param>
    /// <param name="normal">
    /// 파티클이 향할 방향(맞은 면의 법선 등). 생략하면 회전 없이 재생한다. 소리에는 영향이 없다.
    /// </param>
    /// <param name="volumeScale">
    /// 소리 볼륨에 곱할 배율(0~1). 같은 순간이 세기에 따라 크고 작게 들려야 할 때만 쓴다
    /// (홈런 진압봉 차지, #998). 파티클에는 영향이 없다.
    /// </param>
    public void PlayEverywhere(
        EFx id,
        Vector3 position,
        Vector3 normal = default,
        float volumeScale = 1f
    )
    {
        if (id == EFx.None)
            return;

        if (!IsSpawned)
        {
            PlayHere(id, position, normal, volumeScale); // 오프라인 — RPC 경로가 없다
            return;
        }

        // 클라가 부르면 그 피어에만 나온다. 조용히 반쪽으로 도는 대신 배선 실수를 드러내고 로컬 재생은 해 준다 —
        // 연출이 아예 사라지면 원인을 찾기가 더 어렵다.
        if (!IsServer)
        {
            WarnOnce(id, $"{id}를 클라이언트에서 전파 요청했다 — 서버 판정 지점에서 부를 것");
            PlayHere(id, position, normal, volumeScale);
            return;
        }

        PlayRpc(id, position, normal, volumeScale);
    }

    /// <summary>
    /// 부른 피어에서만 1회 재생한다 — <b>이미 전 피어에서 도는 경로</b>에서 쓴다
    /// (<c>SendTo.Everyone</c> RPC 안, 전 피어에서 발생하는 이벤트 구독 등).
    /// 그런 자리에서 <see cref="PlayEverywhere"/>를 부르면 피어마다 다시 전파돼 소리가 겹친다.
    /// </summary>
    public void PlayHere(
        EFx id,
        Vector3 position,
        Vector3 normal = default,
        float volumeScale = 1f
    )
    {
        if (id == EFx.None)
            return;

        if (!m_index.TryGetValue(id, out Entry entry))
        {
            WarnOnce(id, $"조합표에 {id} 항목이 없다 — 연출을 건너뛴다");
            return;
        }

        // 매니저가 없는 구성(로비·테스트 씬·부트스트랩 없는 직접 Play)에서는 조용히 넘어간다 (R8 관례).
        // 항목의 Effect·Sound가 None이면 각 매니저가 스스로 무동작한다 — 여기서 따로 갈래를 만들지 않는다.
        App.Game.Effect?.Play(entry.Effect, position, normal);
        App.Sound?.PlaySfxAt(entry.Sound, position, volumeScale);
    }

    // 연출 오브젝트를 네트워크에 싣지 않는다 — 각 피어가 자기 화면에 스스로 만든다
    // (docs/architecture.md 연출 전파 규칙의 '일회성 연출' 행).
    [Rpc(SendTo.Everyone)]
    private void PlayRpc(EFx id, Vector3 position, Vector3 normal, float volumeScale) =>
        PlayHere(id, position, normal, volumeScale);

    private void BuildIndex()
    {
        if (m_entries == null)
        {
            Debug.LogWarning($"[FxManager] 조합표가 비어 있다 — 모든 연출이 나오지 않는다. {name}에 채울 것", this);
            return;
        }

        foreach (Entry entry in m_entries)
        {
            if (entry == null || entry.Id == EFx.None)
                continue;

            // 중복은 조용히 덮어쓰지 않는다 — 어느 쪽이 쓰이는지 모르는 상태를 만들지 않기 위해서다.
            if (!m_index.TryAdd(entry.Id, entry))
                Debug.LogError($"[FxManager] 조합표에 {entry.Id}가 중복 등록됐다 — 먼저 오는 항목만 쓰인다", this);
        }
    }

    private void WarnOnce(EFx id, string reason)
    {
        if (m_warned.Add(id))
            Debug.LogWarning($"[FxManager] {reason}", this);
    }
}
