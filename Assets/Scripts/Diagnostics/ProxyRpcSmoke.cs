using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// <b>⚠ 임시 계측이다 — 3-1이 끝나면 파일째 지운다.</b>
/// (계획서 <c>docs/728-player-pose-streaming.md</c> 3-1)
///
/// <b>재는 것 하나: 오너(클라)가 쏜 언리라이어블 RPC가 서버를 거쳐 다른 피어에 닿는가.</b>
///
/// NPC 래그돌 스트리밍은 <b>서버 → 전원</b>만 써 봤다. 플레이어 시체는 권위가 <b>오너</b>라
/// 방향이 뒤집히는데(<c>클라 → 서버 → 전원</c>), NGO가 그 프록시를 어떻게 다루는지 이 프로젝트에서
/// 한 번도 확인한 적이 없다. 특히 <b>언리라이어블 + 배열 페이로드</b>가 프록시 구간에서
/// 그대로 가는지, 한 홉이 늘어난 만큼 지연이 얼마나 붙는지가 미지수다.
///
/// <b>스트리머를 붙이기 전에 이것부터 하는 이유</b>는 실패했을 때 원인이 갈리기 때문이다 —
/// 스트리머와 함께 붙이면 "프록시가 안 되는 건지 내 배선이 틀린 건지"가 섞인다.
///
/// <b>붙이는 곳: <c>Player</c> 프리팹 루트</b> (오너 권한 <c>NetworkObject</c>가 있는 그 자리).
/// 실제 스트리머가 갈 자리와 같아야 권위·프록시 조건이 동일하다.
///
/// <b>쓰는 법</b> — MPPM 2인 이상, <b>클라이언트가 오너인 플레이어</b>에서:
/// <list type="bullet">
///   <item><c>F9</c> — 단발 1개. 배선이 통하는지만 본다</item>
///   <item><c>F10</c> — 실제 스트림과 같은 조건으로 3초간 25Hz 연사. 손실·지연을 여기서 잰다</item>
/// </list>
/// 로그는 보내는 쪽 <c>[프록시RPC 송신]</c>, 받는 쪽 <c>[프록시RPC 수신]</c>이다.
/// ⚠ MPPM 가상 플레이어 콘솔은 도구로 못 읽으므로 <c>Library/VP/&lt;가상플레이어&gt;/Logs/</c>가 정본이다.
/// </summary>
public class ProxyRpcSmoke : NetworkBehaviour
{
    // 실제 스트리머와 같은 조건으로 쏜다 — 여기서 잰 값을 그대로 3-2의 판단에 쓰려면
    // 페이로드 모양과 주기가 같아야 한다 (뼈 11개 × 4B 압축 쿼터니언).
    private const int k_boneCount = 11;
    private const float k_sendInterval = 0.04f; // 25Hz
    private const float k_burstSeconds = 3f;

    private uint[] m_payload;
    private ushort m_sequence;
    private float m_burstRemaining;
    private float m_sendTimer;

    // 수신 쪽 집계
    private int m_received;
    private int m_lost; // 시퀀스 구멍 = 진짜 손실
    private int m_stale; // 늦게 도착해 버린 것 = 재정렬
    private ushort m_newestSequence;
    private bool m_haveSequence;
    private double m_delaySum;
    private double m_delayMax;

    private void Update()
    {
        // 쏘는 것은 오너뿐이다 — 실제 스트리머의 권위 조건과 같게 맞춘다.
        if (!IsSpawned || !IsOwner)
            return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.f9Key.wasPressedThisFrame)
            {
                SendOne();
                Debug.Log($"[프록시RPC 송신] 단발 seq={m_sequence}", this);
            }

            if (keyboard.f10Key.wasPressedThisFrame)
            {
                m_burstRemaining = k_burstSeconds;
                m_sendTimer = 0f;
                Debug.Log(
                    $"[프록시RPC 송신] 연사 시작 — {k_burstSeconds}초 × 25Hz "
                        + $"(예상 {Mathf.RoundToInt(k_burstSeconds / k_sendInterval)}개)",
                    this
                );
            }
        }

        if (m_burstRemaining <= 0f)
            return;

        m_burstRemaining -= Time.deltaTime;
        m_sendTimer += Time.deltaTime;
        if (m_sendTimer < k_sendInterval)
            return;

        m_sendTimer = 0f;
        SendOne();

        if (m_burstRemaining <= 0f)
            Debug.Log($"[프록시RPC 송신] 연사 끝 — 총 seq={m_sequence}", this);
    }

    private void SendOne()
    {
        if (m_payload == null || m_payload.Length != k_boneCount)
            m_payload = new uint[k_boneCount];

        m_sequence = unchecked((ushort)(m_sequence + 1));

        // 보낸 시각을 <b>서버 시각</b>으로 찍는다 — 피어마다 로컬 시각은 기준이 달라 비교할 수 없다.
        // NGO의 ServerTime은 전 피어가 같은 값으로 수렴하므로 받는 쪽에서 차를 내면 한 방향 지연이 된다.
        SmokeRpc(m_sequence, NetworkManager.ServerTime.Time, m_payload);
    }

    // 실제 스트리머와 <b>같은 속성</b>이어야 의미가 있다 — SendTo.NotMe · 언리라이어블 · 배열 파라미터.
    [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Unreliable)]
    private void SmokeRpc(ushort sequence, double sentServerTime, uint[] payload)
    {
        m_received++;

        double delay = NetworkManager.ServerTime.Time - sentServerTime;
        m_delaySum += delay;
        if (delay > m_delayMax)
            m_delayMax = delay;

        // ⚠ <b>역순부터 걸러야 한다.</b> 뺄셈이 부호 없는 ushort라, 옛 패킷이면 차가
        // 65534 같은 거대한 값이 되어 <b>손실 수만 개로 잘못 집계된다.</b> "더 새것인가"를
        // 먼저 물어 갈라내는 것이 유일하게 맞는 순서다.
        if (m_haveSequence && !IsNewer(sequence))
        {
            m_stale++;
        }
        else
        {
            if (m_haveSequence)
            {
                int gap = unchecked((ushort)(sequence - m_newestSequence)) - 1;
                if (gap > 0)
                    m_lost += gap;
            }

            m_newestSequence = sequence;
            m_haveSequence = true;
        }

        // 한 줄에 다 담는다 — 여러 줄로 쓰면 MPPM 로그에서 잘린다.
        Debug.Log(
            $"[프록시RPC 수신] seq={sequence} 페이로드={payload?.Length ?? -1} "
                + $"지연={delay * 1000.0:F0}ms 누적(수신={m_received} 결손={m_lost} 역순={m_stale} "
                + $"평균지연={m_delaySum / m_received * 1000.0:F0}ms 최대={m_delayMax * 1000.0:F0}ms)",
            this
        );
    }

    // ushort 랩어라운드를 견디는 "더 새것인가" — 실제 스트리머와 같은 판정.
    private bool IsNewer(ushort candidate) =>
        unchecked((ushort)(candidate - m_newestSequence)) is > 0 and < 32768;
}
