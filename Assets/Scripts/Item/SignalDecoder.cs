using Unity.Netcode;
using UnityEngine;
using UnityEngine.Localization;

/// <summary>
/// 신호 해석기 — 본부에 설치되는 단말. 타이핑한 짧은 메시지를 팀 전원에게 보낸다. (GDD 8-3/8-4/4-4, #108)
///
/// <b>존재 이유는 먹통 우회다.</b> '전자기기 먹통'(#106) 중에는 음성이 알아듣기 어렵게 망가지는데
/// (<see cref="DeviceBlackoutView"/>가 <see cref="VivoxManager.SetVoiceDistorted"/>를 켠다),
/// 이 단말은 텍스트 경로라 그 영향을 받지 않는다 — 먹통 판정을 <b>일부러 참조하지 않는다</b>.
/// 여기에 먹통 게이트를 추가하면 아이템의 존재 이유가 사라진다.
/// (#372에서 먹통 연출이 '무전 차단'에서 '음성 왜곡'으로 바뀌었지만, 텍스트가 먹통 중 유일하게
/// 또렷한 통신 수단이라는 이 아이템의 역할은 그대로다 — 오히려 왜곡이 심할수록 가치가 올라간다)
///
/// <b>설치형</b>(GDD 8-4) — 들고 다니는 아이템이 아니라 본부에 놓인 고정 단말이다.
/// 설치 상태·표시 토글·미설치 상호작용 차단은 <see cref="InstallableItem"/>이 전부 담당하므로
/// 이 클래스에는 남아 있지 않다. 여기서는 <b>메시지 전송</b>만 다룬다.
///
/// 서버 권위 — 전송은 오너가 아닌 아무 플레이어나 할 수 있으므로(씬에 놓인 서버 소유 오브젝트)
/// 요청 RPC는 Everyone 권한이며, 서버가 길이·설치 여부를 재검증한 뒤 전 피어에 뿌린다 (#55).
/// </summary>
public class SignalDecoder : InstallableItem
{
    /// <summary>전송 가능한 최대 글자 수. 서버가 이 길이로 자른다 — 클라가 보낸 문자열은 신뢰할 수 없다.</summary>
    public const int k_maxMessageLength = 20;

    [Header("수신 표시")]
    [Tooltip("받은 메시지를 화면에 유지하는 시간(초)")]
    [SerializeField]
    private float m_displaySeconds = 8f;

    [Tooltip("수신 문구 형식 — UITable/signal.received ({0}에 받은 메시지가 들어간다)")]
    [SerializeField]
    private LocalizedString m_receivedFormat;

    /// <summary>
    /// E 상호작용 본체 — 베이스 <see cref="InstallableItem.Interact"/>가 미설치를 걸러낸 뒤 부른다
    /// (설치된 상태에서만 도달). 입력창을 연다.
    /// PlayerInteractor는 오너 클라에서만 돌므로(오너 외 비활성) 이 호출도 상호작용한
    /// 본인의 클라이언트에서만 일어난다 = 입력창은 로컬로만 열린다.
    /// </summary>
    protected override void OnInteract(GameObject interactor)
    {
        // 입력창은 HUD와 함께 로컬 플레이어 화면에 있다 — 씬에 UI 매니저·HUD가 없는 구성(단독 Play 등)이면
        // 패널을 못 찾고 무동작한다.
        if (App.UI.Current != null && App.UI.Current.TryGetPanel(out SignalInputPanel panel))
            panel.Open(this, interactor);
        else
            Debug.LogWarning("신호 해석기: 입력창 패널을 찾지 못해 열 수 없다", this);
    }

    // ---- 전송 ----

    // 이름에 주의: SendMessage는 UnityEngine.Component의 멤버라 그 이름을 쓰면 CS0108로 숨겨진다.
    // Assets/csc.rsp가 CS0108을 에러로 승격하므로(아키텍처 리팩토링 #245) 반드시 다른 이름을 쓸 것.
    /// <summary>입력창이 확정한 메시지를 팀 전원에게 보낸다. 보낸 사람의 클라이언트에서 호출된다.</summary>
    public void SendSignal(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        // 서버(호스트)·오프라인은 로컬로 즉시 처리
        if (!IsSpawned || IsServer)
        {
            ServerBroadcast(message);
            return;
        }

        RequestBroadcastRpc(message);
    }

    // InvokePermission = Everyone을 명시한다 — 이 단말은 씬에 놓인 서버 소유 오브젝트라
    // 어떤 플레이어도 오너가 아니다. 기본값(오너 전용)이면 아무도 전송할 수 없다.
    // (InstallableItem 문서가 파생에 요구하는 사항)
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void RequestBroadcastRpc(string message)
    {
        ServerBroadcast(message);
    }

    // 서버 검증 후 전파 — 클라가 보낸 문자열은 길이도 내용도 신뢰할 수 없다.
    private void ServerBroadcast(string message)
    {
        if (IsSpawned && !IsServer)
            return;

        // 미설치 단말로는 보낼 수 없다 (클라 게이트는 신뢰 불가이므로 재검증)
        if (!IsInstalled)
            return;

        string trimmed = message.Trim();
        if (trimmed.Length == 0)
            return;

        // 신뢰 경계 — 길이를 서버가 자른다. 안 자르면 임의 길이 문자열이 그대로 전 피어에 퍼지고
        // RPC 크기 한도를 넘기면 전송 자체가 깨진다.
        if (trimmed.Length > k_maxMessageLength)
            trimmed = trimmed.Substring(0, k_maxMessageLength);

        if (!IsSpawned)
        {
            ShowLocal(trimmed); // 오프라인 Play 테스트
            return;
        }

        BroadcastRpc(trimmed);
    }

    [Rpc(SendTo.Everyone)]
    private void BroadcastRpc(string message) => ShowLocal(message);

    // 받은 본문은 플레이어가 친 글이라 번역 대상이 아니다 — 형식 문구만 테이블에서 오고
    // 본문은 Smart String 인자({0})로 끼워 넣는다. 인자를 먼저 넣어야 구독 시점의
    // 첫 발화부터 올바른 문장이 나온다.
    private void ShowLocal(string message)
    {
        Debug.Log($"[신호 해석기] {message}");

        m_receivedFormat.Arguments = new object[] { message };
        App.UI.SignalMessage?.Show(m_receivedFormat, m_displaySeconds);
    }
}
