using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 몽타주 포트레이트 — 무채색 베이스 두상 위에 공개된 축만 얹어 그린 그림 몽타주. (#607)
///
/// 글 몽타주와 같은 계약을 그림으로 옮긴 것이다: 말하지 않은 축은 그리지 않는다.
/// 안 그려진 자리가 '없음'이 아니라 '미상'으로 읽혀야 하므로 베이스는 사진이 아니라 덜 그린 몽타주처럼 둔다.
/// 그림이 말하는 것이 공개 축 딱 그만큼이라 몽타주 부합 인원 k(AppearanceAssigner)의 보장도 그대로 성립한다.
///
/// 겹침 순서는 자식 순서(sibling index)로 정한다 — 베이스 → 머리 → 수염 → 모자 → 안경.
/// </summary>
public class MontagePortraitView : MonoBehaviour
{
    [Header("레이어 (뒤에서 앞 순서로 배치할 것)")]
    [SerializeField] private Image m_baseImage;      // 살 실루엣 — 피부색을 칠하는 자리
    [SerializeField] private Image m_faceImage;      // 이목구비 — 피부색과 무관해 칠하지 않는다
    [SerializeField] private Image m_hairImage;
    [SerializeField] private Image m_facialHairImage;
    [SerializeField] private Image m_headwearImage;
    [SerializeField] private Image m_eyewearImage;

    [Tooltip("공개되지 않은 색 축에 쓰는 표시. 색이 아니라 농도로 말해야 한다 — 무채색으로만 두면 은발(0.76,0.78,0.82)과 구분되지 않아 미공개가 실제 축 값 하나를 사칭하게 된다. 반투명이면 어느 색 값과도 겹치지 않는다")]
    [SerializeField] private Color m_unknownTint = new Color(0.72f, 0.72f, 0.74f, 0.35f);

    public void Bind(in AppearanceProfile profile, RevealedAxisSet revealedAxes, AppearanceDatabase database)
    {
        if (database == null || database.MontageBase == null)
        {
            Clear();
            return;
        }

        SetLayer(m_baseImage, database.MontageBase, TintOf(AppearanceAxis.SkinColor, profile, revealedAxes, database));
        SetLayer(m_faceImage, database.MontageFace, Color.white);
        BindHair(profile, revealedAxes, database);
        BindPropAxis(m_facialHairImage, AppearanceAxis.FacialHair, profile, revealedAxes, database);
        BindPropAxis(m_headwearImage, AppearanceAxis.Headwear, profile, revealedAxes, database);
        BindPropAxis(m_eyewearImage, AppearanceAxis.Eyewear, profile, revealedAxes, database);
    }

    /// <summary>머리는 스타일 축과 색 축이 한 레이어를 나눠 쓴다 — 스타일이 미공개인데 색만 공개면
    /// 색을 얹을 자리가 없으므로 형태 미상 머리를 대신 깐다.</summary>
    private void BindHair(in AppearanceProfile profile, RevealedAxisSet revealedAxes, AppearanceDatabase database)
    {
        Sprite sprite;
        if (revealedAxes.Contains(AppearanceAxis.HairStyle))
            sprite = database.GetOption(AppearanceAxis.HairStyle, profile.HairStyleIndex)?.MontageLayer;
        else if (revealedAxes.Contains(AppearanceAxis.HairColor))
            sprite = database.MontageUnknownHair;
        else
            sprite = null;

        SetLayer(m_hairImage, sprite, TintOf(AppearanceAxis.HairColor, profile, revealedAxes, database));
    }

    // 색을 입히지 않는다 — 수염·모자·안경은 색 자체가 몽타주 축이 아니고, 레이어를 실제 프롭 색 그대로
    // 구워 두기 때문이다(노랑·검정 고글을 단색으로 칠하면 화면과 어긋난다).
    private void BindPropAxis(
        Image image,
        AppearanceAxis axis,
        in AppearanceProfile profile,
        RevealedAxisSet revealedAxes,
        AppearanceDatabase database
    )
    {
        if (!revealedAxes.Contains(axis))
        {
            SetLayer(image, null, Color.white);
            return;
        }

        SetLayer(image, database.GetOption(axis, profile.GetIndex(axis))?.MontageLayer, Color.white);
    }

    private Color TintOf(
        AppearanceAxis colorAxis,
        in AppearanceProfile profile,
        RevealedAxisSet revealedAxes,
        AppearanceDatabase database
    )
    {
        if (!revealedAxes.Contains(colorAxis))
            return m_unknownTint;

        AppearanceDatabase.AppearanceOption option = database.GetOption(colorAxis, profile.GetIndex(colorAxis));
        return option != null ? option.Color : m_unknownTint;
    }

    private void Clear()
    {
        SetLayer(m_baseImage, null, Color.white);
        SetLayer(m_faceImage, null, Color.white);
        SetLayer(m_hairImage, null, Color.white);
        SetLayer(m_facialHairImage, null, Color.white);
        SetLayer(m_headwearImage, null, Color.white);
        SetLayer(m_eyewearImage, null, Color.white);
    }

    private static void SetLayer(Image image, Sprite sprite, Color tint)
    {
        if (image == null)
            return;

        image.sprite = sprite;
        image.color = tint;
        image.enabled = sprite != null;
    }
}
