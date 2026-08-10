using UnityEngine;

/// <summary>
/// <b>같은 뼈대를 쓰는 두 리그 사이에서 포즈만 옮긴다.</b> (#571 사망 전용 모델 분리)
///
/// 살아있는 몸과 시체가 별도 모델로 갈리면서 생긴 유일한 이음새다. 사망하는 순간 살아있는 리그가
/// 잡고 있던 포즈를 시체에 넘겨야 화면이 이어지고, 부활할 때는 반대로 시체의 정착 포즈를 살아있는
/// 리그로 되돌려야 기상 블렌드의 출발점이 생긴다.
///
/// <b>받는 쪽(시체)에서 몰고 가며 이름으로 짝짓는다.</b> 이게 이 유틸의 핵심 결정이고, 앞서
/// <c>GetComponentsInChildren</c> 배열을 인덱스로 맞추다 <b>조용히 실패했다</b>:
/// 장착 아이템 모델이 살아있는 손 본 밑(<c>Hand_R/HeldItemAnchor</c>)에 인스턴스화되므로
/// <b>살아있는 서브트리에는 뼈가 아닌 자식이 런타임에 늘어난다.</b> 그러면 두 배열의 길이가 갈려
/// 아무것도 복사되지 않고, 시체는 프리팹의 바인드 포즈(첫 사망)나 직전에 누운 포즈(두 번째 이후)로
/// 나타난다. 둘 다 실제로 밟았다.
///
/// 시체 리그는 <b>런타임에 자식이 늘지 않는다</b>(아무도 여기에 뭘 붙이지 않는다) — 그래서 시체를
/// 기준으로 돌면 살아있는 쪽에 무엇이 더 붙어도 영향이 없다. 아이템 모델은 짝이 없어 방문조차 되지 않는다.
///
/// <b>로컬 값만 만진다.</b> 두 리그의 부모가 서로 다른 자리에 있으므로 월드 값을 옮기면 자세가
/// 통째로 어긋난다 (<see cref="RagdollRig.CaptureLocalPose"/>의 같은 사정).
/// </summary>
public static class RagdollPose
{
    /// <summary>
    /// <paramref name="from"/> 이하의 포즈를 <paramref name="to"/> 이하로 복사한다.
    ///
    /// 회전만이 아니라 <b>로컬 위치까지</b> 옮긴다. 뼈 길이는 두 리그에서 같으니 대부분 같은 값을
    /// 다시 쓰는 셈이지만, 골반만 예외로 골라낼 필요가 없어져 코드가 단순해진다.
    /// </summary>
    /// <returns>
    /// 실제로 값을 쓴 뼈 수. 호출부는 이 값을 기대치와 대조할 것 — <b>0이나 부족한 값이 조용히
    /// 넘어가면 위 실패가 그대로 재현된다.</b>
    /// </returns>
    public static int Copy(Transform from, Transform to)
    {
        if (from == null || to == null)
            return 0;

        to.localPosition = from.localPosition;
        to.localRotation = from.localRotation;

        int copied = 1;
        for (int i = 0; i < to.childCount; i++)
        {
            Transform target = to.GetChild(i);
            Transform source = FindChild(from, target.name);
            if (source != null)
                copied += Copy(source, target);
        }

        return copied;
    }

    // 직속 자식만 이름으로 찾는다 — Transform.Find는 경로를 해석하므로 이름에 '/'가 없다는 전제가
    // 붙고, 여기서는 그냥 자식 순회가 더 정직하다. 형제끼리 이름이 겹치는 뼈는 Synty 리그에 없다.
    private static Transform FindChild(Transform parent, string name)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == name)
                return child;
        }

        return null;
    }
}
