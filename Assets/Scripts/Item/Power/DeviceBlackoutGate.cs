/// <summary>
/// 전자기기 먹통(#106) 사용 차단 게이트 — 먹통 중 못 쓰는 아이템(스캐너·구역 스캐너)이 각자
/// <see cref="DeviceBlackoutEvent"/> 조회·캐시·재해석을 복제하지 않도록 공용으로 뽑았다. (#490)
///
/// 먹통 이벤트 참조는 첫 조회 후 캐시한다. 씬이 바뀌어 이벤트가 파괴되면 Unity의 null 판정에
/// 걸려 자동으로 다시 해석된다 — 아이템은 씬을 넘어 살아남을 수 있으므로 이 재해석이 필수다.
///
/// <see cref="ServerChannel"/>과 같은 관례로 NetworkBehaviour가 아닌 순수 C# 클래스다 —
/// 아이템이 필드로 들고 쓴다. 판정값은 <see cref="DeviceBlackoutEvent.IsCommsBlackout"/>이
/// 피어별로 갈라주므로 클라 힌트와 서버 판정이 항상 같은 규칙을 쓴다.
/// </summary>
public class DeviceBlackoutGate
{
    private DeviceBlackoutEvent m_blackout;

    /// <summary>전자기기 먹통 중인지. 먹통 이벤트가 없는 구성(테스트 등)에서는 항상 false.</summary>
    public bool IsActive
    {
        get
        {
            if (m_blackout == null)
                m_blackout = App.Game.SuddenEvent?.GetEvent<DeviceBlackoutEvent>();
            return m_blackout != null && m_blackout.IsCommsBlackout;
        }
    }
}
