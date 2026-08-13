#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// ⚠ <b>임시 계측이다 — 지울 것.</b> `feature/ragdoll-hotfix` 전용이고, 검증
/// (<c>docs/ragdoll-corpse-jail-teleport.md</c> §6)이 끝나면 <b>이 파일 하나를 통째로 지우면</b> 흔적이
/// 남지 않는다. 그래서 프로덕션 코드는 한 줄도 건드리지 않는다: 프리팹에 붙이지 않고
/// <see cref="Bootstrap"/>이 씬 로드 뒤 스스로 하나 만들며, 읽는 값은 전부 공개 API다.
///
/// <b>무엇을 재는가</b> — 계획서 §6-2·3의 두 기준을 <b>수정 전에</b> 한 번 찍어 기준선을 만든다.
/// 그리고 §7의 세 가설(터널링 / 관절 장력 / 바닥 아래 정착) 중 어느 것인지를 가른다:
/// <list type="bullet">
///   <item><c>여유 = 최저뼈y − 바닥y</c> — §6-2 (기준 ≥ 0)</item>
///   <item><c>골반로컬y</c> — §6-3 (#572 실측 0.234 → −0.001이 실패 신호)</item>
///   <item><c>최대v</c> — 순간이동 구간의 뼈 최고 속도. 터널링 가설(a)의 판별점이다</item>
///   <item><c>얼림 · 골반kine</c> — 피어별 구성이 실제로 갈리는지의 확인 (§3-2)</item>
/// </list>
///
/// <b>언제 찍는가</b> — 루트가 한 프레임에 <see cref="k_teleportDeltaMeters"/> 이상 움직이면 순간이동
/// 으로 보고 캡처를 연다. 이 임계가 <b>끌기와 순간이동을 가른다</b>: 끌리는 시체는 #572 실측 최고
/// 7.73m/s라 60fps에서 0.13m/프레임인 반면, 감옥까지 수십 m를 100ms에 쓸고 가는 골반 보간은
/// 프레임당 수 m다. 놓치면 <b>F9</b>로 직접 연다.
///
/// <b>어디에 남는가</b> — 콘솔 <b>한 줄</b>(MCP <c>read_console</c>이 첫 줄만 가져오므로)과
/// <c>&lt;프로세스 프로젝트 루트&gt;/Logs/corpse-diag-&lt;역할&gt;.log</c> 둘 다. 파일까지 쓰는 이유는
/// <b>정작 필요한 쪽이 클라이언트</b>인데 MPPM 가상 플레이어 콘솔은 도구로 못 읽기 때문이다 —
/// 파일이면 경로만 알면 읽힌다. 캡처를 열 때 그 절대 경로를 콘솔에 한 번 찍는다.
/// <c>Logs/</c>는 이미 <c>.gitignore</c> 대상이라 저장소를 더럽히지 않는다.
/// </summary>
public class CorpseTeleportDiag : MonoBehaviour
{
    // 순간이동으로 볼 한 프레임 루트 이동(m) — 클래스 주석의 "끌기와 가르는 임계".
    private const float k_teleportDeltaMeters = 0.5f;

    // 트리거 <b>이전</b> 프레임도 남긴다 — 쓸려 가기 직전의 정상 자세가 비교 기준이다.
    private const int k_preRollFrames = 15;

    // 트리거 이후 찍는 프레임 수 — 60fps에서 1.5초. 이동(~0.1초)과 그 뒤 정착까지 덮는다.
    private const int k_captureFrames = 90;

    private const float k_rescanSeconds = 0.5f;

    // 이만큼(m) 바닥 아래로 내려가면 "가라앉았다"로 보고 캡처를 연다. 정착한 시체의 정상 여유가
    // +0.05~+0.15라, 음수 5cm면 접촉으로 설명되지 않는다.
    private const float k_sunkMeters = -0.05f;

    // 지형 마스크 — NpcRagdoll.m_groundMask의 프리팹 값(Default)과 같게 둔다. 래그돌 뼈는 다른
    // 레이어라 걸리지 않는다.
    private const int k_groundMask = 1;

    // 골반 밑 지면 탐색 — NpcRagdoll.TryGroundUnder와 같은 수치라야 프로덕션이 보는 바닥과 같은 값이 나온다.
    private const float k_probeLift = 0.5f;
    private const float k_probeDistance = 1.5f;

    // 머리 위 탐색 거리(m) — 바닥 아래로 꺼졌으면 위쪽에서 바닥이 잡힌다.
    // ⚠ MeshCollider 뒷면은 기본 설정에서 레이가 통과하므로 <b>안 잡혀도 무죄다.</b> 잡히면 확증이고,
    // 못 잡으면 판단은 여유(최저뼈y − 바닥y) 쪽으로 미룬다.
    private const float k_ceilingProbeDistance = 3f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        var go = new GameObject("[시체진단]") { hideFlags = HideFlags.DontSave };
        DontDestroyOnLoad(go);
        go.AddComponent<CorpseTeleportDiag>();
    }

    private sealed class Corpse
    {
        public NpcRagdoll Ragdoll;
        public NpcController Npc;
        public RagdollRig Rig;
        public RagdollRope Rope;
        public Rigidbody[] Bones;

        public Vector3 LastRootPos;
        public bool HasLastRootPos;

        public int FramesLeft; // > 0 이면 캡처 중
        public int SampleIndex;
        public readonly Queue<string> PreRoll = new();

        // BuildPayload가 채운다 — 트리거 판정이 같은 프레임의 값을 보게 하려는 것이다.
        public float Clearance = float.NaN;
        public bool Jointed;
        public bool PrevJointed;
        public bool HasJointState;
    }

    private readonly Dictionary<int, Corpse> m_tracked = new();
    private float m_rescanTimer;

    // 캡처를 열 때 한 번만 경로를 알린다 — 매번 찍으면 정작 볼 줄이 밀린다.
    private bool m_announcedPath;

    private void LateUpdate()
    {
        // LateUpdate인 이유는 NpcRagdoll.LateUpdate와 같다 — NetworkTransform이 이번 프레임에 적용한
        // 루트·골반을 읽어야 한 프레임 늦지 않는다.
        m_rescanTimer -= Time.deltaTime;
        if (m_rescanTimer <= 0f)
        {
            m_rescanTimer = k_rescanSeconds;
            Rescan();
        }

        bool forced = Keyboard.current != null && Keyboard.current.f9Key.wasPressedThisFrame;

        foreach (Corpse corpse in m_tracked.Values)
            Sample(corpse, forced);
    }

    private void Rescan()
    {
        NpcRagdoll[] found = FindObjectsByType<NpcRagdoll>(
            FindObjectsInactive.Exclude,
            FindObjectsSortMode.None
        );

        var alive = new HashSet<int>();
        for (int i = 0; i < found.Length; i++)
        {
            NpcRagdoll ragdoll = found[i];
            int id = ragdoll.GetInstanceID();
            alive.Add(id);

            if (m_tracked.ContainsKey(id))
                continue;

            RagdollRig rig = ragdoll.GetComponentInChildren<RagdollRig>(true);
            if (rig == null || !rig.IsValid)
                continue; // 리그가 없는 프리팹 — 잴 뼈가 없다

            m_tracked[id] = new Corpse
            {
                Ragdoll = ragdoll,
                Npc = ragdoll.GetComponent<NpcController>(),
                Rig = rig,
                Rope = rig.GetComponent<RagdollRope>(),
                Bones = CollectBones(rig),
            };
        }

        // 죽고 사라진 시체를 흘려보낸다 — 안 지우면 파괴된 참조에 계속 접근한다.
        var stale = new List<int>();
        foreach (int id in m_tracked.Keys)
        {
            if (!alive.Contains(id))
                stale.Add(id);
        }
        for (int i = 0; i < stale.Count; i++)
            m_tracked.Remove(stale[i]);
    }

    // 리그 밑의 <b>래그돌 레이어</b> 리지드바디만 — 손에 든 아이템의 rb가 섞이면 최대v가 거짓말을 한다.
    // (RagdollRig가 뼈 배열을 공개하지 않아 여기서 같은 기준으로 다시 모은다)
    private static Rigidbody[] CollectBones(RagdollRig rig)
    {
        if (rig.BoneRoot == null)
            return new Rigidbody[0];

        int layer = LayerMask.NameToLayer(RagdollRig.k_layerName);
        Rigidbody[] all = rig.BoneRoot.GetComponentsInChildren<Rigidbody>(true);

        var bones = new List<Rigidbody>(all.Length);
        for (int i = 0; i < all.Length; i++)
        {
            if (all[i].gameObject.layer == layer)
                bones.Add(all[i]);
        }
        return bones.ToArray();
    }

    private void Sample(Corpse corpse, bool forced)
    {
        if (corpse.Ragdoll == null || corpse.Rig == null || corpse.Rig.Hips == null)
            return;

        Vector3 root = corpse.Ragdoll.transform.position;
        float rootDelta = corpse.HasLastRootPos ? Vector3.Distance(root, corpse.LastRootPos) : 0f;
        corpse.LastRootPos = root;
        corpse.HasLastRootPos = true;

        // 시체만 본다 — 산 NPC는 이 버그의 대상이 아니고, 전원을 찍으면 볼 줄이 묻힌다.
        if (corpse.Npc == null || !corpse.Npc.Death.IsDead)
            return;

        string payload = BuildPayload(corpse, root, rootDelta);

        // 트리거 셋. 순간이동만 보던 것을 넓혔다 — 실제 증상(가라앉음)이 <b>순간이동이 아닌 순간</b>에
        // 나왔기 때문이다: 감옥 안에서 밧줄을 <b>묶는 순간</b> 몸이 바닥으로 꺼졌다.
        bool teleported = rootDelta >= k_teleportDeltaMeters;

        // ① 바닥을 파고들었다 — 증상 자체다. 캡처 중이 아닐 때만 열어 한 번 꺼지면 계속 재발동하지 않게 한다.
        bool sank = !float.IsNaN(corpse.Clearance) && corpse.Clearance < k_sunkMeters;

        // ② 밧줄 관절이 걸리거나 풀렸다 — BeginRopePull이 Unfreeze를 태우므로 자세가 바뀌는 지점이고,
        //    실측에서 증상이 정확히 여기서 시작했다.
        bool ropeChanged = corpse.Jointed != corpse.PrevJointed;

        if (corpse.FramesLeft <= 0 && (teleported || sank || ropeChanged || forced))
            OpenCapture(corpse, rootDelta, forced, teleported ? "순간이동" : sank ? "가라앉음" : ropeChanged ? (corpse.Jointed ? "줄묶임" : "줄풀림") : "F9");

        if (corpse.FramesLeft > 0)
        {
            Emit($"[시체진단] f{corpse.SampleIndex:+00;-00} {payload}");
            corpse.SampleIndex++;
            corpse.FramesLeft--;
            return;
        }

        corpse.PreRoll.Enqueue(payload);
        while (corpse.PreRoll.Count > k_preRollFrames)
            corpse.PreRoll.Dequeue();
    }

    private void OpenCapture(Corpse corpse, float rootDelta, bool forced, string cause)
    {
        if (!m_announcedPath)
        {
            m_announcedPath = true;
            Debug.Log($"[시체진단] 로그 파일: {Path.GetFullPath(LogPath())}");
        }

        Emit(
            $"[시체진단] ▶캡처 {cause} {Role()} {corpse.Ragdoll.name} "
                + $"Δ루트={rootDelta:F2}m 여유={corpse.Clearance:+0.000;-0.000} "
                + $"줄={(corpse.Jointed ? 1 : 0)} 프리롤={corpse.PreRoll.Count}"
        );

        // 프리롤은 음수 프레임으로 — 트리거 직전이 정상 자세의 기준선이다.
        int index = -corpse.PreRoll.Count;
        while (corpse.PreRoll.Count > 0)
            Emit($"[시체진단] f{index++:+00;-00} {corpse.PreRoll.Dequeue()}");

        corpse.SampleIndex = 0;
        corpse.FramesLeft = k_captureFrames;
    }

    // ⚠ <b>반드시 한 줄이다.</b> Unity 콘솔은 두 줄까지 보여주지만 MCP read_console은 첫 줄만
    // 가져오므로, 판별점이 둘째 줄로 밀리면 도구로는 안 보인다.
    private string BuildPayload(Corpse corpse, Vector3 root, float rootDelta)
    {
        Vector3 hips = corpse.Rig.Hips.position;
        float lowest = corpse.Rig.LowestBoneY;
        float ground = GroundBelow(hips);
        float ceiling = CeilingAbove(hips);

        float maxSpeed = 0f;
        for (int i = 0; i < corpse.Bones.Length; i++)
        {
            float speed = corpse.Bones[i].linearVelocity.magnitude;
            if (speed > maxSpeed)
                maxSpeed = speed;
        }

        // §6-2의 기준값 그 자체 — 음수면 몸이 바닥을 파고들었다.
        float clearance = lowest - ground;
        corpse.Clearance = clearance;

        bool hipsKinematic = corpse.Rig.HipsBody != null && corpse.Rig.HipsBody.isKinematic;
        bool carried = corpse.Rope != null && corpse.Rope.IsBeingCarried;

        // ⚠ <b>관절과 운반자를 갈라 찍는다.</b> 계획서 §5-4가 경고한 대로 <b>운반자가 사라져도 관절은
        // 남는다</b> — 그래서 끌림(IsBeingCarried)만 보면 570m 밖 앵커에 매인 관절을 놓친다.
        bool jointed = corpse.Rope != null && corpse.Rope.IsAttached;

        corpse.PrevJointed = corpse.HasJointState ? corpse.Jointed : jointed;
        corpse.Jointed = jointed;
        corpse.HasJointState = true;

        // ⚠ 골반 <b>액터</b>의 위치 — 트랜스폼과 갈리는지가 이 계측의 핵심이다. 이 프로젝트는
        // m_AutoSyncTransforms = 0이라 트랜스폼에 쓴 값이 PhysX로 즉시 넘어가지 않는다.
        Vector3 hipsRb = corpse.Rig.HipsBody != null ? corpse.Rig.HipsBody.position : hips;
        float rbSplit = Vector3.Distance(hips, hipsRb);

        // ⚠ <b>피어 간 정렬 키다.</b> 프로세스마다 Time.time 원점이 달라 그것만으로는 같은 이벤트를
        // 짝지을 수 없다 — 실제로 호스트와 클라의 n번째 캡처가 서로 다른 NPC였다. 서버 틱은 전 피어가
        // 공유하고, NetworkObjectId는 여러 시체를 가른다. 둘이 있어야 비교가 성립한다.
        long tick = CorpseDiagLog.ServerTick();
        ulong netId = corpse.Npc != null && corpse.Npc.IsSpawned ? corpse.Npc.NetworkObjectId : 0UL;

        return $"{Role()} {corpse.Ragdoll.name} id={netId} 틱={tick} t={Time.time:F2} "
            + $"루트=({root.x:F2},{root.y:F3},{root.z:F2}) "
            + $"골반=({hips.x:F2},{hips.y:F3},{hips.z:F2}) "
            + $"골반rb=({hipsRb.x:F2},{hipsRb.y:F3},{hipsRb.z:F2}) rb차={rbSplit:F3} "
            + $"골반로컬y={hips.y - root.y:F3} 최저뼈y={lowest:F3} "
            + $"바닥y={ground:F3} 여유={clearance:+0.000;-0.000} 위y={ceiling:F3} "
            + $"Δ루트={rootDelta:F2} 평균v={corpse.Rig.AverageSpeed:F2} 최대v={maxSpeed:F2} "
            + $"골반kine={(hipsKinematic ? 1 : 0)} 얼림={(corpse.Ragdoll.IsFrozen ? 1 : 0)} "
            + $"래그돌={(corpse.Ragdoll.IsRagdollActive ? 1 : 0)} 끌림={(carried ? 1 : 0)} "
            + $"줄={(jointed ? 1 : 0)}";
    }

    // NpcRagdoll.TryGroundUnder와 같은 수치·같은 마스크 — 프로덕션이 보는 바닥과 같은 값을 내야 한다.
    private static float GroundBelow(Vector3 hips)
    {
        return Physics.Raycast(
            hips + Vector3.up * k_probeLift,
            Vector3.down,
            out RaycastHit hit,
            k_probeLift + k_probeDistance,
            k_groundMask,
            QueryTriggerInteraction.Ignore
        )
            ? hit.point.y
            : float.NaN;
    }

    private static float CeilingAbove(Vector3 hips)
    {
        return Physics.Raycast(
            hips,
            Vector3.up,
            out RaycastHit hit,
            k_ceilingProbeDistance,
            k_groundMask,
            QueryTriggerInteraction.Ignore
        )
            ? hit.point.y
            : float.NaN;
    }

    // 역할 판정은 <see cref="CorpseDiagLog"/>가 정본이다 — 두 벌을 두면 줄 안의 역할 표기와
    // 파일 이름이 갈릴 수 있고, 그러면 병합이 같은 피어를 둘로 센다.
    private static string Role() => CorpseDiagLog.Role();

    // 표본은 <b>파일에만</b> 보낸다 — 캡처 한 번이 105줄이라 콘솔에 흘리면 정작 봐야 할
    // [시체배치] 다섯 줄이 묻힌다. 파일에서는 Tools > Ragdoll > 시체 진단 로그 모으기 로 함께 읽힌다.
    private void Emit(string line) => CorpseDiagLog.Write(line, alsoConsole: false);

    private static string LogPath() => CorpseDiagLog.PathForThisPeer();
}
#endif
