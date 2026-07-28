using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// 서버 권위 채널링(홀드형 3초류 대기)의 공통 생명주기를 담당하는 컴포지션 헬퍼. (#109)
/// Scanner·PlayerEscorter·PlayerReviver가 각자 구현하던 CTS 소유·재진입 가드·
/// try/catch(OperationCanceledException)/finally 정리 골격이 사실상 동일해 여기로 추출했다.
/// NetworkBehaviour를 상속하지 않는 plain 클래스 — 각 사용처가 필드로 들고(컴포지션) 사용한다.
/// (ItemBase 상속 방식은 대상 3곳 중 2곳이 아이템이 아니라서 채택하지 않음 — 이슈 논의 참고)
/// </summary>
public class ServerChannel
{
    /// <summary>채널링 종료 사유.</summary>
    public enum Result
    {
        /// <summary>제한 시간을 다 채우고 정상 완료.</summary>
        Completed,

        /// <summary>Cancel() 호출로 도중에 취소됨(홀드 뗌 등).</summary>
        Canceled,

        /// <summary>keepAlive가 false를 반환해 도중에 중단됨(대상이 사거리를 벗어남 등).</summary>
        OutOfRange,
    }

    private CancellationTokenSource m_cts;

    /// <summary>지금 채널링 진행 중인지 여부 — 재진입(중복 시작) 가드용.</summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// seconds초 동안 채널링을 진행한다. 동시에 하나만 실행 가능 — 호출부가 IsActive로 먼저 가드해야 한다.
    /// keepAlive를 넘기면 매 프레임 호출해 false가 되는 즉시 OutOfRange로 중단한다(거리 이탈 등, Scanner/Escorter 관례).
    /// keepAlive를 생략하면 중간 검사 없이 단일 Delay로 대기한다(PlayerReviver 관례 — 완료 시점에만 호출부가 검사).
    ///
    /// onProgressPoint를 넘기면 진행률이 progressPoint(0~1)를 넘는 프레임에 <b>딱 한 번</b> 호출한다
    /// (#400 스캔 반응). 채널을 둘로 쪼개지 않는 이유는 그 사이에 IsActive가 풀려 재진입·취소 유실이
    /// 생기기 때문. keepAlive 없는 단일 Delay 경로에서는 중간 지점을 잡을 수 없어 무시된다.
    /// </summary>
    public async UniTask<Result> RunAsync(
        float seconds,
        Func<bool> keepAlive = null,
        float progressPoint = -1f,
        Action onProgressPoint = null)
    {
        IsActive = true;
        m_cts = new CancellationTokenSource();

        try
        {
            if (keepAlive == null)
            {
                await UniTask.Delay(TimeSpan.FromSeconds(seconds), cancellationToken: m_cts.Token);
                return Result.Completed;
            }

            // 단일 Delay가 아닌 프레임 루프 — 채널링 도중 조건 이탈(거리 등)을 즉시 실패시킨다 (#91).
            // 뗌 취소는 Yield의 토큰 예외(catch)로, 조건 이탈은 return으로 — 종료 사유가 구분된다.
            bool pointFired = onProgressPoint == null || progressPoint < 0f;
            float pointSeconds = seconds * Mathf.Clamp01(progressPoint);

            float elapsed = 0f;
            while (elapsed < seconds)
            {
                if (!keepAlive())
                {
                    return Result.OutOfRange;
                }

                // 중간 지점 통과 — 1회만. 여기서 대상이 도주해 다음 프레임 keepAlive가 끊기는 것도 의도다 (#400)
                if (!pointFired && elapsed >= pointSeconds)
                {
                    pointFired = true;
                    onProgressPoint();
                }

                await UniTask.Yield(PlayerLoopTiming.Update, m_cts.Token);
                elapsed += Time.deltaTime;
            }

            return Result.Completed;
        }
        catch (OperationCanceledException)
        {
            return Result.Canceled;
        }
        finally
        {
            IsActive = false;
            m_cts?.Dispose();
            m_cts = null;
        }
    }

    /// <summary>진행 중인 채널링을 취소한다. 채널링 중이 아니면 무동작.</summary>
    public void Cancel() => m_cts?.Cancel();

    /// <summary>보유 컴포넌트의 OnDestroy에서 호출 — 취소 후 CTS를 정리한다.</summary>
    public void Dispose()
    {
        m_cts?.Cancel();
        m_cts?.Dispose();
        m_cts = null;
    }
}
