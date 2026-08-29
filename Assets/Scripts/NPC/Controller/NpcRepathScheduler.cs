using System.Collections.Generic;
using UnityEngine;

/// <summary>재탐색·훑기 채널 — 채널마다 만료 시각을 따로 센다. (#573)</summary>
public enum NpcRepathChannel
{
    /// <summary>목적지 재계산 — 유일하게 거리 티어를 탄다.</summary>
    Repath = 0,
    /// <summary>도주 중 추적자 훑기 — 이탈 판정이 함께 걸려 있어 고정 주기.</summary>
    ThreatScan = 1,
    /// <summary>추격 표적 후보 훑기 — 범위 내 플레이어 전수 순회.</summary>
    TargetScan = 2,
    /// <summary>도주 막힘 판정.</summary>
    StuckCheck = 3,
}

/// <summary>
/// NPC 하나의 재탐색 주기를 관리한다 — "지금 다시 계산할 때인가"에만 답한다. (#573)
///
/// <b>NPC당 하나</b>다(상태당 X). 티어 판정 재료인 "가장 가까운 플레이어 거리"를 상태마다 다시 재지 않기
/// 위해서고, 상태를 오갈 때 위상이 초기화되지 않게 하기 위해서이기도 하다.
///
/// <b>위상은 생성 시 한 번만 흩뿌린다.</b> 상태 진입마다 난수를 새로 뽑으면 같은 프레임에 같은 상태로
/// 전이한 무리가 다시 뭉친다 — 애초에 고치려던 그 현상이다. 만료 시각을 절대 시각(Time.time)으로 두는 것도
/// 같은 이유다: 상태가 바뀌어도 이 NPC의 위상이 유지된다.
/// </summary>
public class NpcRepathScheduler
{
    private const int k_channelCount = 4;

    // 플레이어 위치 캐시 — 프레임당 1회만 수집한다. NPC마다 FindObjectsByType을 도는 것이
    // 원래 줄이려던 비용보다 커지는 것을 막는다 (SuddenEventUtil.CollectFieldPlayers는 호출마다 전수 검색).
    private static readonly List<Transform> s_players = new List<Transform>();
    private static int s_playersFrame = -1;

    // 배선이 빠졌을 때 쓰는 코드 기본값 — NPC 전체가 공유한다.
    private static NpcRepathConfig s_fallbackConfig;

    private readonly NpcRepathConfig m_config;
    private readonly Transform m_owner;
    private readonly float[] m_nextDue = new float[k_channelCount];

    private float m_tierDistance = float.MaxValue;
    private float m_nextTierSample;

    public NpcRepathScheduler(NpcRepathConfig config, Transform owner)
    {
        m_config = Resolve(config, owner);
        m_owner = owner;

        // 위상 분산 — 첫 만료를 [0, interval) 안의 임의 시점으로 흩뿌린다.
        float now = Time.time;
        for (int i = 0; i < k_channelCount; i++)
            m_nextDue[i] = now + Random.Range(0f, BaseInterval((NpcRepathChannel)i));

        // 티어 표본도 같이 흩뿌린다 — 안 그러면 표본 채집이 한 프레임에 몰린다.
        m_nextTierSample = now + Random.Range(0f, m_config.TierSampleInterval);
    }

    // 설정이 비어 있으면 코드 기본값으로 버틴다. 예전에는 주기가 const라 실패할 수 없던 자리인데,
    // SO로 옮기면서 "배선 누락 = Awake에서 NRE = 그 NPC가 통째로 죽는다"가 됐다. 새 SerializeField는
    // 기존 프리팹에 자동 전파되지 않으므로 나중에 만들어지는 프리팹이 조용히 이걸 밟는다.
    // 조용히 넘기지는 않는다 — 어느 오브젝트가 비었는지 에러로 남겨 프리팹을 고치게 한다.
    private static NpcRepathConfig Resolve(NpcRepathConfig config, Transform owner)
    {
        if (config != null)
            return config;

        s_fallbackConfig ??= ScriptableObject.CreateInstance<NpcRepathConfig>();
        Debug.LogError(
            $"NpcRepathConfig가 비어 있다 — 코드 기본값으로 대체한다. NpcController의 m_repathConfig를 채울 것: {owner.name}",
            owner
        );

        return s_fallbackConfig;
    }

    /// <summary>
    /// 이 채널을 지금 돌 차례인가 — true를 돌려준 그 순간 다음 만료를 예약한다.
    /// 한 프레임에 두 번 물으면 두 번째는 false다. 호출부는 결과를 그대로 게이트로 쓰면 된다.
    /// </summary>
    public bool Due(NpcRepathChannel channel)
    {
        float now = Time.time;
        int index = (int)channel;
        if (now < m_nextDue[index])
            return false;

        m_nextDue[index] = now + IntervalFor(channel);
        return true;
    }

    /// <summary>
    /// 방금 직접 계산했다고 표시한다 — 게이트를 거치지 않고 <c>SetDestination</c>을 부른 자리에서.
    /// 이걸 빼먹으면 그 직후 게이트가 또 열려 같은 프레임 근처에서 두 번 계산한다.
    /// </summary>
    public void MarkDone(NpcRepathChannel channel)
    {
        m_nextDue[(int)channel] = Time.time + IntervalFor(channel);
    }

    /// <summary>
    /// 다음 Due를 즉시 통과시킨다 — 상태 진입처럼 <b>한 번은 반드시 계산해야</b> 하는 자리에서만.
    /// 남용하면 위상 분산이 무의미해지므로 Enter에서만 쓴다.
    /// </summary>
    public void ForceDue(NpcRepathChannel channel)
    {
        m_nextDue[(int)channel] = 0f;
    }

    /// <summary>채널의 현재 주기(초) — 로그·디버그 표시용. 조회만 하고 아무것도 바꾸지 않는다.</summary>
    public float IntervalOf(NpcRepathChannel channel) =>
        channel == NpcRepathChannel.Repath ? PickTierInterval() : BaseInterval(channel);

    private float IntervalFor(NpcRepathChannel channel) =>
        channel == NpcRepathChannel.Repath ? TieredRepathInterval() : BaseInterval(channel);

    private float BaseInterval(NpcRepathChannel channel)
    {
        switch (channel)
        {
            case NpcRepathChannel.ThreatScan:
                return m_config.ThreatScanInterval;
            case NpcRepathChannel.TargetScan:
                return m_config.TargetScanInterval;
            case NpcRepathChannel.StuckCheck:
                return m_config.StuckCheckInterval;
            default:
                return m_config.MidInterval;
        }
    }

    private float TieredRepathInterval()
    {
        float now = Time.time;
        if (now >= m_nextTierSample)
        {
            m_nextTierSample = now + m_config.TierSampleInterval;
            m_tierDistance = NearestPlayerDistance();
        }

        return PickTierInterval();
    }

    // 표본을 새로 뜨지 않고 마지막 거리로만 고른다 — IntervalOf가 조회만으로 부수효과를 내지 않게.
    private float PickTierInterval()
    {
        if (m_tierDistance <= m_config.NearDistance)
            return m_config.NearInterval;
        return m_tierDistance <= m_config.MidDistance ? m_config.MidInterval : m_config.FarInterval;
    }

    private float NearestPlayerDistance()
    {
        RefreshPlayers();
        if (s_players.Count == 0)
            return float.MaxValue;

        Vector3 origin = m_owner.position;
        float nearestSqr = float.MaxValue;
        for (int i = 0; i < s_players.Count; i++)
        {
            Transform player = s_players[i];
            if (player == null)
                continue;

            // 수평 거리 — 층이 갈린 경우를 티어 기준으로 삼지 않는다(추격 판정과 같은 기준).
            Vector3 delta = player.position - origin;
            delta.y = 0f;

            float sqr = delta.sqrMagnitude;
            if (sqr < nearestSqr)
                nearestSqr = sqr;
        }

        return nearestSqr == float.MaxValue ? float.MaxValue : Mathf.Sqrt(nearestSqr);
    }

    private static void RefreshPlayers()
    {
        if (s_playersFrame == Time.frameCount)
            return;
        s_playersFrame = Time.frameCount;

        s_players.Clear();

        // SuddenEventUtil.CollectFieldPlayers와 <b>기준이 다르다</b> — 저쪽은 IsTargetable만 모으지만
        // 여기는 다운된 플레이어도 넣는다. 티어는 "누구를 노릴 수 있는가"가 아니라 "누가 보고 있는가"라
        // 쓰러진 플레이어 주변도 촘촘해야 하기 때문이다. 저쪽과 합치지 않는 이유가 이것이다.
        PlayerHealth[] found = Object.FindObjectsByType<PlayerHealth>(FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
            s_players.Add(found[i].transform);
    }
}
