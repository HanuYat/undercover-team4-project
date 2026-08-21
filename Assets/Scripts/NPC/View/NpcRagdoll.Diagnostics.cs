using UnityEngine;

/// <summary>
/// <see cref="NpcRagdoll"/>의 <b>진단 계측 전부</b> — 본체에서 갈라낸 partial이다.
///
/// <b>여기 있는 것은 전부 임시다.</b> 갈리면 이 파일을 통째로 지운다 — 본체에 남는 것은 주석
/// 처리된 호출 줄 몇 개뿐이다. 무엇을 재던 것인지는 <c>docs/npc-ragdoll.md</c> §5·§11에 있다.
///
/// ⚠ <b>2026-08-20 — 호출부를 전부 주석 처리했다. 코드는 남긴다.</b> 토글이 없어 항상 켜지는
/// 구조라 다른 팀원이 Play하면 콘솔이 시끄러웠다. 다시 재야 하면 호출 줄의 <c>//</c>만 풀면 된다.
/// </summary>
public partial class NpcRagdoll
{
    /// <summary>
    /// 시체 밑 여유 — <b>최저뼈Y − 바닥Y.</b> 음수면 몸이 바닥을 파고들었다.
    /// 바닥을 못 찾으면 <see cref="float.NaN"/>이다 — 호출부는 "판단 불가"로 다뤄야 한다.
    /// </summary>
    private float LowestBoneClearance()
    {
        if (!TryGroundUnder(m_rig.Hips.position, out Vector3 ground))
            return float.NaN;

        return m_rig.LowestBoneY - ground.y;
    }

    // 진단 ⑥ 보조 (임시) — 그 자리에서 지면으로 잡히는 <b>오브젝트 이름</b>. 위 탐색과 같은 레이다.
    private string TryGroundColliderUnder(Vector3 from)
    {
        const float k_probeLift = 0.5f;

        return Physics.Raycast(
            from + Vector3.up * k_probeLift,
            Vector3.down,
            out RaycastHit hit,
            k_probeLift + m_groundProbeDistance,
            m_groundMask,
            QueryTriggerInteraction.Ignore
        )
            ? hit.collider.name
            : "없음";
    }

    // ---- 진단 (임시 — 기상 중 사망 시 클라 시체가 어긋나는 증상 추적용. 갈리면 통째로 지운다) ----
    //
    // ⚠ <b>2026-08-20 — 호출부를 전부 주석 처리했다. 코드는 남긴다.</b> 토글이 없어 항상 켜지는
    // 구조라 다른 팀원이 Play하면 콘솔이 시끄러웠다. 다시 재야 하면 해당 호출 줄의 <c>//</c>만
    // 풀면 된다 — 무엇을 재는 계측인지는 아래 각 주석에 그대로 남아 있다.
    //
    // 지운 계측이 무엇을 재던 것인지는 docs/npc-ragdoll.md §5에 있다. 진단 ①(권위 쪽 정착 상태)과
    // ②(정착 자세와 루트의 도착 시차)는 <b>재던 대상이 사라져</b> 함께 지웠다 — 정착이 뼈 길이를
    // 되돌리지도, 지면을 재정렬하지도, 몸을 루트에 매달지도 않는다.

    // 진단 ⑤ — 원격의 정착 결과. 호스트의 같은 값과 <b>같아야 한다.</b> 다르면 골격이 갈려 있는
    // 것이고(스트림이 뼈 길이를 안 싣는다 — docs/npc-ragdoll.md §7), 음수면 몸이 바닥에 박혀 있다.
    private void LogRemoteSettledClearance()
    {
        float clearance = LowestBoneClearance();
        Debug.Log(
            $"[진단5 클라여유] {name} 여유={clearance:F3}"
                + $"{(float.IsNaN(clearance) ? " ⚠바닥을 못 찾았다" : clearance < -0.02f ? " ⚠바닥에 박혔다" : clearance > 0.15f ? " ⚠떠 있다" : " (정상)")}",
            this
        );
    }

    // 진단 ⑥ (임시) — <b>바닥을 파고든 뼈를 뼈 단위로 집는다.</b>
    //
    // 부분 잠김("상체가 잠기거나 하체가 잠김, 전신은 아니다")의 원인이 두 층으로 갈리는데
    // <b>처방이 정반대다.</b> 어느 쪽인지 이 한 줄이 가른다:
    //
    //  · <b>콜라이더 없는 뼈</b>(손·발·발가락·목·쇄골) — 물리가 막을 것이 없어 원래 통과한다.
    //    프리팹 실측으로 리지드바디 12개 = 콜라이더 12개이므로 <b>나머지 리그 전부</b>가 여기다.
    //    처방은 RagdollSetup에 캡슐을 더하는 프리팹 작업이고 코드로 할 일이 없다
    //  · <b>콜라이더 있는 뼈</b> — 진입 자세가 이미 파고들어 있었고
    //    <c>RagdollRig</c>의 겹침 탈출 속도 상한(0.5m/s = 50Hz에서 스텝당 6mm, 중력을 빼면 실질
    //    0.3m/s)으로는 못 빠져나온 것이다. 처방은 그 상수와 접촉 오프셋 튜닝이다
    //
    // <b>진입과 정착을 둘 다 찍는 것이 핵심이다.</b> 깊이가 줄지 않았으면 물리가 손을 못 댄 것이고,
    // 그러면 겹침 탈출이 아니라 <b>진입 자세 자체</b>를 봐야 한다는 뜻이다.
    //
    // 지면은 <b>뼈마다 따로</b> 잰다 — 골반 하나로 재면 경사·계단에 걸친 몸이 통째로 틀린다.
    private Transform[] m_penetrationProbe;

    private void LogFloorPenetration(string phase, int detail = 6)
    {
        if (m_rig == null || m_rig.BoneRoot == null)
            return;

        if (m_penetrationProbe == null)
            m_penetrationProbe = m_rig.BoneRoot.GetComponentsInChildren<Transform>(true);

        const float k_reportBelow = -0.01f; // 1cm 아래부터 — 그보다 얕으면 접촉 오차다

        var sunk = new System.Collections.Generic.List<(string Name, float Depth, bool Covered)>();
        int covered = 0;

        // 가장 깊은 뼈가 <b>무엇을</b> 지면으로 잡았는지 — 도로가 아니라 인도 턱·잔해가 나오면
        // 이 측정이 "바닥 깊이"가 아니라 <b>모서리에 물린 것</b>을 재고 있다는 뜻이다.
        string deepestHit = "?";
        float deepest = 0f;

        for (int i = 0; i < m_penetrationProbe.Length; i++)
        {
            Transform bone = m_penetrationProbe[i];
            if (bone == null || !TryGroundUnder(bone.position, out Vector3 ground))
                continue;

            float clearance = bone.position.y - ground.y;
            if (clearance >= k_reportBelow)
                continue;

            bool hasCollider = bone.GetComponent<Collider>() != null;
            if (hasCollider)
                covered++;

            if (clearance < deepest)
            {
                deepest = clearance;
                deepestHit = TryGroundColliderUnder(bone.position);
            }

            sunk.Add((bone.name, clearance, hasCollider));
        }

        sunk.Sort((a, b) => a.Depth.CompareTo(b.Depth)); // 깊은 것부터

        var line = new System.Text.StringBuilder();
        line.Append($"[진단6 관통] {name} {phase} 파고든뼈 {sunk.Count}");
        line.Append($" (콜라이더유 {covered} / 무 {sunk.Count - covered})");

        int shown = Mathf.Min(sunk.Count, detail);
        for (int i = 0; i < shown; i++)
            line.Append($" | {sunk[i].Name} {sunk[i].Depth:F3}{(sunk[i].Covered ? "(유)" : "(무)")}");

        if (sunk.Count > shown)
            line.Append($" …+{sunk.Count - shown}");

        if (sunk.Count > 0)
            line.Append($" | 최심지면={deepestHit}");

        Debug.Log(line.ToString(), this);
    }

    // 진단 ⑦ (임시) — <b>기상 창을 프레임 단위로 훑는다.</b>
    //
    // 가설: 부활 블렌드가 뼈를 되돌리는 <b>도중</b>에 몸이 땅속으로 들어가는 구간이 있고, 그때
    // 재래그돌되면 물리가 파묻힌 자세를 그대로 받는다.
    //
    // 블렌드는 <b>로컬 회전</b>을 섞는다(<see cref="RagdollPoseBlend"/>). 출발점은 물리가 만든
    // 임의의 엎드린 자세이고 목표는 authoring된 기상 클립 자세인데, 두 자세가 다르면 <b>그 사이
    // 보간 경로는 어느 쪽 끝점도 아닌 자세들</b>이다 — 회전 보간은 팔다리 위치를 보존하지 않으므로
    // 중간에 양 끝점보다 더 낮은 자세가 나올 수 있다. 가설이 맞다면 여기서 <b>깊이가 솟다 가라앉는
    // 봉우리</b>가 보인다.
    //
    // 창은 블렌드(0.3초)보다 넉넉히 잡는다 — 딥이 블렌드 뒤 클립 초반일 수도 있다.
    // 권위 쪽만 찍는다: 물리가 도는 곳이 여기고, 로그 양이 절반이 된다.
    private const int k_riseProbeFrames = 30;

    private int m_riseProbeFrame = -1;

    private void BeginRiseProbe() => m_riseProbeFrame = HasMoveAuthority ? 0 : -1;

    private void TickRiseProbe()
    {
        if (m_riseProbeFrame < 0 || m_riseProbeFrame >= k_riseProbeFrames)
            return;

        // 상세는 1개만 — 30줄이 나가므로 봉우리의 <b>모양</b>을 읽을 수 있어야 한다.
        LogFloorPenetration($"기상+{m_riseProbeFrame:00}{(m_blending ? " 블렌드" : "")}", detail: 1);
        m_riseProbeFrame++;
    }

    // 진단 ④ — <b>스트림에 실리지 않는 뼈</b>가 피어마다 같은 자세인가.
    //
    // 리그에는 Rigidbody가 없는 뼈가 섞여 있고(Spine_01 · Spine_03 · Neck · Clavicle), 스트림은
    // <c>m_bodies</c>만 실으므로 그 뼈들은 <b>각 피어의 애니메이터가 마지막에 놓은 자세에 그대로
    // 멈춘다.</b> 그런데 <c>Hips → Spine_01 → Spine_02</c>라 <b>상체 전체가 Spine_01에 매달려
    // 있다</b> — 여기가 피어마다 다르면 스트림된 회전이 전부 같아도 몸이 다른 자세로 굳는다.
    //
    // 기상 클립은 척추가 빠르게 펴지는 구간이고, 기상 시작이 ClientRpc + 로컬 시계라
    // (<c>NpcAnimationDriver.HandleStandUp</c>) 피어마다 <b>클립 시간이 RTT만큼 어긋난다.</b>
    // 서 있다 죽으면 양쪽 다 같은 대기 자세라 차이가 작고, 기상 중에만 크게 벌어져야 가설이 맞다.
    //
    // 읽는 법: 같은 시체에 대해 <b>호스트 줄과 클라 줄을 나란히 놓고</b> 각도를 비교한다.
    private static readonly string[] k_unstreamedProbeBones =
    {
        "Spine_01",
        "Spine_03",
        "Neck",
        "Clavicle_L",
    };

    private Transform[] m_unstreamedProbe;
    private float m_probeClipTime = -1f;

    // ⚠ 애니메이터를 끄기 전에 부른다 — 꺼진 뒤에는 클립 시간을 못 읽는다.
    private void SampleAnimatorClock()
    {
        m_probeClipTime =
            m_animator != null && m_animator.enabled && m_animator.runtimeAnimatorController != null
                ? m_animator.GetCurrentAnimatorStateInfo(0).normalizedTime
                : -1f;
    }

    private void LogUnstreamedBones()
    {
        if (m_rig == null || m_rig.BoneRoot == null)
            return;

        if (m_unstreamedProbe == null)
        {
            m_unstreamedProbe = new Transform[k_unstreamedProbeBones.Length];
            Transform[] all = m_rig.BoneRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < k_unstreamedProbeBones.Length; i++)
            {
                for (int j = 0; j < all.Length; j++)
                {
                    if (all[j].name != k_unstreamedProbeBones[i])
                        continue;

                    m_unstreamedProbe[i] = all[j];
                    break;
                }
            }
        }

        var line = new System.Text.StringBuilder();
        line.Append($"[진단4 비스트림뼈] {name} 권위={HasMoveAuthority} ");
        line.Append(m_probeClipTime >= 0f ? $"클립t={m_probeClipTime:F3}" : "클립t=?");

        for (int i = 0; i < m_unstreamedProbe.Length; i++)
        {
            Transform bone = m_unstreamedProbe[i];
            if (bone == null)
            {
                line.Append($" | {k_unstreamedProbeBones[i]}=없음");
                continue;
            }

            Vector3 euler = bone.localRotation.eulerAngles;
            line.Append(
                $" | {bone.name}=({Mathf.DeltaAngle(0f, euler.x):F1},"
                    + $"{Mathf.DeltaAngle(0f, euler.y):F1},{Mathf.DeltaAngle(0f, euler.z):F1})"
            );
        }

        Debug.Log(line.ToString(), this);
    }

    // 진단 ⑧ (임시) — <b>물리가 뼈를 얼마나 늘려 놨는가.</b>
    //
    // 스트림은 뼈 <b>길이</b>를 싣지 않는다 — "관절이 유지하므로 상수"가 그 설계의 전제다
    // (docs/728-ragdoll-pose-streaming.md §1-2). 그런데 관절 projection이 꺼져 있어 붙드는 일이
    // 전적으로 solver 반복 몫이고(<c>RagdollRig</c>의 solver 상수 주석), 강한 임펄스나 밧줄 장력에서는
    // 눈에 띄게 늘어난다. <b>게다가 시체는 그 길이를 되돌릴 곳이 없다</b> — <c>RestoreBindPose</c>는
    // <c>ExitRagdoll</c>에서만 도는데 시체는 그 문을 영영 지나지 않고,
    // <c>RagdollRig.RestoreBindBoneLengths</c>는 지금 <b>아무도 부르지 않는다</b>(옛 키네마틱 정착과
    // 함께 호출부가 사라졌다).
    //
    // <b>읽는 법.</b> 진입에서 0에 가깝다가 밧줄·배치를 지나며 커지면, 그 값이 곧 원격과 갈릴 수 있는
    // 폭의 상한이다. 끝까지 0에 가까우면 이 가설은 기각이고 진단 ⑨도 함께 0으로 나온다.
    private System.Collections.Generic.List<(string Bone, float Drift)> m_driftProbe;

    private void LogBoneLengthDrift(string phase)
    {
        if (!HasMoveAuthority || m_rig == null || !m_rig.IsValid)
            return;

        // <b>어느 뼈인지가 값보다 중요해졌다</b> — 밧줄부착 시점에 이미 0.127m였고 끌기·배치를
        // 지나도 상수라(실측 2026-08-19), 물리가 그때그때 늘리는 것이 아니라 <b>이미 굳어 있는
        // 오프셋</b>이다. 뼈 이름이 그것이 어느 관절에서 생겼는지를 가른다.
        if (m_driftProbe == null)
            m_driftProbe = new System.Collections.Generic.List<(string Bone, float Drift)>();

        m_rig.CollectBindPositionDrift(m_driftProbe);
        m_driftProbe.Sort((a, b) => b.Drift.CompareTo(a.Drift));

        float drift = m_driftProbe.Count > 0 ? m_driftProbe[0].Drift : 0f;

        var line = new System.Text.StringBuilder();
        line.Append($"[진단8 뼈길이] {name} {phase} 최대드리프트={drift:F3}m 뼈={m_driftProbe.Count}");

        for (int i = 0; i < Mathf.Min(m_driftProbe.Count, 3); i++)
            line.Append($" | {m_driftProbe[i].Bone} {m_driftProbe[i].Drift:F3}");

        line.Append(drift > 0.02f ? " ⚠늘어났다" : " (정상)");

        Debug.Log(line.ToString(), this);
    }

    // 진단 ⑨ (임시) — <b>원격이 그리는 몸과 내 몸의 차를 뼈 단위로, 호스트 혼자서 잰다.</b>
    //
    // 스트림이 싣는 것은 골반 <b>월드 위치</b>와 뼈 <b>로컬 회전</b>뿐이다
    // (<see cref="RagdollPoseStreamer"/>). 원격은 그 회전을 <b>자기 바인드 길이</b> 골격에 얹어 몸을
    // 다시 만드는데, 권위 쪽 뼈는 물리가 늘려 놓아 길이가 다르다(진단 ⑧). 여기서 재는 것이 정확히
    // 그 차 — <b>클라 화면과 호스트 화면이 벌어지는 폭</b>이다.
    //
    // <b>원격 로그 없이 판별되는 것이 요점이다</b>(MPPM 가상 플레이어는 콘솔이 안 잡힌다). 원격이 할
    // 재구성을 호스트에서 그대로 따라 해 실제 뼈 위치와 뺀다: 골반은 <b>보내는 값</b>에 못박고,
    // 자식으로 내려가며 <c>부모월드 × (바인드 길이 · 지금 로컬 회전)</c>으로 위치를 다시 만든다.
    // 체인 끝(팔·손)일수록 오차가 쌓이므로 <b>상위 뼈 이름이 어디서 벌어졌는지</b>를 말해 준다.
    //
    // ⚠ 재지 <b>않는</b> 것 둘: ① 골반 위쪽 뼈 — 원격에서는 루트 계층이 놓는다, ② 원격 루트 NT의
    // 보간 오차 — 루트 회전은 양쪽이 같다고 본다. 그쪽이 의심되면 진단 ⑤(클라 여유)와 함께 읽는다.
    //
    // ⚠⚠ <b>고친 뒤로 이 값은 "원격이 그리게 될 몸"이 아니다.</b> 신뢰 1회 패킷이 뼈 길이를 함께
    // 나르므로(docs/npc-ragdoll.md §8) 원격은 더 이상 바인드 길이로 재구성하지 않는다. 지금 이 값이
    // 뜻하는 것은 <b>"길이를 안 실었다면 얼마나 갈렸을까"</b> — 즉 드리프트가 커지고 있는지 보는
    // 감시 지표다. 화면이 맞는지는 이 로그가 아니라 <b>호스트와 클라 화면을 나란히 놓고</b> 본다.
    private Vector3[] m_reconPositions;
    private Quaternion[] m_reconRotations;

    private void LogRemoteReconstructionError(string phase, int detail = 5)
    {
        if (!HasMoveAuthority || m_rig == null || !m_rig.IsValid)
            return;

        Transform[] bones = m_rig.PoseBones;
        Transform hips = m_rig.Hips;
        if (bones == null || bones.Length == 0 || hips == null)
            return;

        if (m_reconPositions == null || m_reconPositions.Length != bones.Length)
        {
            m_reconPositions = new Vector3[bones.Length];
            m_reconRotations = new Quaternion[bones.Length];
        }

        var diffs = new System.Collections.Generic.List<(string Name, float Error)>();
        float worst = 0f;
        float sum = 0f;

        for (int i = 0; i < bones.Length; i++)
        {
            Transform bone = bones[i];
            if (bone == null || !bone.IsChildOf(hips))
                continue; // 골반 위쪽 — 원격에서는 루트 계층이 놓는 자리라 잴 대상이 아니다

            if (bone == hips)
            {
                // 원격이 월드로 못박는 값 그대로다 — 여기서는 오차가 0이라 세지 않는다.
                m_reconPositions[i] = hips.position;
                m_reconRotations[i] = hips.rotation;
                continue;
            }

            int parent = IndexOfBone(bones, bone.parent);
            if (parent < 0 || !m_rig.TryGetBindLocalPosition(bone, out Vector3 bindLocal))
                continue;

            // 계층 수학 그대로 — 원격이 하는 일이 이것이다(회전은 받은 것, 길이는 자기 바인드).
            m_reconRotations[i] = m_reconRotations[parent] * bone.localRotation;
            m_reconPositions[i] =
                m_reconPositions[parent]
                + m_reconRotations[parent] * Vector3.Scale(bindLocal, bone.parent.lossyScale);

            float error = Vector3.Distance(m_reconPositions[i], bone.position);
            sum += error;
            if (error > worst)
                worst = error;

            diffs.Add((bone.name, error));
        }

        diffs.Sort((a, b) => b.Error.CompareTo(a.Error)); // 큰 것부터

        var line = new System.Text.StringBuilder();
        line.Append($"[진단9 재구성] {name} {phase} 최대={worst:F3}m");
        line.Append($" 평균={(diffs.Count > 0 ? sum / diffs.Count : 0f):F3} 뼈={diffs.Count}");

        int shown = Mathf.Min(diffs.Count, detail);
        for (int i = 0; i < shown; i++)
            line.Append($" | {diffs[i].Name} {diffs[i].Error:F3}");

        line.Append(worst > 0.05f ? " ⚠원격과 갈린다" : worst > 0.02f ? " ⚠벌어지는 중" : " (정상)");

        Debug.Log(line.ToString(), this);
    }

    // 진단 ⑨ 보조 (임시) — 뼈 배열에서 부모의 자리를 찾는다. 리그당 20개 남짓이라 선형으로 충분하다.
    private static int IndexOfBone(Transform[] bones, Transform bone)
    {
        if (bone == null)
            return -1;

        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i] == bone)
                return i;
        }

        return -1;
    }
}