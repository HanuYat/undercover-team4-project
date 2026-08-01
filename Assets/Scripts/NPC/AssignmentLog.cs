using System.Text;
using UnityEngine;

/// <summary>
/// 라운드 시작 배정 결과의 진단 로그 — 스캔 UI(#39) 전까지 배정이 맞는지 콘솔로 확인하는 용도.
///
/// <b>⚠ 데모 빌드 전 제거 대상.</b> 누가 진범이고 누가 위조범인지가 그대로 찍히므로 정답이 노출된다.
/// 지우려면 이 파일과 <see cref="CriminalAssigner"/>의 호출 3줄(생성·Add·Flush)만 지우면 된다 —
/// 그러라고 따로 뺀 것이다. 예전에는 배정 루프 한가운데에 3중 삼항 연산자로 박혀 있어
/// 걷어내려면 루프를 헤집어야 했다.
///
/// 배정된 값(공개 여부·반응·현상금)은 <see cref="CitizenIdentity"/>에서 직접 읽는다 —
/// 로그를 위해 호출부가 값을 따로 들고 있을 필요가 없도록.
/// </summary>
public sealed class AssignmentLog
{
    private readonly StringBuilder m_builder = new StringBuilder();
    private int m_forgerCount;

    public AssignmentLog(int npcCount, int suspectCount, int revealCount)
    {
        m_builder.AppendLine(
            $"시민 프로필 배정 완료 ({npcCount}명, 예비 용의자 {suspectCount}명 중 {revealCount}명 공개):"
        );
    }

    /// <summary>시민 한 명분을 기록한다. 배정이 <see cref="CitizenIdentity"/>에 반영된 뒤 호출할 것.</summary>
    /// <param name="isSuspect">예비 용의자 풀에 들었는가 — 공개 여부(IsCriminal)와 별개다.</param>
    /// <param name="isForger">위조범인가 — 신원에는 서버 전용 플래그로만 남아 조회할 수 없어 받는다.</param>
    /// <param name="forgedSymbol">위조 축이 문양인가(false면 이름). <see cref="CitizenProfileFactory.ApplyForgery"/>의 반환값.</param>
    /// <param name="realSymbolIndex">위조 전 진짜 문양 index — 문양 위조 표시에만 쓴다.</param>
    public void Add(
        CitizenIdentity identity,
        bool isSuspect,
        bool isForger,
        bool forgedSymbol,
        int realSymbolIndex
    )
    {
        CitizenProfile profile = identity.Profile;
        if (profile == null)
        {
            return;
        }

        if (isForger)
        {
            m_forgerCount++;
        }

        // 범인 표시는 정답이 노출된다. 반응은 미끼 행동(#78) 빈도 확인용으로 함께 남긴다.
        string roleTag = identity.IsCriminal ? $"  ← 수배 공개 ({identity.Reaction})"
            : isSuspect ? $"  ← 예비 용의자 · 미공개 ({identity.Reaction})"
            : identity.Reaction != ReactionType.Compliant ? $"  (미끼: {identity.Reaction})"
            : "";

        // 위조 시 어느 축이 오염됐는지 함께 남겨 본부 대조 확인에 쓴다
        string forgeryTag = !isForger ? ""
            : forgedSymbol ? $"  [위조: 문양 {realSymbolIndex}→{profile.m_symbolIndexView}]"
            : $"  [위조: {profile.CitizenName}→{profile.m_nameView}]";

        string bountyTag = identity.Bounty > 0 ? $"  [현상금 {identity.Bounty}원]" : "";

        m_builder.AppendLine(
            $"  {profile.CitizenName} | {profile.m_typeView} | {profile.m_factionView}{roleTag}{forgeryTag}{bountyTag}"
        );
    }

    /// <summary>요약 줄을 붙이고 콘솔에 한 번에 출력한다.</summary>
    public void Flush(int totalAssignedBounty)
    {
        m_builder.AppendLine(
            $"  → 배정 현상금 총합 {totalAssignedBounty}원 (돌발 이벤트 수익 별도) · 위조범 {m_forgerCount}명"
        );
        Debug.Log(m_builder.ToString());
    }
}
