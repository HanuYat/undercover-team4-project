using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 비밀 청탁의 인도 지점 (#485) — 꺼낸 대상을 여기까지 데려오면 그 대상은 도시로 사라진다.
///
/// 책임은 하나: "이 지점이 인도 범위 안인가"(<see cref="Contains"/>). 완수 판정과 소멸·지급은
/// <see cref="SecretFavorBroker"/>가 굴린다 — 범위만 답하고 판정은 남에게 맡기는 역할 분담이다.
/// 영역 판정도 그쪽과 같은 방식이다(로컬 공간 박스 검사 — 회전을 살리고 물리 동기화를 기다리지 않는다).
///
/// <b>번호로 식별한다.</b> 의뢰 전송에는 이 번호만 싣고, 수신 클라가 그 번호로 지점을 찾아 표식을 켠다.
/// 위치를 이름(문구)으로만 알리면 미니맵이 없는 현장에서는 사실상 못 찾는다.
/// (씬 탐색 순서는 피어마다 다를 수 있어 인덱스는 식별자로 쓸 수 없다)
///
/// 씬 배치: 지점 프롭에 이 컴포넌트와 <see cref="BoxCollider"/>(Is Trigger)를 함께 둔다.
/// </summary>
public class SecretFavorDropoff : MonoBehaviour
{
    [Header("식별 번호 (지점마다 서로 다르게)")]
    [SerializeField] private int m_id;

    [Header("인도 범위 (비우면 판정이 일어나지 않는다)")]
    [SerializeField] private BoxCollider m_zone;

    [Header("의뢰 표식 (수신자 화면에만 켜진다)")]
    [Tooltip(
        "지점 위에 세울 홀로그램·빔 등. 기본은 꺼진 상태로 두고 청탁을 받은 클라이언트에서만 켜진다.\n\n"
            + "멀리서·건물 뒤에서도 보여야 길잡이가 된다 — 세로로 긴 발광 오브젝트로 만들고 머티리얼의 "
            + "ZTest를 Always로 두면 벽에 가려도 보인다. 조준을 방해하지 않게 콜라이더는 두지 말 것"
    )]
    [SerializeField] private GameObject m_marker;

    // 씬에 놓인 지점들 — 브로커가 여기서 뽑고, 수신 클라가 번호로 되찾는다.
    private static readonly List<SecretFavorDropoff> s_all = new List<SecretFavorDropoff>();

    public int Id => m_id;

    /// <summary>씬의 인도 지점 전체. 서버·클라 구분 없다(씬 오브젝트라 모든 피어에 있다).</summary>
    public static IReadOnlyList<SecretFavorDropoff> All => s_all;

    private void OnEnable() => s_all.Add(this);

    private void OnDisable() => s_all.Remove(this);

    private void Awake()
    {
        // 씬에 켜진 채 저장돼도 시작은 항상 꺼짐 — 켜져 있으면 청탁을 받지 않은 사람 화면에도 보인다
        SetMarkerVisible(false);

        if (m_zone == null)
        {
            Debug.LogWarning($"SecretFavorDropoff({name}): 인도 범위(BoxCollider)가 없어 도착 판정이 일어나지 않는다", this);
            return;
        }

        // 트리거가 아니면 대상을 데리고 들어갈 수가 없다 — 범위 콜라이더의 흔한 사고
        if (!m_zone.isTrigger)
            Debug.LogWarning($"SecretFavorDropoff({name}): 인도 범위의 Is Trigger가 꺼져 있다 — 플레이어가 막힌다", this);
    }

    /// <summary>번호로 지점을 찾는다 — 의뢰 수신 클라가 표식을 켜려고 쓴다. 없으면 null.</summary>
    public static SecretFavorDropoff Find(int id)
    {
        for (int i = 0; i < s_all.Count; i++)
            if (s_all[i] != null && s_all[i].m_id == id)
                return s_all[i];

        return null;
    }

    /// <summary>
    /// 의뢰 표식을 켜고 끈다 — <b>부르는 피어의 화면에만</b> 반영된다.
    /// 이 지점은 NetworkObject가 없는 씬 오브젝트라 활성화가 동기화되지 않는다(붙이지 말 것).
    /// 그래서 청탁을 받은 클라이언트에서만 켜면 다른 플레이어에게는 아무 변화가 없다.
    /// </summary>
    public void SetMarkerVisible(bool visible)
    {
        if (m_marker != null)
            m_marker.SetActive(visible);
    }

    /// <summary>모든 지점의 표식을 끈다 — 완수·취소 시. 어느 지점이었는지 기억하지 않아도 되게 한다.</summary>
    public static void HideAllMarkers()
    {
        for (int i = 0; i < s_all.Count; i++)
            if (s_all[i] != null)
                s_all[i].SetMarkerVisible(false);
    }

    /// <summary>인도 범위의 한가운데(월드 좌표) — 반출 대상이 걸어올 목적지다 (#548).
    /// 범위 미배선이면 지점 오브젝트 자리를 준다. 회전한 범위도 맞게 나오도록 로컬→월드로 옮긴다.</summary>
    public Vector3 Center =>
        m_zone != null ? m_zone.transform.TransformPoint(m_zone.center) : transform.position;

    /// <summary>이 지점이 인도 범위 안인가 — 범위 미배선이면 항상 false. (JailZone.ContainsPoint와 동일 방식)</summary>
    public bool Contains(Vector3 position)
    {
        if (m_zone == null)
            return false;

        Vector3 local = m_zone.transform.InverseTransformPoint(position) - m_zone.center;
        Vector3 half = m_zone.size * 0.5f;

        return Mathf.Abs(local.x) <= half.x
            && Mathf.Abs(local.y) <= half.y
            && Mathf.Abs(local.z) <= half.z;
    }
}
