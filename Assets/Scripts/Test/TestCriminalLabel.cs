using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// [테스트용] 범인으로 배정된 NPC들 머리 위에 "범인" 라벨을 띄운다. (다수 진범 지원, #127)
/// CriminalAssigner.OnCriminalAssigned를 구독해 런타임에 TestWorldLabel을 부착한다.
/// 정답이 노출되므로 데모 빌드 전에 제거할 것.
/// (범인 배정은 서버/오프라인에서만 발생하므로 이 라벨도 그쪽에서만 보인다)
/// </summary>
public class TestCriminalLabel : MonoBehaviour
{
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;

    [SerializeField] private string m_text = "범인";
    [SerializeField] private Color m_color = Color.red;

    private void OnEnable()
    {
        if (Assigner != null)
            Assigner.OnCriminalAssigned += HandleCriminalAssigned;
    }

    private void Start()
    {
        // 늦게 활성화돼 배정 이벤트를 이미 놓친 경우 보정
        if (Assigner != null && Assigner.CriminalNpcs.Count > 0)
            HandleCriminalAssigned(Assigner.CriminalNpcs);
    }

    private void OnDisable()
    {
        if (Assigner != null)
            Assigner.OnCriminalAssigned -= HandleCriminalAssigned;
    }

    private void HandleCriminalAssigned(IReadOnlyList<NpcController> criminals)
    {
        foreach (NpcController criminal in criminals)
        {
            if (criminal == null)
                continue;

            // 이미 붙어 있으면 중복 부착 방지 (Start 보정과 이벤트가 겹칠 수 있음)
            if (criminal.GetComponent<TestWorldLabel>() != null)
                continue;

            TestWorldLabel label = criminal.gameObject.AddComponent<TestWorldLabel>();
            label.Configure(m_text, m_color);
        }
    }
}
