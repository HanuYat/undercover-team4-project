using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// [테스트용] 예비 용의자 머리 위에 라벨을 띄운다 — 공개된 수배와 미공개 대기분을 구분한다. (#127 · #102)
/// CriminalAssigner.OnCriminalAssigned를 구독해 런타임에 TestWorldLabel을 부착한다.
/// 정답이 노출되므로 데모 빌드 전에 제거할 것.
/// (범인 배정은 서버/오프라인에서만 발생하므로 이 라벨도 그쪽에서만 보인다)
/// </summary>
public class TestCriminalLabel : MonoBehaviour
{
    private CriminalAssigner Assigner => App.Game.CriminalAssigner;

    [SerializeField] private string m_text = "수배";
    [SerializeField] private Color m_color = Color.red;

    [Header("미공개 예비 용의자 (#102)")]
    [Tooltip("제보 전화로 아직 공개되지 않은 대기 중 용의자에게 붙는 라벨")]
    [SerializeField] private string m_pendingText = "예비(미공개)";
    [SerializeField] private Color m_pendingColor = Color.gray;

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
            Apply(label, criminal);
        }
    }

    // 제보 전화 승격(#102)은 배정 이벤트 밖에서 일어나므로, 라벨은 폴링으로 따라간다.
    // 테스트 전용 스크립트이고 대상이 예비 풀 크기(보통 3명)뿐이라 비용을 따질 규모가 아니다.
    private void Update()
    {
        if (Assigner == null)
            return;

        IReadOnlyList<NpcController> criminals = Assigner.CriminalNpcs;
        for (int i = 0; i < criminals.Count; i++)
        {
            if (criminals[i] == null)
                continue;

            TestWorldLabel label = criminals[i].GetComponent<TestWorldLabel>();
            if (label != null)
                Apply(label, criminals[i]);
        }
    }

    // 공개된 수배인지 대기 중인 예비 용의자인지로 문구·색을 가른다 (#102)
    private void Apply(TestWorldLabel label, NpcController npc)
    {
        CitizenIdentity identity = npc.GetComponent<CitizenIdentity>();
        bool revealed = identity != null && identity.IsCriminal;
        label.Configure(revealed ? m_text : m_pendingText, revealed ? m_color : m_pendingColor);
    }
}
