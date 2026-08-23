using UnityEngine;

/// <summary>
/// <see cref="PlayerRagdoll"/>의 <b>진단 계측 전부</b> — 본체에서 갈라낸 partial이다.
///
/// <b>여기 있는 것은 전부 임시다.</b> 원인이 잡히면 이 파일을 통째로 지운다 — 본체에 남는 것은
/// 호출 줄 몇 개뿐이다. 무엇을 재고 어떻게 읽는지는 <c>docs/player-ragdoll.md</c> §13에 있다.
///
/// #759 계측(<c>[밧줄]</c>·<c>[낙하속도]</c>·<c>[리그물리]</c>)은 원인이 닫혀 <b>재워 뒀다</b> —
/// 지우지 않고 주석 처리만 했으므로 <c>//</c>를 풀면 다시 돈다.
/// </summary>
public partial class PlayerRagdoll
{
    [Header("진단 (임시 — docs/player-ragdoll.md §13)")]
    [Tooltip("부활 순간의 yaw를 실측해 콘솔에 남긴다 — m_rootYawOffset을 맞추기 위한 계측이다. " +
             "읽는 법은 docs/player-ragdoll.md §13. 값이 확정되면 끈다")]
    [SerializeField] private bool m_logRevivalYaw;

    [Tooltip("정착 순간 <b>앞뒤 20프레임</b>을 한 줄씩 찍는다 — 정착할 때 몸이 아래로 내려갔다 " +
             "올라오는 현상을 잡는 계측이다. 읽는 법은 docs/player-ragdoll.md §13. 확정되면 끈다")]
    [SerializeField] private bool m_logSettleTrace;

    // 리그에서 뼈를 이름으로 찾는다 — 진단용(리지드바디가 없는 뼈도 집어야 한다).
    private Transform FindLiveBone(string boneName)
    {
        if (m_rig.BoneRoot == null)
            return null;

        Transform[] bones = m_rig.BoneRoot.GetComponentsInChildren<Transform>(true);
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i].name == boneName)
                return bones[i];
        }

        return null;
    }

    // ---- yaw 진단 (m_rootYawOffset 캘리브레이션) ----

    /// <summary>
    /// 부활 순간의 yaw를 실측해 남긴다 — <b>눈대중으로 오프셋을 맞추지 않기 위한 계측이다.</b>
    /// 재는 것은 시체 방향과 클립 방향의 차이이고, 읽는 법은 docs/player-ragdoll.md §13.
    /// </summary>
    private void LogRevivalYaw(
        bool haveCorpseYaw,
        float corpseYaw,
        bool haveLiveBefore,
        float liveBefore
    )
    {
        float rootYaw = m_root.eulerAngles.y;

        if (!haveCorpseYaw)
        {
            Debug.Log(
                $"[래그돌 부활 yaw] 시체가 거의 수직이라 누운 방향을 못 쟀다 — {name} "
                    + $"(루트 {rootYaw:F1}°). 다시 눕혀서 죽여 볼 것",
                this
            );
            return;
        }

        if (!TryLiveBodyYaw(out float clipYaw))
        {
            Debug.Log(
                $"[래그돌 부활 yaw] 살아있는 리그에서 Hips/Head를 못 찾아 클립 방향을 못 쟀다 — {name}",
                this
            );
            return;
        }

        // 애니메이터가 정말 뼈를 썼는가 — 안 썼으면 '클립'은 방금 입힌 시체 방향이고 오프셋도 무의미하다.
        bool animatorWrote =
            !haveLiveBefore || Mathf.Abs(Mathf.DeltaAngle(liveBefore, clipYaw)) > 0.05f;

        float needed = Mathf.DeltaAngle(clipYaw, rootYaw);
        Debug.Log(
            $"[래그돌 부활 yaw] 시체 {corpseYaw:F1}° / 클립 {clipYaw:F1}° / 루트 {rootYaw:F1}° "
                + $"→ m_rootYawOffset = {needed:F1}° (현재 {m_rootYawOffset:F1}°, "
                + $"어긋남 {Mathf.DeltaAngle(clipYaw, corpseYaw):F1}°) "
                + $"| 권한={HasMoveAuthority} 애니메이터기록={animatorWrote} "
                + $"컬링={m_animator.cullingMode} 상태={m_state}",
            this
        );
    }

    // 애니메이터가 놓은 자세의 몸 방향 — RagdollRig.TryGetBodyYaw와 같은 계산이되 뼈를 이름으로 찾는다.
    private bool TryLiveBodyYaw(out float yaw)
    {
        yaw = 0f;
        if (m_rig.BoneRoot == null)
            return false;

        Transform hips = FindLiveBone("Hips");
        Transform head = FindLiveBone("Head");
        if (hips == null || head == null)
            return false;

        Vector3 lengthwise = head.position - hips.position;
        lengthwise.y = 0f;
        if (lengthwise.sqrMagnitude < 0.0004f)
            return false;

        yaw = Quaternion.LookRotation(lengthwise.normalized).eulerAngles.y;
        return true;
    }

    // ---- 진입 자세 추적 (m_logEntryHeadTrace) — ⚠ 임시 계측, 원인이 잡히면 지운다 ----

    [Tooltip("래그돌 진입 직후 30프레임을 <b>루트 / 뼈 / 지면</b> 세 줄로 찍는다 — 쓰러지는 순간 몸이 " +
             "죽기 직전 자세에서 뜨고 돌아 버리는 현상을 잡는 계측이다.\n\n" +
             "⚠ <b>권한=True로 찍어야 한다</b> — 원격은 첫 패킷 전까지 자세가 못박혀 있어 아무것도 " +
             "움직이지 않는다.\n\n" +
             "각 칸을 읽는 법은 docs/player-ragdoll.md §13. 확정되면 끈다")]
    [SerializeField] private bool m_logEntryHeadTrace;

    // 쓰러져 바닥에 닿기까지를 담아야 "언제 뜨나"를 볼 수 있다 — 6프레임으로는 몸이 아직 서 있다.
    private const int k_entryTraceFrames = 30;

    // 볼 뼈 — <b>물리 뼈와 아닌 뼈를 섞어</b> 담는다. 루트가 움직일 때 리지드바디가 없는 뼈만
    // 계층을 따라가면 그 차이가 곧 비틀림이고, 섞어 두지 않으면 그것을 못 본다.
    private static readonly string[] s_entryTraceBones = { "Hips", "Spine_01", "Neck", "Head" };

    private const int k_entryTraceHeadIndex = 3; // 팝·단차를 재는 뼈 = s_entryTraceBones의 "Head"

    private PlayerHeadLook m_headLook; // 죽기 직전에 얹혀 있던 시선 기울기를 묻는다

    // ⚠ 리그가 한 벌이 된 뒤로 이 둘은 <b>같은 트랜스폼</b>을 가리킨다 — 골반간격은 항상 0이다 (docs §2).
    private Transform[] m_liveTraceBones;
    private Transform[] m_corpseTraceBones;

    private int m_entryTraceLeft;
    private int m_entryFrame;

    // 직전 프레임의 최종 자세 — 매 프레임 갱신하고, 진입 순간의 값을 기준선으로 얼린다.
    private float[] m_lastBoneY;
    private float[] m_lastBoneYaw;
    private Quaternion m_lastHeadRotation;
    private float m_lastRootY;
    private float m_lastRootYaw;
    private bool m_haveLast;

    // 진입 기준선 — 죽기 직전 프레임에 화면에 나온 몸. 뜸·yawΔ·팝은 전부 이것과의 차이다.
    private float[] m_baselineBoneY;
    private float[] m_baselineBoneYaw;
    private Quaternion m_baselineHeadRotation;
    private float m_baselineRootY;
    private float m_baselineRootYaw;
    private bool m_haveBaseline;

    private Quaternion m_prevCorpseHead;
    private bool m_havePrevCorpseHead;

    // 이름으로 뼈를 찾는다 — 깊이 우선.
    private static Transform FindBone(Transform root, string name)
    {
        if (root == null)
            return null;

        if (root.name == name)
            return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform found = FindBone(root.GetChild(i), name);
            if (found != null)
                return found;
        }

        return null;
    }

    // 볼 뼈와 버퍼를 한 번만 잡는다 — 멱등.
    private void ResolveTraceBones()
    {
        if (m_liveTraceBones != null)
            return;

        m_liveTraceBones = new Transform[s_entryTraceBones.Length];
        m_corpseTraceBones = new Transform[s_entryTraceBones.Length];
        m_lastBoneY = new float[s_entryTraceBones.Length];
        m_lastBoneYaw = new float[s_entryTraceBones.Length];
        m_baselineBoneY = new float[s_entryTraceBones.Length];
        m_baselineBoneYaw = new float[s_entryTraceBones.Length];

        for (int i = 0; i < s_entryTraceBones.Length; i++)
        {
            m_liveTraceBones[i] = FindBone(m_rig.BoneRoot, s_entryTraceBones[i]);
            m_corpseTraceBones[i] = FindBone(m_rig.BoneRoot, s_entryTraceBones[i]);
        }
    }

    // 직전 프레임의 자세를 담아 둔다 — <see cref="Update"/> 시작에서만 정직한 값이다 (docs §13).
    private void SampleEntryBaseline()
    {
        if (!m_logEntryHeadTrace)
            return;

        ResolveTraceBones();

        for (int i = 0; i < m_liveTraceBones.Length; i++)
        {
            Transform bone = m_liveTraceBones[i];
            if (bone == null)
                continue;

            m_lastBoneY[i] = bone.position.y;
            m_lastBoneYaw[i] = bone.eulerAngles.y;
        }

        Transform head = m_liveTraceBones[k_entryTraceHeadIndex];
        if (head != null)
            m_lastHeadRotation = head.rotation;

        m_lastRootY = m_root.position.y;
        m_lastRootYaw = m_root.eulerAngles.y;
        m_haveLast = true;
    }

    // 물리에 넘기는 프레임에 연다 — 그 <b>직후</b>의 값이 첫 줄이어야 진입이 바꾼 것이 갈린다.
    // ⚠ 기준선은 <b>직전 프레임</b>의 값이다 — 지금 몸을 읽으면 차이가 정의상 0이 된다.
    private void BeginEntryTrace()
    {
        if (!m_logEntryHeadTrace)
            return;

        ResolveTraceBones();
        m_headLook ??= GetComponentInParent<PlayerHeadLook>();

        m_entryFrame = Time.frameCount;
        m_entryTraceLeft = k_entryTraceFrames;
        m_havePrevCorpseHead = false;

        m_haveBaseline = m_haveLast;
        System.Array.Copy(m_lastBoneY, m_baselineBoneY, m_lastBoneY.Length);
        System.Array.Copy(m_lastBoneYaw, m_baselineBoneYaw, m_lastBoneYaw.Length);
        m_baselineHeadRotation = m_lastHeadRotation;
        m_baselineRootY = m_lastRootY;
        m_baselineRootYaw = m_lastRootYaw;

        float tilt = m_headLook != null ? m_headLook.LastAppliedTilt : float.NaN;

        Debug.Log(
            $"[진입추적] ===== 시체 켬 (권한={HasMoveAuthority} 프레임={m_entryFrame}) — 시선={tilt:F1}° "
                + $"기준선={m_haveBaseline} 기준루트Y={m_baselineRootY:F3} "
                + $"기준루트yaw={m_baselineRootYaw:F1}° / 복사직후 ↓ =====",
            this
        );

        LogEntrySample();
    }

    private void TickEntryTrace()
    {
        if (m_entryTraceLeft <= 0)
            return;

        m_entryTraceLeft--;
        LogEntrySample();
    }

    // 한 프레임을 세 줄로 찍는다 — 한 줄에 다 넣으면 MPPM 로그에서 잘린다. 읽는 법은 docs §13.
    private void LogEntrySample()
    {
        if (m_corpseTraceBones == null)
            return;

        string frame = (Time.frameCount - m_entryFrame).ToString("+0;-0;0");

        // ---- 루트 ----
        float rootLift = m_haveBaseline ? m_root.position.y - m_baselineRootY : float.NaN;
        float rootYawDelta = m_haveBaseline
            ? Mathf.DeltaAngle(m_baselineRootYaw, m_root.eulerAngles.y)
            : float.NaN;

        // TryGetBodyYaw가 무엇을 보고 판단했는지 같이 남긴다 — 수평 성분의 크기가 곧 그 함수의
        // 거절 가드가 옳게 걸렸는지의 근거다.
        float bodyYaw = float.NaN;
        bool haveBodyYaw = false;
        if (m_rig != null && m_rig.TryGetBodyYaw(out float measuredYaw))
        {
            haveBodyYaw = true;
            bodyYaw = measuredYaw;
        }

        Transform corpseHips = m_corpseTraceBones[0];
        Transform corpseHead = m_corpseTraceBones[k_entryTraceHeadIndex];
        float horizontalCm = float.NaN;
        if (corpseHips != null && corpseHead != null)
        {
            Vector3 lengthwise = corpseHead.position - corpseHips.position;
            lengthwise.y = 0f;
            horizontalCm = lengthwise.magnitude * 100f;
        }

        Debug.Log(
            $"[진입루트] 권한={HasMoveAuthority} f={frame} 루트뜸={rootLift:+0.000;-0.000;0.000}m "
                + $"루트yawΔ={rootYawDelta:+0.0;-0.0;0.0}° 몸yaw={bodyYaw:F1}° 수평={horizontalCm:F1}cm "
                + $"유효={haveBodyYaw} 상태={m_state}",
            this
        );

        // ---- 뼈 ----
        var bones = new System.Text.StringBuilder();
        for (int i = 0; i < m_corpseTraceBones.Length; i++)
        {
            Transform bone = m_corpseTraceBones[i];
            if (bone == null)
                continue;

            float lift = m_haveBaseline ? bone.position.y - m_baselineBoneY[i] : float.NaN;
            float yawDelta = m_haveBaseline
                ? Mathf.DeltaAngle(m_baselineBoneYaw[i], bone.eulerAngles.y)
                : float.NaN;

            bones.Append(
                $"{s_entryTraceBones[i]}={lift:+0.000;-0.000;0.000}m/{yawDelta:+0.0;-0.0;0.0}° "
            );
        }

        // ⚠ 골반간격은 리그가 한 벌이 된 뒤로 항상 0이다 — 근거로 쓰지 말 것 (docs §2).
        Transform liveHips = m_liveTraceBones[0];
        float rigSpan =
            liveHips != null && corpseHips != null
                ? (liveHips.position - corpseHips.position).magnitude
                : float.NaN;

        float pop =
            m_haveBaseline && corpseHead != null
                ? Quaternion.Angle(m_baselineHeadRotation, corpseHead.rotation)
                : float.NaN;

        float step = 0f;
        if (corpseHead != null)
        {
            step = m_havePrevCorpseHead
                ? Quaternion.Angle(m_prevCorpseHead, corpseHead.rotation)
                : 0f;
            m_prevCorpseHead = corpseHead.rotation;
            m_havePrevCorpseHead = true;
        }

        Debug.Log(
            $"[진입뼈] 권한={HasMoveAuthority} f={frame} {bones}골반간격={rigSpan:F3}m "
                + $"팝={pop:F1}° 단차={step:F1}°",
            this
        );

        // ---- 지면 ----
        // <b>"떠 있다"를 직접 재는 줄이다</b> — 위 두 줄은 죽기 직전 자세 기준의 상대값이라 몸 전체가
        // 떠 있어도 0으로 보인다. 지면 탐색은 정착 판정·정착 정렬과 <b>같은 것</b>을 쓴다.
        float groundY = float.NaN;
        bool haveGround = false;
        if (corpseHips != null && TryGroundUnder(corpseHips.position, out Vector3 groundPoint))
        {
            haveGround = true;
            groundY = groundPoint.y;
        }

        float lowestAbove = haveGround && m_rig != null ? m_rig.LowestBoneY - groundY : float.NaN;
        float hipsAbove = haveGround && corpseHips != null
            ? corpseHips.position.y - groundY
            : float.NaN;
        float rootAbove = haveGround ? m_root.position.y - groundY : float.NaN;

        Debug.Log(
            $"[진입지면] 권한={HasMoveAuthority} f={frame} 지면Y={groundY:F3} 찾음={haveGround} "
                + $"최저뼈-지면={lowestAbove:+0.000;-0.000;0.000}m "
                + $"골반-지면={hipsAbove:F3}m 루트-지면={rootAbove:+0.000;-0.000;0.000}m",
            this
        );
    }

    // ⚠ #759 계측 — 원인이 닫혀 주석 처리했다(2026-08-20). 근거: docs/759-ragdoll-slowmotion-handoff.md
    //    [밧줄]·[밧줄추적]·[리그물리]·[낙하속도] 본체. 호출부 일곱 곳도 같이 막혀 있다.
    /*
    // ---- 밧줄 견인 계측 (m_logRopePull) — ⚠ 임시 계측, #759가 닫히면 지운다 ----

    [Tooltip("밧줄로 끌기 시작한 순간과 그 1.5초 뒤를 두 줄로 찍는다 — <b>끌리는 시체가 원격에서 " +
             "제자리에 남는</b> 증상의 판정용이다.\n\n" +
             "<b>가르는 것은 골반이동 대 루트이동이다.</b> 루트만 움직이고 골반이 0에 가까우면 " +
             "<b>권위 피어에서 몸 자체가 안 끌린 것</b>이다 — 원격은 마지막 자세를 월드로 붙들고 " +
             "있을 뿐이라 전송은 결백하다. 골반이 제대로 움직였는데도 원격이 제자리면 그때가 " +
             "전송 문제다.\n\n" +
             "함께 찍는 <b>키네마틱·잠듦</b>이 원인을 바로 지목한다 — 키네마틱 뼈에는 밧줄 관절이 " +
             "힘을 만들지 못하고, 잠든 뼈는 장력을 받지 못한다(RagdollRope.Tick).\n\n" +
             "확정되면 끈다")]
    [SerializeField] private bool m_logRopePull;

    private const float k_ropeTraceSeconds = 1.5f;

    private bool m_ropeTraceActive;
    private float m_ropeTraceStart;
    private static readonly float[] k_ropeMarks = { 0.5f, 1f, 1.5f };

    private readonly float[] m_ropeMoved = new float[3];
    private int m_ropeMarkIndex;
    private float m_ropePeakSpeed;
    private Vector3 m_ropePrevHips;

    private Vector3 m_ropeTraceHipsStart;
    private Vector3 m_ropeTraceRootStart;

    // 끌기 시작 — 이 순간의 물리 상태가 곧 "왜 안 끌리는가"의 답이다.
    private void BeginRopeTrace()
    {
        m_ropeTraceActive = m_logRopePull;
        if (!m_ropeTraceActive)
            return;

        m_ropeTraceStart = Time.unscaledTime;
        m_ropeTraceHipsStart = m_rig.Hips != null ? m_rig.Hips.position : Vector3.zero;
        m_ropeTraceRootStart = m_root.position;
        m_ropePrevHips = m_ropeTraceHipsStart;
        m_ropeMarkIndex = 0;
        m_ropePeakSpeed = 0f;
        for (int i = 0; i < m_ropeMoved.Length; i++)
            m_ropeMoved[i] = float.NaN;

        Rigidbody hips = m_rig.HipsBody;
        Debug.Log(
            $"[밧줄] {TraceId} 권한={HasMoveAuthority} 상태={m_state} 밧줄길이={RopeLength:F2}m "
                + $"골반키네마틱={(hips != null ? hips.isKinematic.ToString() : "없음")} "
                + $"잠듦={(hips != null ? hips.IsSleeping().ToString() : "없음")} "
                + $"평균속도={m_rig.AverageSpeed:F2}m/s",
            this
        );
    }

    private void TickRopeTrace()
    {
        if (!m_ropeTraceActive)
            return;

        // 끌려온 거리의 <b>모양</b>과 최고 속도 — 총량만 보면 "느리게 끌렸다"를 놓친다.
        Vector3 hipsNow = m_rig.Hips != null ? m_rig.Hips.position : m_ropePrevHips;
        if (Time.unscaledDeltaTime > 0.0001f)
        {
            m_ropePeakSpeed = Mathf.Max(
                m_ropePeakSpeed,
                Vector3.Distance(hipsNow, m_ropePrevHips) / Time.unscaledDeltaTime
            );
        }

        m_ropePrevHips = hipsNow;

        float ropeElapsed = Time.unscaledTime - m_ropeTraceStart;
        while (m_ropeMarkIndex < k_ropeMarks.Length && ropeElapsed >= k_ropeMarks[m_ropeMarkIndex])
        {
            m_ropeMoved[m_ropeMarkIndex] = Vector3.Distance(hipsNow, m_ropeTraceHipsStart);
            m_ropeMarkIndex++;
        }

        if (ropeElapsed < k_ropeTraceSeconds)
            return;

        m_ropeTraceActive = false;

        Rigidbody hips = m_rig.HipsBody;
        float hipsMoved = m_rig.Hips != null
            ? Vector3.Distance(m_rig.Hips.position, m_ropeTraceHipsStart)
            : float.NaN;

        Debug.Log(
            $"[밧줄추적] {TraceId} 권한={HasMoveAuthority} t={k_ropeTraceSeconds:F1}s "
                + $"골반이동={hipsMoved:F2}m "
                + $"루트이동={Vector3.Distance(m_root.position, m_ropeTraceRootStart):F2}m "
                + $"평균속도={m_rig.AverageSpeed:F2}m/s "
                + $"골반키네마틱={(hips != null ? hips.isKinematic.ToString() : "없음")} "
                + $"잠듦={(hips != null ? hips.IsSleeping().ToString() : "없음")} "
                + $"최고속도={m_ropePeakSpeed:F2}m/s 견인곡선=[{DescribeRopeCurve()}] "
                + $"관절={(m_rope != null && m_rope.IsAttached ? "걸림" : "없음")} "
                + $"끌림={(m_rope != null && m_rope.IsBeingCarried ? "예" : "아니오")} "
                + $"골반바디={(m_rig.HipsBody != null ? m_rig.HipsBody.name : "없음")}",
            this
        );
    }

    private string DescribeRopeCurve()
    {
        System.Text.StringBuilder line = new System.Text.StringBuilder();
        for (int i = 0; i < m_ropeMarkIndex && i < k_ropeMarks.Length; i++)
        {
            if (line.Length > 0)
                line.Append(' ');
            line.Append($"{k_ropeMarks[i]:0.##}s:{m_ropeMoved[i]:F2}m");
        }

        return line.Length > 0 ? line.ToString() : "표본없음";
    }

    // ---- 낙하 속도 계측 (m_logFallRate) — ⚠ 임시 계측, #759가 닫히면 지운다 ----

    [Tooltip("쓰러지는 동안 <b>물리가 실시간을 따라갔는가</b>를 한 줄로 찍는다 — #759 ③(권위 피어의 " +
             "시뮬레이션이 느리다) 판정용이다.\n\n" +
             "<b>비율 = 물리시간 ÷ 실시간.</b> 1.00이 정상이고 <b>0.60이면 40% 느리게 쓰러진 것</b>이다 " +
             "— 슬로모션의 정의가 이 값이다. 물리시간은 Time.fixedTime으로 잰다(실행된 고정 스텝만 " +
             "누적되는 시계라 스텝을 따로 셀 필요가 없다).\n\n" +
             "<b>최장프레임이 333ms를 넘으면</b> Maximum Allowed Timestep 클램프에 걸린 것이라 원인이 " +
             "<b>프레임 히칭으로 확정</b>된다. 비율은 낮은데 최장프레임이 그보다 작으면 클램프가 " +
             "아니므로 겹침 해소(RagdollRig의 최대 침투 해소 속도)를 다음으로 의심한다.\n\n" +
             "<b>권한 피어에서 읽는다</b> — 원격은 뼈가 키네마틱이라 잴 물리가 없다. 그래도 함께 " +
             "찍는 이유는 두 피어의 프레임 사정을 같은 화면에서 비교하기 위해서다.\n\n" +
             "확정되면 끈다")]
    [SerializeField] private bool m_logFallRate;

    // 무너짐 한 번을 덮기에 넉넉한 창 — 이 안에 정착하면 그쪽이 먼저 마감한다.
    private const float k_fallRateWindowSeconds = 3f;


    // 낙하 곡선 표본 시각(초) — 정상 낙하는 0.5s 안에 거의 끝난다. 느린 판은 이 곡선이 기어간다.
    private static readonly float[] k_fallMarks = { 0.25f, 0.5f, 1f, 2f, 3f };

    private readonly float[] m_fallDrops = new float[5];
    private int m_fallMarkIndex;
    private float m_fallPeakDescent;
    private float m_fallPrevHipsY;

    // #759 A/B — 같은 시각에 나란히 뽑는 두 곡선. 둘의 <b>조합</b>이 A와 B를 가른다 (docs/759 §6):
    //  · A(관절 앵커가 어긋난 채 구워짐) → 관절오차 크게 출발, 뼈속도 높음(떤다), 골반은 안 나감
    //  · B(speculative 접촉이 제동)      → 관절오차 작음,        뼈속도 낮음(눌린다), 골반도 안 나감
    private readonly float[] m_fallJointErrors = new float[5];
    private readonly float[] m_fallBoneSpeeds = new float[5];
    private float m_fallEntryJointError; // 붕괴 첫 프레임의 앵커 오차 — 사망마다 흔들리면 A다

    private bool m_fallRateActive;
    private float m_fallRateRealStart;
    private float m_fallRatePhysicsStart;
    private float m_fallRateHipsStartY;
    private int m_fallRateFrames;
    private float m_fallRateWorstFrame;

    // 시체를 켜는 순간 = 낙하의 시작. 두 시계를 나란히 찍어 두고 그 뒤로 벌어지는 차를 본다.
    private void BeginFallRateTrace()
    {
        m_fallRateActive = m_logFallRate;
        if (!m_fallRateActive)
            return;

        m_fallRateRealStart = Time.unscaledTime;
        m_fallRatePhysicsStart = Time.fixedTime;
        m_fallRateHipsStartY = m_rig.Hips != null ? m_rig.Hips.position.y : float.NaN;
        m_fallRateFrames = 0;
        m_fallRateWorstFrame = 0f;

        m_fallMarkIndex = 0;
        m_fallPeakDescent = 0f;
        m_fallPrevHipsY = m_fallRateHipsStartY;
        for (int i = 0; i < m_fallDrops.Length; i++)
        {
            m_fallDrops[i] = float.NaN;
            m_fallJointErrors[i] = float.NaN;
            m_fallBoneSpeeds[i] = float.NaN;
        }

        m_fallEntryJointError = m_rig.MaxJointAnchorError(out _);

        LogRigPhysics();
    }

    // 진입 시점의 물리 설정 1회 — "시계는 정상인데 몸이 느리다"의 원인은 거의 여기 있다.
    private void LogRigPhysics()
    {
        Rigidbody hips = m_rig.HipsBody;
        if (hips == null)
            return;

        // #759 A/B — 아래 넷이 이번 판의 가설을 가른다 (docs/759 §5·§6).
        //  · 충돌검출: 프리팹은 ContinuousSpeculative다. Discrete로 덮고 낙하곡선 0.25s 칸을 본다 → B
        //  · 진입관절오차: 붕괴 첫 프레임에 솔버가 이미 지고 있는 위치 오차. 사망마다 흔들리면 → A
        float entryError = m_rig.MaxJointAnchorError(out string worstJoint);

        Debug.Log(
            $"[리그물리] {TraceId} 뼈={m_rig.BoneCount} 골반질량={hips.mass:F1}kg "
                + $"선형감쇠={hips.linearDamping:F2} 각감쇠={hips.angularDamping:F2} "
                + $"중력={hips.useGravity} 침투해소상한={hips.maxDepenetrationVelocity:F2}m/s "
                + $"솔버={hips.solverIterations}/{hips.solverVelocityIterations} "
                + $"중력크기={Physics.gravity.magnitude:F2}m/s² "
                + $"충돌검출={hips.collisionDetectionMode} "
                + $"관절={m_rig.JointCount}개 자동앵커={m_rig.HasAutoConfiguredAnchors} "
                + $"진입관절오차={entryError * 1000f:F1}mm(최악={(string.IsNullOrEmpty(worstJoint) ? "없음" : worstJoint)})",
            this
        );
    }

    private void TickFallRate()
    {
        if (!m_fallRateActive)
            return;

        m_fallRateFrames++;
        m_fallRateWorstFrame = Mathf.Max(m_fallRateWorstFrame, Time.unscaledDeltaTime);

        // 골반이 내려간 속도와 곡선 — 낙차 총량은 포화되므로 <b>모양</b>을 봐야 느림이 보인다.
        float hipsY = m_rig.Hips != null ? m_rig.Hips.position.y : m_fallPrevHipsY;
        if (Time.unscaledDeltaTime > 0.0001f)
        {
            m_fallPeakDescent = Mathf.Max(
                m_fallPeakDescent,
                (m_fallPrevHipsY - hipsY) / Time.unscaledDeltaTime
            );
        }

        m_fallPrevHipsY = hipsY;

        float elapsed = Time.unscaledTime - m_fallRateRealStart;
        while (m_fallMarkIndex < k_fallMarks.Length && elapsed >= k_fallMarks[m_fallMarkIndex])
        {
            m_fallDrops[m_fallMarkIndex] = m_fallRateHipsStartY - hipsY;

            // 같은 시각의 관절 오차와 뼈 속도 — 낙하곡선과 나란히 놓아야 A와 B가 갈린다.
            m_fallJointErrors[m_fallMarkIndex] = m_rig.MaxJointAnchorError(out _);
            m_fallBoneSpeeds[m_fallMarkIndex] = m_rig.AverageSpeed;

            m_fallMarkIndex++;
        }

        if (Time.unscaledTime - m_fallRateRealStart >= k_fallRateWindowSeconds)
            DumpFallRate("창종료");
    }

    // 한 줄로 마감한다 — 창이 닫히거나 정착한 시점, 둘 중 먼저 오는 쪽이 부른다.
    private void DumpFallRate(string reason)
    {
        if (!m_fallRateActive)
            return;

        m_fallRateActive = false;

        float real = Time.unscaledTime - m_fallRateRealStart;
        float physics = Time.fixedTime - m_fallRatePhysicsStart;
        float ratio = real > 0.0001f ? physics / real : float.NaN;
        float fps = real > 0.0001f ? m_fallRateFrames / real : float.NaN;
        float drop = m_rig.Hips != null ? m_fallRateHipsStartY - m_rig.Hips.position.y : float.NaN;
        // 경계는 상수로 박지 않고 런타임 값을 읽는다 — ProjectSettings가 바뀌면 판정도 따라간다.
        string verdict = m_fallRateWorstFrame > Time.maximumDeltaTime
            ? "⚠ 클램프에 걸린 프레임 있음"
            : "클램프 없음";

        Debug.Log(
            $"[낙하속도] {TraceId} 권한={HasMoveAuthority} 종료={reason} 실시간={real:F2}s 물리={physics:F2}s "
                + $"비율={ratio:F2}(1.00이 정상) 프레임={m_fallRateFrames} 평균={fps:F1}fps "
                + $"최장프레임={m_fallRateWorstFrame * 1000f:F0}ms({verdict}) 골반낙차={drop:F2}m "
                + $"최고하강={m_fallPeakDescent:F2}m/s 낙하곡선=[{DescribeFallCurve()}] "
                + $"진입관절오차={m_fallEntryJointError * 1000f:F1}mm "
                + $"관절오차곡선=[{DescribeMarkCurve(m_fallJointErrors, 1000f, "mm")}] "
                + $"뼈속도곡선=[{DescribeMarkCurve(m_fallBoneSpeeds, 1f, "m/s")}]",
            this
        );
    }

    // 표본이 찍힌 구간만 적는다 — 일찍 끝난 국면에서 빈 칸이 0으로 보이면 오독한다.
    private string DescribeFallCurve() => DescribeMarkCurve(m_fallDrops, 1f, "m");

    // 낙하 표본 시각(k_fallMarks)에 나란히 찍힌 값 하나를 곡선으로 편다. (#759 A/B 계측)
    private string DescribeMarkCurve(float[] samples, float scale, string unit)
    {
        System.Text.StringBuilder line = new System.Text.StringBuilder();
        for (int i = 0; i < m_fallMarkIndex && i < k_fallMarks.Length; i++)
        {
            if (line.Length > 0)
                line.Append(' ');
            line.Append($"{k_fallMarks[i]:0.##}s:{samples[i] * scale:F2}{unit}");
        }

        return line.Length > 0 ? line.ToString() : "표본없음";
    }

    */
    // ---- 정착 딥 추적 (m_logSettleTrace) — ⚠ 임시 계측, 원인이 잡히면 지운다 ----

    private const int k_settleTraceFrames = 20;

    private struct SettleTraceSample
    {
        public int Frame;
        public float RootY;
        public float HipsWorldY;
        public float HipsLocalY;
        public bool Driven;
        public RagdollState State;
    }

    private SettleTraceSample[] m_trace;
    private int m_traceHead;
    private int m_traceFilled;
    private int m_traceAfter; // 정착 뒤로 더 찍을 프레임 수 (0이면 안 찍는 중)
    private int m_traceSettleFrame;

    // 매 프레임 담아만 둔다 — 찍는 것은 정착하는 순간이다. 딥이 한순간이라 사후 관측이 불가능하다.
    private void TickSettleTrace()
    {
        if (!m_logSettleTrace || m_rig == null || !m_rig.IsValid || m_rig.Hips == null)
            return;

        if (m_state == RagdollState.Animated)
            return;

        if (m_trace == null || m_trace.Length != k_settleTraceFrames)
        {
            m_trace = new SettleTraceSample[k_settleTraceFrames];
            m_traceHead = 0;
            m_traceFilled = 0;
        }

        SettleTraceSample sample = new SettleTraceSample
        {
            Frame = Time.frameCount,
            RootY = m_root != null ? m_root.position.y : float.NaN,
            HipsWorldY = m_rig.Hips.position.y,
            HipsLocalY = m_rig.Hips.localPosition.y,
            Driven = m_streamer != null && m_streamer.IsStreamDriven,
            State = m_state,
        };

        m_trace[m_traceHead] = sample;
        m_traceHead = (m_traceHead + 1) % k_settleTraceFrames;
        if (m_traceFilled < k_settleTraceFrames)
            m_traceFilled++;

        // 정착 이후 구간 — 실시간으로 이어 찍는다.
        if (m_traceAfter > 0)
        {
            m_traceAfter--;
            LogTraceSample(sample);
        }
    }

    // 정착하는 순간 링버퍼를 쏟고, 이후 구간을 이어 찍도록 예약한다. 양쪽 피어가 같은 함수를 쓴다.
    private void DumpSettleTrace()
    {
        if (!m_logSettleTrace)
            return;

        m_traceSettleFrame = Time.frameCount;
        Debug.Log(
            $"[정착추적] ===== 정착 (권한={HasMoveAuthority} 프레임={m_traceSettleFrame}) — "
                + $"이전 {m_traceFilled}프레임 ↓ =====",
            this
        );

        int start = (m_traceHead - m_traceFilled + k_settleTraceFrames) % k_settleTraceFrames;
        for (int i = 0; i < m_traceFilled; i++)
            LogTraceSample(m_trace[(start + i) % k_settleTraceFrames]);

        m_traceAfter = k_settleTraceFrames;
    }

    // 한 샘플이 한 줄이다 — 여러 줄로 쓰면 MPPM 로그에서 잘린다.
    private void LogTraceSample(SettleTraceSample sample)
    {
        Debug.Log(
            $"[정착추적] 권한={HasMoveAuthority} f={sample.Frame - m_traceSettleFrame:+0;-0;0} "
                + $"루트Y={sample.RootY:F3} 골반월드Y={sample.HipsWorldY:F3} "
                + $"골반로컬Y={sample.HipsLocalY:F3} 스트림={sample.Driven} 상태={sample.State}",
            this
        );
    }
}