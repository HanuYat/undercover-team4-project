using UnityEngine;

/// <summary>
/// 바퀴 회전 연출 — 폭탄이 실제로 움직인 거리만큼 바퀴를 굴린다. (#399 표현 계층)
///
/// <b>순수 로컬 표현이고 동기화가 없다.</b> 이동은 서버의 NavMeshAgent가 만들고 원격 피어에는
/// NetworkTransform이 결과만 실어다 주는데, 이 컴포넌트는 어느 쪽이든 <b>지난 프레임 대비 얼마나
/// 움직였나</b>만 보고 굴리므로 두 경우 모두 같은 그림이 나온다 — 속도 값을 따로 전파할 필요가 없다.
///
/// 굴리는 양은 이동 거리에서 나온다(<see cref="m_wheelRadius"/> 기준 원둘레). 속도를 곱해 대충
/// 돌리면 밀려날 때·멈출 때 바퀴만 헛도는 그림이 나오는데, 거리 기준이면 그 어긋남이 원천적으로 없다.
/// 진압봉에 밀려 뒤로 갈 때는 전진 성분이 음수가 되어 저절로 거꾸로 돈다.
/// </summary>
public class BombWheelSpin : MonoBehaviour
{
    [Tooltip("굴릴 바퀴 축들 — 각자의 로컬 X축을 중심으로 돈다 (Synty 로봇의 Wheel_F/M/B)")]
    [SerializeField]
    private Transform[] m_wheels;

    [Tooltip("바퀴 반지름(m) — 이동 거리를 회전각으로 바꾸는 기준. 작을수록 빨리 돈다")]
    [SerializeField]
    private float m_wheelRadius = 0.17f;

    private Vector3 m_lastPosition;

    private void OnEnable()
    {
        m_lastPosition = transform.position;
    }

    private void LateUpdate()
    {
        if (m_wheels == null || m_wheels.Length == 0 || m_wheelRadius <= 0.001f)
            return;

        Vector3 delta = transform.position - m_lastPosition;
        m_lastPosition = transform.position;

        // 전진 성분만 본다 — 제자리 회전으로는 바퀴가 돌지 않는다(실제로 굴러간 거리가 아니다)
        float travelled = Vector3.Dot(delta, transform.forward);
        if (Mathf.Abs(travelled) < 0.0001f)
            return;

        float degrees = travelled / (2f * Mathf.PI * m_wheelRadius) * 360f;
        for (int i = 0; i < m_wheels.Length; i++)
        {
            if (m_wheels[i] != null)
                m_wheels[i].Rotate(Vector3.right, degrees, Space.Self);
        }
    }
}
