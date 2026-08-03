using Unity.Netcode;
using UnityEngine;

/// <summary>신호 해석기 — 본부에 설치되는 단말. 타이핑한 짧은 메시지를 팀 전원에게 보낸다. (#108)</summary>
[RequireComponent(typeof(SignalDecoderHud))]
public class SignalDecoder : InstallableItem
{
    /// <summary>전송 가능한 최대 글자 수. 서버가 이 길이로 자른다 — 클라가 보낸 문자열은 신뢰할 수 없다.</summary>
    public const int k_maxMessageLength = 20;

    private SignalDecoderHud m_hud;

    protected override void OnInstallableAwake()
    {
        m_hud = GetComponent<SignalDecoderHud>();
    }

    /// <summary>
    /// E 상호작용 본체 — 베이스 Interact가 미설치를 걸러낸 뒤 부른다(설치된 상태에서만 도달). 입력창을 연다.
    /// PlayerInteractor는 오너 클라에서만 돌므로(오너 외 비활성) 이 호출도 상호작용한
    /// 본인의 클라이언트에서만 일어난다 = 입력창은 로컬로만 열린다.
    /// </summary>
    protected override void OnInteract(GameObject interactor)
    {
        m_hud.Open(this, interactor);
    }

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

        // 신뢰 경계 — 길이를 서버가 자른다. 안 자르면 임의 길이 문자열이 그대로 전 피어에 퍼지고 RPC 크기 한도를 넘기면 전송 자체가 깨진다.
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

    private void ShowLocal(string message)
    {
        Debug.Log($"[신호 해석기] {message}");
        m_hud.ShowMessage(message);
    }
}
