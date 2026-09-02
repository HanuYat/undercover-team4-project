using System.Collections.Generic;
using System.Text;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// [디버그/검증 전용] 스폰된 NPC의 외형 프로필과 몽타주 부합 여부를 눈으로 확인한다. (#221/#222 검증용)
///
/// 확인해 주는 것:
/// - <b>화면 외형</b>(IAppearanceProfileSource.Profile)과 <b>정답 외형</b>(CitizenIdentity.Appearance)을
///   6축 표시 이름으로 나열한다. 둘이 어긋나면(=시각 조립 오류) 경고를 띄운다.
///   Appearance는 서버 전용이라 <b>호스트에서만</b> 채워진다(클라이언트에선 미표시).
/// - 이 NPC의 화면 외형으로 몽타주를 다시 만들어 보여 준다(BuildMontageText) — 실제 몽타주와 눈으로 대조용.
/// - 각 범인의 공개 축(AppearanceAssigner.CriminalRevealedAxes) 기준으로 이 NPC가 그 몽타주에 부합하는지 표시한다.
///
/// 게임 로직에 전혀 관여하지 않는 읽기 전용 뷰다. 표시 로직(Scene 라벨)은 에디터에서만 동작하므로
/// 릴리스 빌드에는 영향이 없다. 검증이 끝나면 NPC 프리팹/오브젝트에서 이 컴포넌트만 떼면 된다.
/// </summary>
public class NpcProfileDebugView : MonoBehaviour
{
    [Tooltip("Scene 뷰에서 머리 위에 요약 라벨을 그린다")]
    [SerializeField] private bool m_drawSceneLabel = true;

    [Tooltip("라벨을 그릴 높이 오프셋(m)")]
    [SerializeField] private float m_labelHeight = 2.2f;

    // 같은 GameObject의 소스 컴포넌트 — 디버그 용도라 매번 조회해도 무방하나 캐시해 둔다.
    private CitizenIdentity m_identity;
    private IAppearanceProfileSource m_appearanceSource;

    private CitizenIdentity Identity => m_identity != null ? m_identity : (m_identity = GetComponent<CitizenIdentity>());
    private IAppearanceProfileSource AppearanceSource =>
        m_appearanceSource ?? (m_appearanceSource = GetComponent<IAppearanceProfileSource>());

    // 배정기·DB는 플레이 중에만 유효 — App은 순수 C# 싱글톤이라 접근 자체는 안전하나 필드는 null일 수 있다.
    private static AppearanceAssigner Assigner => Application.isPlaying ? App.Game.Appearance : null;
    private static AppearanceDatabase Database
    {
        get
        {
            AppearanceAssigner assigner = Assigner;
            return assigner != null ? assigner.Database : null;
        }
    }

    /// <summary>디버그 뷰 한 번치 결과 — 인스펙터와 Scene 라벨이 공유한다.</summary>
    public struct Report
    {
        public bool HasVisual;          // 화면 외형이 배정됨
        public string VisualLine;       // 화면 외형 6축
        public bool TruthKnown;         // 정답 외형을 알 수 있음(호스트 + 배정 완료)
        public string TruthLine;        // 정답 외형 6축
        public bool Consistent;         // 화면 == 정답 (TruthKnown일 때만 의미)
        public string RevealedAxesLine; // 공개 축 이름들
        public string VisualMontage;    // 화면 외형으로 만든 몽타주 텍스트
        public bool IsCriminal;         // 서버 전용 값(호스트에서만 true 가능)
        public List<MontageMatch> Montages; // 실제 범인 몽타주별 부합 여부
        public string Warning;          // null이면 이상 없음
    }

    public struct MontageMatch
    {
        public int Index;       // 범인 인덱스(1-base 표시용은 +1)
        public string Text;     // 몽타주 텍스트
        public bool Matches;    // 이 NPC의 화면 외형이 공개 축에서 이 몽타주와 일치
    }

    /// <summary>현재 상태를 한 번에 수집한다. 인스펙터·Scene 라벨이 이 결과를 표시한다.</summary>
    public Report BuildReport()
    {
        var report = new Report { Montages = new List<MontageMatch>() };

        AppearanceProfile visual = AppearanceProfile.Unassigned;
        if (AppearanceSource != null)
            visual = AppearanceSource.Profile;
        report.HasVisual = visual.IsAssigned;
        report.VisualLine = FormatProfile(visual);

        CitizenIdentity identity = Identity;
        if (identity != null)
        {
            report.IsCriminal = identity.IsCriminal;

            AppearanceProfile truth = identity.Appearance;
            report.TruthKnown = truth.IsAssigned;
            if (report.TruthKnown)
            {
                report.TruthLine = FormatProfile(truth);
                report.Consistent = report.HasVisual && visual.Equals(truth);
                if (report.HasVisual && !report.Consistent)
                    report.Warning = "화면 외형과 정답 외형이 다릅니다 — 시각 조립 오류 가능";
            }
        }

        AppearanceAssigner assigner = Assigner;
        AppearanceDatabase db = Database;
        if (assigner != null && db != null)
        {
            // 공개 축은 범인마다 다르다 — 요약 줄은 합집합으로, 부합 판정은 각 범인의 축으로 한다
            IReadOnlyList<AppearanceProfile> criminals = assigner.CriminalProfiles;
            IReadOnlyList<RevealedAxisSet> criminalAxes = assigner.CriminalRevealedAxes;

            RevealedAxisSet union = default;
            foreach (RevealedAxisSet axes in criminalAxes)
            {
                foreach (AppearanceAxis axis in axes)
                    union.Add(axis);
            }
            report.RevealedAxesLine = union.IsEmpty ? "(없음)" : $"{FormatRevealedAxes(union)} (범인별 합집합)";

            if (report.HasVisual && !union.IsEmpty)
                report.VisualMontage = db.BuildMontageText(visual, union);

            // 몽타주 문장은 보관되지 않는다 — 범인 프로필과 공개 축으로 여기서 다시 만든다 (#497)
            for (int i = 0; i < criminals.Count; i++)
            {
                RevealedAxisSet axes = i < criminalAxes.Count ? criminalAxes[i] : default;
                bool matches = report.HasVisual && !axes.IsEmpty && visual.MatchesOn(criminals[i], axes);
                report.Montages.Add(new MontageMatch
                {
                    Index = i,
                    Text = db.BuildMontageText(criminals[i], axes),
                    Matches = matches,
                });
            }
        }

        return report;
    }

    /// <summary>6축을 "축이름: 값(인덱스)" 형태로 이어 붙인다. DB가 없으면 인덱스만.</summary>
    public string FormatProfile(in AppearanceProfile profile)
    {
        if (!profile.IsAssigned)
            return "(미배정)";

        AppearanceDatabase db = Database;
        var builder = new StringBuilder();
        for (int i = 0; i < AppearanceProfile.k_axisCount; i++)
        {
            var axis = (AppearanceAxis)i;
            int index = profile.GetIndex(axis);
            string axisName = AppearanceDatabase.GetAxisName(axis);
            string valueName = db != null
                ? AppearanceDatabase.GetOptionName(db.GetOption(axis, index))
                : index.ToString();

            if (builder.Length > 0)
                builder.Append(" / ");
            builder.Append(axisName).Append(": ").Append(valueName);
        }
        return builder.ToString();
    }

    private string FormatRevealedAxes(RevealedAxisSet axes)
    {
        if (axes.IsEmpty)
            return "(없음)";

        var builder = new StringBuilder();
        foreach (AppearanceAxis axis in axes)
        {
            if (builder.Length > 0)
                builder.Append(", ");
            builder.Append(AppearanceDatabase.GetAxisName(axis));
        }
        return builder.ToString();
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!m_drawSceneLabel || !Application.isPlaying)
            return;

        Report report = BuildReport();
        if (!report.HasVisual)
            return;

        var label = new StringBuilder();
        label.Append(name);
        if (report.IsCriminal) label.Append(" [범인]");
        label.Append('\n').Append(report.VisualLine);
        if (!string.IsNullOrEmpty(report.Warning))
            label.Append("\n⚠ ").Append(report.Warning);
        else if (report.TruthKnown && report.Consistent)
            label.Append("\n외형=정답 ✓");

        Handles.Label(transform.position + Vector3.up * m_labelHeight, label.ToString());
    }
#endif
}
