using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 일회성 이펙트 재생 + 오브젝트 풀링. <see cref="App"/>.Game.Effect로 접근한다. (#478)
/// <b>순수 로컬 연출이다</b> — 네트워크를 모른다. 서버가 판정 지점에서 전 피어 RPC를 쏘고
/// 각 피어가 자기 화면에 이 매니저로 재생한다 (연출 오브젝트를 네트워크에 싣지 않으므로
/// 프리팹 등록이 필요 없다 — <see cref="NpcDespawnVfx"/>가 먼저 쓴 방식이다).
///
/// <b>사운드는 다루지 않는다</b> — <see cref="SoundManager"/>가 따로 가진다. 덕분에 같은 연출을
/// 대상에 따라 다른 소리로 낼 때 이펙트 프리팹을 종류별로 복제하지 않아도 된다
/// (진압봉 먼지는 1종인데 타격음은 깡·퍽·둔탁 3종이다).
///
/// 인게임 씬 매니저라 로비·상점에는 없다 — 사용처는 <c>App.Game.Effect?.Play(...)</c>로 가드할 것.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.BaseManagement)]
public class EffectManager : CommonManagerBase
{
    [Tooltip("이펙트 카탈로그 — 비우면 모든 재생 요청이 무동작한다")]
    [SerializeField] private EffectLibrary m_library;

    // 풀에 든 인스턴스 한 개분. 파티클 참조를 함께 들고 다니는 이유는 재사용마다
    // GetComponentInChildren을 다시 돌지 않기 위함이다 — 풀의 존재 이유와 같은 취지.
    private class Pooled
    {
        public GameObject Instance;
        public ParticleSystem Particles; // 없을 수도 있다(파티클 아닌 연출 프리팹)
    }

    // 재생 중인 인스턴스와 반납 예정 시각.
    private struct Playing
    {
        public EEffect Id;
        public Pooled Item;
        public float ReturnTime;
    }

    private readonly Dictionary<EEffect, EffectLibrary.Entry> m_entries = new();
    private readonly Dictionary<EEffect, Stack<Pooled>> m_pools = new();
    private readonly List<Playing> m_playing = new();

    // 배선 사고를 알리되 매 프레임 도배하지 않는다 — 타격은 초당 여러 번 들어올 수 있다.
    private readonly HashSet<EEffect> m_warned = new();

    protected override void Awake()
    {
        base.Awake(); // App.Game.Effect 등록 (R5)

        BuildIndex();
        PrewarmAll();
    }

    /// <summary>
    /// 이펙트를 한 번 재생한다. 어느 피어에서 불러도 그 피어에서만 보인다(로컬 연출).
    /// </summary>
    /// <param name="id">카탈로그 키. <see cref="EEffect.None"/>이면 무동작이다(의도적 '연출 없음')</param>
    /// <param name="position">재생 위치 — 월드 좌표</param>
    /// <param name="normal">
    /// 이펙트가 향할 방향(맞은 면의 법선 등). 프리팹의 로컬 +Z가 이쪽을 보게 회전한다.
    /// 영벡터를 넘기면 회전 없이(<see cref="Quaternion.identity"/>) 재생한다.
    /// </param>
    public void Play(EEffect id, Vector3 position, Vector3 normal)
    {
        if (id == EEffect.None)
            return;

        if (!m_entries.TryGetValue(id, out EffectLibrary.Entry entry))
        {
            WarnOnce(id, $"카탈로그에 {id} 항목이 없다");
            return;
        }

        if (entry.Prefab == null)
        {
            WarnOnce(id, $"{id} 항목에 프리팹이 배정되지 않았다");
            return;
        }

        Pooled item = Rent(id, entry.Prefab);
        item.Instance.transform.SetPositionAndRotation(position, RotationFor(normal));
        item.Instance.SetActive(true);

        // playOnAwake는 OnEnable에서 걸리지만, 재사용 인스턴스는 이전 재생의 잔여 파티클을
        // 들고 있을 수 있다. 명시적으로 비우고 다시 트는 편이 상태가 확실하다.
        if (item.Particles != null)
        {
            item.Particles.Clear(true);
            item.Particles.Play(true);
        }

        m_playing.Add(new Playing
        {
            Id = id,
            Item = item,
            ReturnTime = Time.time + entry.LifetimeSeconds,
        });
    }

    // 수명이 지난 인스턴스를 회수한다. 동시 재생 개수가 한 자릿수라 매 프레임 순회해도 무게가 없다.
    // 뒤에서부터 도는 이유는 순회 중 제거 때문이다.
    private void Update()
    {
        float now = Time.time;

        for (int i = m_playing.Count - 1; i >= 0; i--)
        {
            if (now < m_playing[i].ReturnTime)
                continue;

            Return(m_playing[i]);
            m_playing.RemoveAt(i);
        }
    }

    // 씬 언로드로 매니저가 죽으면 자식 인스턴스도 함께 파괴된다 — 별도 정리가 필요 없다.
    // 다만 재생 목록은 비워 둔다(도메인 리로드를 끈 채 다시 Play할 때 죽은 참조를 만지지 않게).
    protected override void OnDestroy()
    {
        m_playing.Clear();
        m_pools.Clear();
        base.OnDestroy(); // App 등록 해제 (R5)
    }

    // ---- 카탈로그 ----

    private void BuildIndex()
    {
        if (m_library == null)
        {
            Debug.LogWarning($"[EffectManager] 이펙트 카탈로그가 배정되지 않았다 — 모든 연출이 나오지 않는다. {name}에 지정할 것", this);
            return;
        }

        foreach (EffectLibrary.Entry entry in m_library.Entries)
        {
            if (entry == null || entry.Id == EEffect.None)
                continue;

            // 중복은 조용히 덮어쓰지 않는다 — 어느 쪽이 쓰이는지 모르는 상태를 만들지 않기 위해서다.
            if (!m_entries.TryAdd(entry.Id, entry))
                Debug.LogError($"[EffectManager] 카탈로그에 {entry.Id}가 중복 등록됐다 — 먼저 오는 항목만 쓰인다", m_library);
        }
    }

    private void PrewarmAll()
    {
        foreach (EffectLibrary.Entry entry in m_entries.Values)
        {
            if (entry.Prefab == null || entry.Prewarm <= 0)
                continue;

            Stack<Pooled> pool = PoolFor(entry.Id);
            for (int i = 0; i < entry.Prewarm; i++)
                pool.Push(Create(entry.Prefab));
        }
    }

    // ---- 풀 ----

    private Pooled Rent(EEffect id, GameObject prefab)
    {
        Stack<Pooled> pool = PoolFor(id);

        // 파괴된 인스턴스(씬 전환 도중 등)는 건너뛰고 다음을 꺼낸다.
        while (pool.Count > 0)
        {
            Pooled item = pool.Pop();
            if (item.Instance != null)
                return item;
        }

        return Create(prefab);
    }

    private void Return(Playing playing)
    {
        if (playing.Item.Instance == null)
            return; // 이미 파괴됐다 — 풀에 되돌리면 죽은 참조가 쌓인다

        playing.Item.Instance.SetActive(false);
        PoolFor(playing.Id).Push(playing.Item);
    }

    private Stack<Pooled> PoolFor(EEffect id)
    {
        if (!m_pools.TryGetValue(id, out Stack<Pooled> pool))
        {
            pool = new Stack<Pooled>();
            m_pools[id] = pool;
        }

        return pool;
    }

    // 매니저 자식으로 만든다 — 하이어라키가 정리되고, 씬이 내려갈 때 함께 사라진다.
    // 매니저 오브젝트는 움직이지 않으므로 부모를 따라 이펙트가 끌려다닐 걱정은 없다
    // (RopeDragView가 먼지를 월드에 독립시킨 건 부모가 움직이는 플레이어였기 때문이다).
    private Pooled Create(GameObject prefab)
    {
        GameObject instance = Instantiate(prefab, transform);
        instance.SetActive(false);

        return new Pooled
        {
            Instance = instance,
            Particles = instance.GetComponentInChildren<ParticleSystem>(true),
        };
    }

    // ---- 보조 ----

    private static Quaternion RotationFor(Vector3 normal) =>
        normal.sqrMagnitude < 0.0001f ? Quaternion.identity : Quaternion.LookRotation(normal);

    private void WarnOnce(EEffect id, string reason)
    {
        if (m_warned.Add(id))
            Debug.LogWarning($"[EffectManager] {reason} — 해당 연출을 건너뛴다", this);
    }
}
