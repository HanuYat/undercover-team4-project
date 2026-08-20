using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 서버 권위 채널링 상호작용의 공통 기반 — 채널링 게이지(#184)와 오너 판정 피드백(#91)을 제공한다.
/// 둘 다 "판정은 서버가 하되 그 행동을 시작한 오너의 화면에만 보여준다"는 같은 규칙을 따르므로,
/// 호스트 오너·오프라인은 로컬로 즉시 구동하고 원격 오너에게는 SendTo.Owner RPC로 전달한다.
/// (데디케이티드 서버 등 HUD가 없는 환경에선 App.UI.Gauge가 null이라 무동작 — 안전)
///
/// 아이템(ItemBase)·검거 허브(PlayerEscorter)·소생(PlayerReviver)이 이 기반을 공유한다 —
/// 여러 경로가 같은 피드백 코드를 복붙하던 것을 단일 출처로 모은 것이다.
/// </summary>
public abstract class ChanneledInteractionBehaviour : NetworkBehaviour
{
    // 서버 권위 경로를 직접 실행해도 되는 피어인지 — 서버(호스트)이거나 오프라인.
    // 스폰 전(오프라인)엣 IsServer 캐시가 아직 갱신되지 않아 false일 수 있으므로, 스폰 여부를 함께 본다.
    protected bool HasServerAuthority => !IsSpawned || IsServer;

    // ---- 채널링 게이지 (#184) ----

    /// <summary>
    /// 채널링을 하는 동안 오너에게 계속 들려줄 소리 — 기본은 없음. 필요한 하위만 재정의한다 (#483).
    ///
    /// 게이지와 같은 경로에 태우는 이유는 <b>시작과 끝이 이미 짝지어져 있기 때문</b>이다.
    /// 게이지 숨김은 완료·취소·거리이탈 어떤 종료에서도 반드시 불리므로(아래 <see cref="NotifyChannelGaugeEnd"/>),
    /// 소리를 여기 붙이면 "취소했는데 소리가 계속 난다"가 구조적으로 생기지 않는다.
    /// </summary>
    protected virtual EAudioClip ChannelLoopSound => EAudioClip.None;

    /// <summary>채널링 게이지 표시 — 오너 화면에. 서버·오프라인은 로컬, 원격 오너에겐 RPC.</summary>
    protected void NotifyChannelGaugeStart(float seconds) => NotifyChannelGaugeStart(seconds, 0f);

    /// <summary>
    /// 이미 진행 중인 것을 중간부터 이어 표시한다 — elapsed초 지난 상태로 시작. (#455)
    /// 아이템을 다시 장착했을 때 남은 충전을 보여주는 용도(Taser). 전체 시간과 경과 시간을 함께 넘기는
    /// 이유는 ChannelingGaugeUI.Show(seconds, elapsed) 문서에 있다.
    /// </summary>
    protected void NotifyChannelGaugeStart(float seconds, float elapsed)
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            ChannelGaugeStartRpc(seconds, elapsed, ChannelLoopSound);
            return;
        }
        ShowChannelFeedback(seconds, elapsed, ChannelLoopSound);
    }

    /// <summary>채널링 게이지 숨김 — 완료·취소·거리이탈 등 어떤 종료 경로에서도 반드시 호출.</summary>
    protected void NotifyChannelGaugeEnd()
    {
        if (IsSpawned && IsServer && !IsOwner)
        {
            ChannelGaugeEndRpc();
            return;
        }
        HideChannelFeedback();
    }

    [Rpc(SendTo.Owner)]
    private void ChannelGaugeStartRpc(float seconds, float elapsed, EAudioClip sound) =>
        ShowChannelFeedback(seconds, elapsed, sound);

    [Rpc(SendTo.Owner)]
    private void ChannelGaugeEndRpc() => HideChannelFeedback();

    // 소리를 RPC 인자로 실어 보내는 이유 — 원격 오너에서는 ChannelLoopSound를 그대로 읽어도 되지만,
    // 게이지와 소리가 같은 한 번의 결정에서 나와야 둘이 어긋날 여지가 없다.
    // owner로 this를 넘기는 이유는 ChannelingGaugeUI 문서 참고 — 아이템·구조·포박이 게이지 하나를
    // 공유하므로, 남의 게이지를 실수로 껐다 켰다 하지 않으려면 짝을 맞춰야 한다. (#725)
    private void ShowChannelFeedback(float seconds, float elapsed, EAudioClip sound)
    {
        App.UI.Gauge?.Show(seconds, elapsed, this);
        App.Sound?.PlayLoop2D(sound);
    }

    private void HideChannelFeedback()
    {
        App.UI.Gauge?.Hide(this);
        App.Sound?.StopLoop2D();
    }

    // ---- 오너 판정 피드백 (#91) ----

    /// <summary>
    /// 서버 판정 결과를 오너에게 알린다. 판정 로그는 서버에서 찍히므로 원격 클라 오너는 볼 수 없다 —
    /// 오너 콘솔에도 같은 로그를 전달한다. toast=true면 로그에 더해 오너 화면 토스트도 요청한다 (#309).
    /// 정식 UI 피드백(#65 계열)이 생기면 이 전달 경로를 확장한다.
    /// </summary>
    protected void NotifyOwner(string message, bool toast = false)
    {
        Debug.Log(message); // 서버(호스트)·오프라인 콘솔
        if (IsSpawned && IsServer && !IsOwner)
        {
            OwnerLogRpc(message, toast); // 원격 클라가 오너면 거기서 로그·토스트
            return;
        }
        if (toast)
            RaiseOwnerToast(message); // 호스트 오너·오프라인은 로컬 발행
    }

    [Rpc(SendTo.Owner)]
    private void OwnerLogRpc(string message, bool toast)
    {
        Debug.Log($"[서버 판정] {message}");
        if (toast)
            RaiseOwnerToast(message);
    }

    /// <summary>
    /// 오너 화면 토스트를 발행한다 — 토스트 채널을 가진 하위만 재정의한다(Scanner.OnScanFeedback).
    /// 기본은 무동작이라 toast를 쓰지 않는 하위는 콘솔 로그만 나간다.
    /// </summary>
    protected virtual void RaiseOwnerToast(string message) { }
}
