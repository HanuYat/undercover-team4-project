using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 래그돌 자세를 <b>전 뼈 통째로</b> 원격에 흘려보낸다 — 권위 피어가 굴린 물리를 나머지가 재생한다.
/// (계획서 <c>docs/728-ragdoll-pose-streaming.md</c> 1단계)
///
/// <b>기존 구조의 불변식 1을 뒤집는 부품이다.</b> 지금까지는 각 피어가 자기 물리를 굴리고 궤적만
/// 골반 하나로 받았다(<c>docs/ragdoll.md</c> 불변식 1). 그래서 피어마다 몸이 갈렸고, 그 차이를
/// 좁히는 보정·스냅·속도캡이 층층이 쌓였다. 이 부품은 그 전제를 바꾼다 — <b>시뮬레이션은 하나뿐이고
/// 원격은 결과만 받는다.</b> 원격의 뼈에는 물리가 돌지 않으므로 <b>위반될 관절이 원리적으로 없다.</b>
///
/// <b>이 부품은 물리를 모른다.</b> 뼈를 키네마틱으로 두는 것도, 언제 래그돌이 되는지도 소유자
/// (<c>NpcRagdoll</c>·<c>PlayerRagdoll</c>)가 정한다. 여기가 아는 것은 <b>"지금 보낼 자세"와
/// "받은 자세를 입히는 법"</b>뿐이다. 반대쪽 짝인 <see cref="RagdollRig"/>가 네트워크를 모르는 것과
/// 같은 선이다.
///
/// <b>⚠ 골반 좌표계가 국면에 따라 갈린다 — 이 부품의 핵심 결정이다</b> (계획서 §1-3):
///
/// <list type="table">
///   <item><term>스트리밍 중</term><description>골반은 <b>월드</b>. 루트 NetworkTransform과 완전히
///   독립이라 두 스트림의 지연 차가 오차로 새지 않는다. 로컬로 보내면 몸이 루트 보간값 위에 얹혀
///   실측 0.25m까지 벌어지던 <c>루트↔골반수평</c> 항이 그대로 되살아난다.</description></item>
///   <item><term>정착(얼림)</term><description><see cref="EndStreaming"/>이 <b>로컬</b>로 한 번
///   보내고 스트림을 끊는다. 그때부터 몸의 주인은 루트다 — 뼈가 키네마틱 자식이라 루트를 옮기면
///   딸려 온다. 유치장 순간이동이 성립하는 자리가 여기다.</description></item>
/// </list>
///
/// <b><c>NpcDeath</c>가 정착 자세를 1회 뿌리던 경로를 흡수했다.</b> 그쪽은 지웠고, 그 1회는
/// 이제 스트림의 <b>마지막 패킷</b>(<see cref="EndStreaming"/>)이다 — 자세가 가는 통로는 하나만 남긴다.
/// </summary>
[DisallowMultipleComponent]
public class RagdollPoseStreamer : NetworkBehaviour
{
    /// <summary>
    /// 누가 이 시체의 자세를 정하는가 — <b>프리팹 값으로 못박는다.</b>
    ///
    /// 자동 감지에 기대지 않는 이유는 NGO의 <c>HasAuthority</c>가 클라이언트-서버 모드에서 항상
    /// <c>IsServer</c>이기 때문이다. NPC는 그게 맞지만 <b>플레이어 시체는 오너 권한</b>이라
    /// (루트 NetworkTransform의 <c>AuthorityMode</c>와 짝) 그대로 쓰면 조용히 틀린다.
    /// </summary>
    public enum PoseAuthority
    {
        Server, // NPC — 서버가 물리를 굴린다
        Owner,  // 플레이어 — 그 몸의 오너가 굴린다
    }

    // 원격이 들고 있을 스냅샷 수. 보간에 2개가 필요하고, 지터·재정렬에 쓸 여유가 조금 있으면 된다.
    // 크게 잡을 이유는 없다 — 오래된 스냅샷은 어차피 재생 시점보다 뒤라 쓸 일이 없고,
    // 버퍼가 길수록 화면이 그만큼 더 늦다.
    private const int k_maxSnapshots = 4;

    // 시퀀스 비교의 절반 지점 — ushort가 한 바퀴 돌아도 "더 새것"을 올바로 가른다.
    // (65535 다음이 0이므로 대소 비교를 그대로 쓰면 랩어라운드 순간 스트림이 통째로 막힌다)
    private const ushort k_sequenceHalfRange = 32768;

    [Header("권위")]
    [Tooltip("이 시체의 자세를 누가 정하는가 — NPC는 Server, 플레이어는 Owner.\n\n" +
             "⚠ 루트 NetworkTransform의 AuthorityMode와 <b>반드시 같아야</b> 한다. 어긋나면 " +
             "몸과 루트가 서로 다른 피어에서 계산돼 시체가 이름표를 두고 떠난다")]
    [SerializeField] private PoseAuthority m_authority = PoseAuthority.Server;

    [Header("송신")]
    [Tooltip("몇 번의 물리 스텝마다 한 번 보내는가 — 50Hz 기준 2면 25Hz, 4면 12.5Hz.\n\n" +
             "<b>대역폭의 유일한 1차 손잡이다</b>(계획서 §1-6). 실측으로 시체 1구당 원격 1인 기준 " +
             "<b>1.5KB/s</b>(25Hz · 페이로드 62B)이고, 서버 업링크는 여기에 <b>동시 시체 수 × 원격 " +
             "수</b>가 곱해진다. 예산이 빠듯하면 여기부터 올린다 — 잃는 것은 보간 지연뿐이고 " +
             "정착 자세는 그대로다")]
    [SerializeField] private int m_sendEveryFixedSteps = 2;

    [Header("수신")]
    [Tooltip("원격이 얼마나 뒤처진 시점을 그리는가(초) — 송신 주기의 2배가 기본값이다.\n\n" +
             "이만큼 늦게 그려야 다음 스냅샷이 이미 도착해 있어 <b>보간할 두 점</b>이 생긴다. " +
             "짧으면 패킷 하나만 늦어도 재생이 끝점에 부딪혀 시체가 멈칫하고, 길면 그만큼 " +
             "화면이 늦는다")]
    [SerializeField] private float m_interpolationDelay = 0.08f;

    private RagdollRig m_rig;

    // ---- 송신 상태 (권위 피어) ----

    private bool m_streaming;
    private ushort m_sequence;
    private int m_stepsSinceSend;
    private Quaternion[] m_sendBuffer;  // 캐처용 — 매 스텝 새로 할당할 이유가 없다
    private uint[] m_packedBuffer;      // 실제로 선에 실리는 것 — 쿼터니언당 4바이트
    private Quaternion[] m_unpackBuffer; // 수신 쪽 — 푸는 자리

    // ---- 수신 상태 (원격) ----

    // 도착한 자세들 — 0이 가장 오래됐다. 링이 아니라 밀어내기인 것은 길이가 4라 옮기는 비용이
    // 인덱스 산술보다 싸고, "0이 가장 오래됐다"가 보간 코드를 훨씬 읽기 쉽게 만들기 때문이다.
    private Snapshot[] m_snapshots;
    private int m_snapshotCount;

    private ushort m_newestSequence;
    private bool m_haveSequence;

    // 스트림이 이 몸을 쥐고 있는가 — 참인 동안 원격은 매 프레임 자세를 대입한다.
    // 마지막(정착) 패킷을 받으면 거짓이 되고, 그때부터 몸은 루트 계층이 옮긴다.
    private bool m_streamDriven;

    private Quaternion[] m_applyBuffer; // 두 스냅샷을 섞어 담는 자리

    // ---- 국면 추적 ----
    //
    // <b>둘을 가르는 이유는 <see cref="IsAwaitingFirstPose"/>에 적혀 있다</b> — "아직 안 왔다"와
    // "다 받고 끝났다"를 하나로 물으면 정착 자세가 진입 자세로 덮인다.
    private bool m_expectingStream; // 전 피어 — 지금 자세가 흘러야 하는 국면인가
    private bool m_hasReceivedPose;    // 원격 — 이번 국면에 한 개라도 받았는가
    private bool m_warnedBoneMismatch; // 배선 불일치 경고를 한 번만 내기 위해

    private struct Snapshot
    {
        public float Time;         // 받은 시각(로컬). ⚠ 서버 시각이 아니다 — 아래 TickApply 주석
        public Vector3 HipsWorld;
        public Quaternion[] Rotations;
    }

    // ---- 질의 ----

    /// <summary>
    /// 이 피어가 자세를 정하는 쪽인가 — 물리를 굴리고 보내는 쪽. <b>세션이 아니면 항상 참이다</b>
    /// (오프라인 Play에서는 자기가 유일한 피어다).
    /// </summary>
    public bool IsPoseAuthority
    {
        get
        {
            if (!IsSpawned)
                return true;

            return m_authority == PoseAuthority.Server ? IsServer : IsOwner;
        }
    }

    /// <summary>
    /// 지금 스트림이 이 몸의 자세를 쥐고 있는가 — 원격에서만 참이 된다.
    /// 소유자가 "내가 물리로 건드려도 되는가"를 묻는 자리다.
    /// </summary>
    public bool IsStreamDriven => m_streamDriven;

    /// <summary>
    /// 원격이 <b>아직 첫 자세를 못 받았는가</b> — 소유자가 그 빈 구간을 메울지 묻는 자리다.
    ///
    /// ⚠ <b><see cref="IsStreamDriven"/>의 반대가 아니다.</b> 저것이 거짓인 경우는 둘인데 뜻이
    /// 정반대다: <b>아직 안 왔다</b>(메워야 한다)와 <b>다 오고 끝났다</b>(정착 자세가 확정이니
    /// 건드리면 안 된다). 하나로 물으면 정착 자세를 받은 직후 다시 메우기가 켜져 <b>진입 시점의
    /// 자세(서 있는 몸)가 정착 자세를 덮어쓴다</b> — 실측된 증상이 정확히 그것이었다:
    /// 다 쓰러진 시체가 마지막에 벌떡 선 자세로 바뀐다.
    /// </summary>
    public bool IsAwaitingFirstPose => m_expectingStream && !m_hasReceivedPose;

    /// <summary>
    /// 정착 자세를 받아 입혔다 — <b>원격에서만 발행된다.</b> 소유자가 여기서 몸을 얼린다.
    ///
    /// 자세 자체는 이 부품이 이미 입혔으므로 구독자가 할 일은 <b>상태 전이</b>뿐이다. 그 분업이
    /// 이 클래스가 물리를 모르는 이유이기도 하다 — 무엇이 "얼림"인지는 소유자만 안다.
    /// </summary>
    public event System.Action OnSettledPoseReceived;

    // ⚠ <b>여기 "골반 위치는 옛 배선(골반 NT)에 맡긴다"는 전환기 스위치가 있었다 — 걷어냈다.</b>
    //
    // 근거는 "둘 다 골반을 쥐면 같은 서버 값의 다른 지연본이 엇갈려 떤다"였는데, <b>맡기는 쪽이
    // 훨씬 나빴다.</b> 골반 NT는 FixedUpdate에 적용되고 루트 NT는 그와 다른 시점에 적용되는데,
    // 원격의 뼈는 전부 키네마틱이라 <b>그 사이에 루트를 따라 통째로 끌려간다.</b> 래그돌 중 루트는
    // 골반을 따라다니므로 진입에 발밑→골반으로 <b>0.9m를 한 방에</b> 뛰고 정착에 지면으로 되돌아온다
    // — 실측된 증상이 정확히 그것이었다: <b>클라에서 시체가 떠서 시작하고 착지할 때 땅속에 들어갔다
    // 나온다.</b> 서버는 <c>CapturePose</c>/<c>RestoreCapturedPose</c>가 그 왕복을 감싸 무사하지만
    // 클라에는 그 감싸기가 없다(<c>NpcRagdoll.TickRootFollow</c> 주석이 미리 적어 두고 있었다).
    //
    // <b>월드로 매 프레임 못박는 것이 그 경로를 통째로 끊는다</b>(계획서 §1-3) — 루트가 어디로
    // 가든 몸은 스트림이 놓은 자리에 있는다. 지연 문제가 아니라 <b>앵커가 두 클럭에 걸쳐 있던</b>
    // 문제였고, 그래서 핑이 0이어도 똑같이 났다.

    private void Awake()
    {
        // ⚠ 리그가 붙는 자리는 개체마다 다르다 — NPC는 <c>Model</c> 밑, 플레이어는 <c>Corpse</c> 밑이다.
        // 평시 비활성일 수 있으므로 includeInactive를 켠다.
        m_rig = GetComponentInChildren<RagdollRig>(true);
        if (m_rig == null)
        {
            Debug.LogWarning(
                $"RagdollPoseStreamer: RagdollRig를 찾지 못해 자세 스트리밍을 끈다 — {name}",
                this
            );
            enabled = false;
            return;
        }

        // 소유자의 Awake보다 먼저 돌 수 있다 — 리그 수집은 멱등이라 여기서 보장해도 된다.
        m_rig.EnsureCollected();
    }

    // ---- 송신 (권위 피어) ----

    /// <summary>
    /// 자세를 흘려보내기 시작한다 — 소유자가 래그돌에 진입할 때 부른다. <b>멱등</b>.
    ///
    /// 권위가 아니면 무동작이다 — 호출부가 권위를 따로 묻지 않아도 되게 여기서 삼킨다.
    /// (소유자 쪽 진입 경로는 전 피어에서 도는 폴링이라 분기를 저쪽에 두면 매 호출부에 번진다)
    /// </summary>
    public void BeginStreaming()
    {
        if (m_rig == null || !m_rig.IsValid)
            return;

        // ⚠ <b>이 한 줄은 원격에서도 세운다</b> — 진단이 "손실"과 "애초에 안 온다"를 가르려면
        // 원격이 <b>지금 자세를 기다리는 국면인지</b>를 알아야 한다. 한 개도 안 오면 손실률을
        // 말할 수 없다(분모가 0이다). 실제 송신은 아래 권위 게이트가 막는다.
        m_expectingStream = true;
        m_hasReceivedPose = false; // 새 국면 — 이번 무너짐의 첫 패킷을 다시 기다린다

        if (!IsPoseAuthority)
            return;

        m_streaming = true;
        m_stepsSinceSend = 0;
    }

    /// <summary>
    /// 스트림을 끊고 <b>마지막 자세를 로컬 좌표로</b> 한 번 보낸다 — 소유자가 정착시킬 때 부른다.
    /// <b>멱등</b>(보내는 중이 아니면 무동작).
    ///
    /// <b>여기서 좌표계가 바뀌는 것이 사양이다</b>(클래스 주석). 스트리밍 중에는 몸이 루트와 무관하게
    /// 월드에 놓이지만, 정착한 뒤에는 <b>루트가 몸을 끌어야</b> 한다 — 뼈가 키네마틱 자식이 되므로
    /// 루트를 옮기면 딸려 오고, 그래야 유치장 순간이동이 원격에서도 성립한다.
    ///
    /// <b>신뢰 전송이다.</b> 이 한 패킷이 원격의 <b>종착 상태</b>라 잃으면 그 시체는 마지막 스트림
    /// 자세로 영원히 남는다.
    /// </summary>
    public void EndStreaming()
    {
        if (!m_streaming)
            return;

        m_streaming = false;
        m_expectingStream = false;

        if (!IsSpawned || m_rig == null || !m_rig.IsValid)
            return;

        EnsureSendBuffer();
        if (m_rig.CaptureLocalPose(m_sendBuffer, out Vector3 hipsLocal))
            FinalPoseRpc(hipsLocal, Pack(m_sendBuffer));
    }

    /// <summary>
    /// 스트림을 <b>아무것도 보내지 않고</b> 끝낸다 — 몸이 일어날 때(기절 해제) 부른다. <b>멱등</b>.
    ///
    /// <see cref="EndStreaming"/>과 갈리는 지점이 여기다. 정착은 <b>종착 자세가 있는</b> 끝이라
    /// 마지막 패킷을 보내야 하지만, 기상은 <b>애니메이터가 몸을 도로 가져가는</b> 끝이라 보낼 자세가
    /// 없다 — 보내면 원격에서 기상 블렌드와 래그돌 자세가 같은 프레임을 두고 싸운다.
    ///
    /// <b>RPC가 필요 없다.</b> 기상 트리거(<c>NpcRagdoll.WantsRagdoll</c>)는 동기화된 상태에서 읽는
    /// 값이라 <b>전 피어가 각자 같은 답을 얻는다</b> — 원격도 자기 <c>ExitRagdoll</c>에서 이 함수를
    /// 부르므로 재생이 그 자리에서 멈춘다. 정착은 권위 피어만 판정하므로 그쪽만 RPC가 필요했던 것이다.
    /// </summary>
    public void StopStreaming()
    {
        m_streaming = false;
        m_expectingStream = false;

        // 원격 쪽 — 재생을 끊는다. 안 끊으면 애니메이터가 놓은 포즈를 매 프레임 덮어쓴다.
        m_streamDriven = false;
        m_snapshotCount = 0;
        m_haveSequence = false;
    }

    // 캡처는 <b>FixedUpdate</b>다 — 물리가 진실인 자리에서 떠야 스텝 사이 보간값이 섞이지 않는다.
    // (뼈는 래그돌 중 Interpolate라 Update에서 읽으면 물리가 만든 적 없는 중간 포즈가 나온다)
    private void FixedUpdate()
    {
        if (!m_streaming || !IsSpawned || !IsPoseAuthority)
            return;

        m_stepsSinceSend++;
        if (m_stepsSinceSend < m_sendEveryFixedSteps)
            return;

        m_stepsSinceSend = 0;
        SendSnapshot();
    }

    private void SendSnapshot()
    {
        if (m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return;

        EnsureSendBuffer();

        // 골반 로컬 위치는 버린다 — 스트리밍 중에는 <b>월드</b>를 보낸다(클래스 주석 §1-3).
        if (!m_rig.CaptureLocalPose(m_sendBuffer, out _))
            return;

        m_sequence = unchecked((ushort)(m_sequence + 1));
        StreamPoseRpc(m_sequence, m_rig.Hips.position, Pack(m_sendBuffer));

    }

    private void EnsureSendBuffer()
    {
        if (m_sendBuffer == null || m_sendBuffer.Length != m_rig.BoneCount)
            m_sendBuffer = new Quaternion[m_rig.BoneCount];

        if (m_packedBuffer == null || m_packedBuffer.Length != m_rig.BoneCount)
            m_packedBuffer = new uint[m_rig.BoneCount];
    }

    // ---- 압축 ----
    //
    // <b>쿼터니언 하나를 16B → 4B로 줄인다</b>(smallest-three). NGO가 자기
    // <c>NetworkTransform</c>에 쓰는 것과 <b>같은</b> 유틸리티라 직접 짜지 않았다.
    //
    // 오차는 성분당 10비트라 약 0.1°다 — 무너지는 시체에서 보이는 크기가 아니고,
    // 정착 자세도 같은 압축을 쓴다(둘을 가르면 마지막 스트림과 정착 사이에
    // 그 0.1°만큼 튀는 이음샐이 생긴다).
    private uint[] Pack(Quaternion[] rotations)
    {
        for (int i = 0; i < rotations.Length; i++)
        {
            Quaternion rotation = rotations[i];
            m_packedBuffer[i] = QuaternionCompressor.CompressQuaternion(ref rotation);
        }

        return m_packedBuffer;
    }

    // 푸는 자리 — 버퍼를 돌려주므로 호출부는 <b>바로 써야 한다</b>(다음 패킷이 덮어쓴다).
    private Quaternion[] Unpack(uint[] packed)
    {
        if (m_unpackBuffer == null || m_unpackBuffer.Length != packed.Length)
            m_unpackBuffer = new Quaternion[packed.Length];

        for (int i = 0; i < packed.Length; i++)
            QuaternionCompressor.DecompressQuaternion(ref m_unpackBuffer[i], packed[i]);

        return m_unpackBuffer;
    }




    // ---- 수신 (원격) ----

    /// <summary>
    /// 흘러오는 자세 — <b>언리라이어블</b>이라 유실·순서 뒤바뀜을 전제한다.
    ///
    /// 언리라이어블인 이유는 자세가 <b>누적되지 않는 값</b>이기 때문이다. 하나를 놓쳐도 다음 패킷이
    /// 완전한 상태를 들고 오므로 재전송은 늦은 정보를 늦게 배달할 뿐이다. 반대로 마지막
    /// 정착 자세(<see cref="FinalPoseRpc"/>)는 <b>뒤가 없어서</b> 신뢰 전송이어야 한다.
    ///
    /// ⚠ <b>압축은 아직 없다</b>(계획서 7단계). 지금은 쿼터니언 무압축 16B × 뼈 수라
    /// 25Hz에서 시체당 약 4.4KB/s다 — 압축(4B/개)을 넣으면 2.1KB/s로 떨어진다. 대역폭 판단은
    /// <b>압축 후 값으로</b> 할 것. 넣을 자리는 이 시그니처를 <c>INetworkSerializable</c> 구조체로
    /// 감싸는 것이고, 호출부는 바뀌지 않는다.
    /// </summary>
    [Rpc(SendTo.NotMe, Delivery = RpcDelivery.Unreliable)]
    private void StreamPoseRpc(ushort sequence, Vector3 hipsWorld, uint[] packed)
    {
        if (m_rig == null || !m_rig.IsValid || packed == null)
            return;

        if (packed.Length != m_rig.BoneCount)
        {
            // ⚠ <b>조용히 넘기지 않는다.</b> 자세가 안 오는 것과 화면상 증상이 같아지므로
            // 배선이 어긋난 것임을 말해 줘야 한다. 매 패킷 터지므로 <b>한 번만</b> 낸다.
            if (!m_warnedBoneMismatch)
            {
                m_warnedBoneMismatch = true;
                Debug.LogWarning(
                    $"RagdollPoseStreamer: 뼈 수가 달라 자세를 버린다 — {name} "
                        + $"받은={packed.Length} 내리그={m_rig.BoneCount}. 피어마다 리그가 다른 프리팩이다",
                    this
                );
            }

            return;
        }

        m_hasReceivedPose = true;

        // ⚠ 옛 패킷을 버린다. 언리라이어블은 순서를 보장하지 않으므로, 이 검사가 없으면 시체가
        // 이따금 한 스냅샷 뒤로 튄다.
        if (m_haveSequence && !IsNewer(sequence, m_newestSequence))
            return;

        m_newestSequence = sequence;
        m_haveSequence = true;
        m_streamDriven = true;

        PushSnapshot(hipsWorld, Unpack(packed));
    }

    /// <summary>
    /// 정착한 자세 — <b>스트림의 마지막 패킷</b>이고 골반이 <b>로컬</b>로 온다. 신뢰 전송.
    /// 받는 즉시 갈아끼우고 스트림 재생을 끝낸다 — 그때부터 몸은 루트 계층이 옮긴다.
    ///
    /// ⚠ <b>늦게 접속한 피어에는 오지 않는다</b>(신뢰 RPC의 성질). 이미 누워 있던 시체를 자세 없이
    /// 보게 되는 구멍이고, <b>지우기 전 <c>NpcDeath</c>의 1회 방송도 똑같이 뚫려 있었다</b> —
    /// 계획서 6단계에서 닫는다(권위 피어가 마지막 자세를 캐시했다가 새 접속자에게만 다시 쏜다).
    /// </summary>
    [Rpc(SendTo.NotMe)]
    private void FinalPoseRpc(Vector3 hipsLocal, uint[] packed)
    {
        if (m_rig == null || !m_rig.IsValid || packed == null)
            return;

        if (packed.Length != m_rig.BoneCount)
            return;

        // 재생 중이던 보간을 통째로 버린다 — 이 자세가 확정이라 섞을 것이 없다.
        //
        // ⚠ <c>m_expectingStream</c>을 내리는 것이 <b>이 자세를 지키는 일이다</b>: 소유자의
        // 빈 구간 메우기가 <see cref="IsAwaitingFirstPose"/>를 보므로, 여기서 안 내리면 그쪽이
        // 다시 켜져 진입 시점의 자세로 덮어쓴다.
        m_snapshotCount = 0;
        m_streamDriven = false;
        m_haveSequence = false;
        m_expectingStream = false;
        m_hasReceivedPose = true;

        m_rig.ApplyLocalPose(Unpack(packed), hipsLocal);

        // <b>자세를 입힌 뒤에 알린다</b> — 구독자가 이 자세를 얼리므로, 먼저 알리면 직전(스트림)
        // 자세가 굳는다.
        OnSettledPoseReceived?.Invoke();
    }

    private void PushSnapshot(Vector3 hipsWorld, Quaternion[] rotations)
    {
        EnsureSnapshotBuffers();

        if (m_snapshotCount == k_maxSnapshots)
        {
            // 가장 오래된 것을 밀어낸다 — 배열은 재사용하고 내용만 앞으로 당긴다.
            Snapshot oldest = m_snapshots[0];
            for (int i = 0; i < k_maxSnapshots - 1; i++)
                m_snapshots[i] = m_snapshots[i + 1];

            m_snapshots[k_maxSnapshots - 1] = oldest;
            m_snapshotCount = k_maxSnapshots - 1;
        }

        Snapshot slot = m_snapshots[m_snapshotCount];
        slot.Time = Time.time;
        slot.HipsWorld = hipsWorld;
        for (int i = 0; i < rotations.Length; i++)
            slot.Rotations[i] = rotations[i];

        m_snapshots[m_snapshotCount] = slot;
        m_snapshotCount++;
    }

    private void EnsureSnapshotBuffers()
    {
        int bones = m_rig.BoneCount;

        if (m_snapshots == null || m_snapshots.Length != k_maxSnapshots)
        {
            m_snapshots = new Snapshot[k_maxSnapshots];
            m_snapshotCount = 0;
        }

        for (int i = 0; i < m_snapshots.Length; i++)
        {
            if (m_snapshots[i].Rotations == null || m_snapshots[i].Rotations.Length != bones)
                m_snapshots[i].Rotations = new Quaternion[bones];
        }

        if (m_applyBuffer == null || m_applyBuffer.Length != bones)
            m_applyBuffer = new Quaternion[bones];
    }

    // 적용은 <b>Update</b>다 — 원격의 뼈는 물리에 참여하지 않으므로 물리 틱에 묶일 이유가 없고,
    // 렌더 주기에 맞춰 그려야 부드럽다.
    private void Update()
    {
        if (!m_streamDriven || IsPoseAuthority)
            return;

        TickApply();
    }



    // 재생 시점을 <see cref="m_interpolationDelay"/>만큼 뒤로 물려 두 스냅샷 사이를 섞는다.
    //
    // ⚠ <b>스냅샷 시각이 로컬 수신 시각이라 네트워크 지터가 그대로 재생에 실린다.</b> 더 정확한
    // 방법은 시퀀스 번호와 송신 주기로 시각을 <b>재구성</b>하거나(<c>시작시각 + (seq−시작seq) ×
    // 주기</c>) 서버 시각을 함께 싣는 것이다. 1단계에서는 넣지 않았다 — 지연 버퍼가 대부분을
    // 흡수하고, 실측에서 떨림이 남으면 그때 붙일 자리로 이 주석을 남긴다. (계획서 7단계)
    private void TickApply()
    {
        if (m_snapshotCount == 0)
            return;

        float renderTime = Time.time - m_interpolationDelay;

        // 재생 시점이 가장 오래된 스냅샷보다 앞이면(=버퍼가 아직 안 찼다) 그것을 그대로 쓴다.
        if (m_snapshotCount == 1 || renderTime <= m_snapshots[0].Time)
        {
            ApplySnapshot(m_snapshots[0]);
            return;
        }

        for (int i = 0; i < m_snapshotCount - 1; i++)
        {
            Snapshot from = m_snapshots[i];
            Snapshot to = m_snapshots[i + 1];

            if (renderTime > to.Time)
                continue;

            float span = to.Time - from.Time;
            float t = span > 0.0001f ? Mathf.Clamp01((renderTime - from.Time) / span) : 1f;
            ApplyBlend(from, to, t);
            return;
        }

        // 재생 시점이 가장 새 스냅샷보다 뒤다 — 패킷이 늦거나 끊겼다. <b>외삽하지 않고 붙든다.</b>
        // 시체가 잠깐 멈춰 보이는 편이 없는 데이터로 지어낸 자세보다 낫고, 정착 패킷이 곧 온다.
        ApplySnapshot(m_snapshots[m_snapshotCount - 1]);
    }

    private void ApplyBlend(Snapshot from, Snapshot to, float t)
    {
        for (int i = 0; i < m_applyBuffer.Length; i++)
            m_applyBuffer[i] = Quaternion.Slerp(from.Rotations[i], to.Rotations[i], t);

        ApplyPose(m_applyBuffer, Vector3.Lerp(from.HipsWorld, to.HipsWorld, t));
    }

    private void ApplySnapshot(Snapshot snapshot) => ApplyPose(snapshot.Rotations, snapshot.HipsWorld);

    // 회전은 리그가 입히고, 골반만 월드로 못박는다.
    //
    // <see cref="RagdollRig.ApplyLocalPose"/>에 <b>지금의 골반 로컬 위치를 그대로</b> 넘기는 것이
    // 이상해 보이지만 의도적이다 — 저 함수는 (회전 + 골반 로컬 위치) 한 쌍을 받는데 여기서 바꾸고
    // 싶은 것은 회전뿐이고, 골반은 바로 아래 줄에서 월드로 덮어쓴다. 리그에 월드 오버로드를 새로
    // 만들지 않은 것은 <b>리그를 안 건드린다</b>는 이 작업의 선 때문이다.
    private void ApplyPose(Quaternion[] rotations, Vector3 hipsWorld)
    {
        Transform hips = m_rig.Hips;
        if (hips == null)
            return;

        m_rig.ApplyLocalPose(rotations, hips.localPosition);
        hips.position = hipsWorld;
    }

    // ushort 랩어라운드를 견디는 "더 새것인가" 판정 — 차이를 부호 없는 반바퀴로 읽는다.
    private static bool IsNewer(ushort candidate, ushort current)
        => unchecked((ushort)(candidate - current)) is > 0 and < k_sequenceHalfRange;
}
