using UnityEngine;

/// <summary>
/// 저장해 둔 포즈에서 <b>지금 애니메이터가 놓는 포즈로</b> 스르륵 끌고 간다 — 부활 블렌드. (#506)
///
/// <b><see cref="RagdollRig"/>에서 떼어냈다</b> (#571 사망 전용 모델 분리). 저쪽은 시체 리그에 붙어
/// 있는데, 부활 블렌드는 <b>살아있는 리그</b>에서 일어난다 — 시체는 이미 꺼진 뒤이고 섞어야 하는
/// 상대는 애니메이터가 평가한 포즈다. 물리를 모르는 순수 보간이라 <see cref="MonoBehaviour"/>일
/// 이유도 없다.
///
/// <b>매 프레임 목표를 다시 읽는 것이 핵심이다.</b> 목표를 <see cref="Begin"/> 시점에 고정하지 않고
/// <see cref="Tick"/>이 그때그때의 로컬값을 읽는다 — <b>LateUpdate에서</b> 부르면 그 값이 곧 이번
/// 프레임의 애니메이터 포즈라, 재생되는 기상 클립을 향해 살아있는 목표로 수렴한다. 고정 목표로
/// 섞으면 클립은 흘러가는데 블렌드만 옛 프레임을 향해 가서 끝나는 순간 툭 튄다.
/// </summary>
public class RagdollPoseBlend
{
    private readonly Transform[] m_bones;
    private readonly Vector3[] m_fromPositions;
    private readonly Quaternion[] m_fromRotations;

    private float m_timer;

    /// <summary>섞을 뼈를 제대로 잡았는가 — 거짓이면 소유자는 블렌드 없이 즉시 복귀해야 한다.</summary>
    public bool IsValid => m_bones != null && m_bones.Length > 0;

    /// <param name="boneRoot">
    /// 리그 최상단. 이하 <b>전 트랜스폼</b>을 섞는다 — 물리를 받지 않는 뼈(척추 사이·목·손가락·발)까지
    /// 포함해야 한다. 그것들은 넘겨받은 정착 포즈에 멈춰 있어, 빼놓으면 블렌드 첫 프레임에 목과 손이 튄다.
    /// </param>
    public RagdollPoseBlend(Transform boneRoot)
    {
        m_bones =
            boneRoot != null ? boneRoot.GetComponentsInChildren<Transform>(true) : new Transform[0];

        m_fromPositions = new Vector3[m_bones.Length];
        m_fromRotations = new Quaternion[m_bones.Length];
    }

    /// <summary>지금 포즈를 출발점으로 잡는다 — 애니메이터가 덮어쓰기 <b>전에</b> 부른다.</summary>
    public void Begin()
    {
        for (int i = 0; i < m_bones.Length; i++)
        {
            m_fromPositions[i] = m_bones[i].localPosition;
            m_fromRotations[i] = m_bones[i].localRotation;
        }

        m_timer = 0f;
    }

    /// <summary>블렌드 한 프레임 — <b>LateUpdate에서</b> 부른다 (클래스 주석의 이유).</summary>
    /// <returns>블렌드가 끝났으면 참.</returns>
    public bool Tick(float blendSeconds)
    {
        m_timer += Time.deltaTime;
        float t = blendSeconds <= 0f ? 1f : Mathf.Clamp01(m_timer / blendSeconds);

        for (int i = 0; i < m_bones.Length; i++)
        {
            m_bones[i].localRotation = Quaternion.Slerp(
                m_fromRotations[i],
                m_bones[i].localRotation,
                t
            );
            m_bones[i].localPosition = Vector3.Lerp(
                m_fromPositions[i],
                m_bones[i].localPosition,
                t
            );
        }

        return t >= 1f;
    }
}
