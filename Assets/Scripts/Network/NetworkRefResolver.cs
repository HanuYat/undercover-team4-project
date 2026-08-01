using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Unity.Netcode;

/// <summary>참조 해석 대기의 종료 사유 — 호출부가 "중단"과 "상한 초과"를 구분해 처리해야 해서 나눈다.</summary>
public enum EResolveResult
{
    /// <summary>전부 해석됐다 — 참조를 그대로 써도 된다.</summary>
    Resolved,

    /// <summary>상한 프레임까지 기다렸지만 일부가 안 풀렸다. 부분 반영할지는 호출부가 정한다.</summary>
    TimedOut,

    /// <summary>대기 중 호출부가 유효하지 않게 됐다(디스폰·세대 교체 등) — 결과를 쓰면 안 된다.</summary>
    Aborted,
}

/// <summary>
/// <see cref="NetworkObjectReference"/>가 해석될 때까지 기다린다. (#144, #370)
/// 갓 스폰된 NetworkObject는 이 클라에 아직 도착하지 않았을 수 있다 — 스폰 메시지와 그 객체를 가리키는
/// RPC/NetworkVariable의 도착 순서가 경쟁하기 때문. 특히 원격 클라의 초기 지급에서 참조가 즉시
/// 해석되지 않아 표시·재구성이 통째로 누락된다. 몇 프레임 기다렸다 다시 보면 대부분 풀린다.
///
/// 이 대기 정책(상한 프레임 수·중단 조건)은 원래 PlayerLoadout과 PlayerHeldItemView가 각자
/// 구현하고 주석으로 "서로 같은 방침"이라고 적어 두고 있었다 — 그 약속을 코드로 옮긴 것이다.
/// </summary>
public static class NetworkRefResolver
{
    /// <summary>
    /// 해석 대기 상한(프레임). 넘으면 포기한다 — 영영 안 오는 참조(이미 디스폰된 객체 등)에
    /// 무한정 매달리지 않기 위한 안전판이라 넉넉하게 잡는다.
    /// </summary>
    public const int k_maxWaitFrames = 120;

    /// <summary>참조 하나가 해석될 때까지 기다린다.</summary>
    /// <param name="itemRef">기다릴 참조.</param>
    /// <param name="isCallerAlive">
    /// 매 프레임 확인하는 계속 조건 — false가 되면 <see cref="EResolveResult.Aborted"/>로 끝낸다.
    /// 호출부의 파괴 여부(<c>this != null &amp;&amp; IsSpawned</c>)와 세대 검사를 여기 담는다.
    /// </param>
    public static UniTask<EResolveResult> WaitAsync(
        NetworkObjectReference itemRef,
        Func<bool> isCallerAlive
    ) => WaitCoreAsync(() => itemRef.TryGet(out _), isCallerAlive);

    /// <summary>참조 여러 개가 <b>전부</b> 해석될 때까지 기다린다.</summary>
    /// <inheritdoc cref="WaitAsync(NetworkObjectReference, Func{bool})" path="/param[@name='isCallerAlive']"/>
    public static UniTask<EResolveResult> WaitAsync(
        IReadOnlyList<NetworkObjectReference> itemRefs,
        Func<bool> isCallerAlive
    ) => WaitCoreAsync(() => AllResolved(itemRefs), isCallerAlive);

    private static async UniTask<EResolveResult> WaitCoreAsync(
        Func<bool> isResolved,
        Func<bool> isCallerAlive
    )
    {
        // 이미 풀려 있으면 한 프레임도 쉬지 않는다 — 대부분의 호출이 여기서 즉시 끝난다.
        for (int frame = 0; frame < k_maxWaitFrames && !isResolved(); frame++)
        {
            await UniTask.Yield(PlayerLoopTiming.Update);

            if (!isCallerAlive())
            {
                return EResolveResult.Aborted;
            }
        }

        return isResolved() ? EResolveResult.Resolved : EResolveResult.TimedOut;
    }

    private static bool AllResolved(IReadOnlyList<NetworkObjectReference> itemRefs)
    {
        for (int i = 0; i < itemRefs.Count; i++)
        {
            if (!itemRefs[i].TryGet(out _))
            {
                return false;
            }
        }

        return true;
    }
}
