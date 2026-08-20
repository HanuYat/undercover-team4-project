// 수배 조건 — 시체 인계가 허용되는가. 0번(DeadOrAlive)이 기본값이라 배정 누락이 조용히 AliveOnly로 새지 않는다. (#766)
[LocalizedEnum("HqTable", "Hq.Wanted.Condition.")]
public enum WantedCondition
{
    DeadOrAlive,
    AliveOnly,
}
