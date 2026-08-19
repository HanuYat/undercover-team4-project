using System.Collections.Generic;
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
    [SerializeField] private Image m_hairImage;
    [SerializeField] private Image m_facialHairImage;
    [SerializeField] private Image m_headwearImage;
    [SerializeField] private Image m_eyewearImage;

    [Tooltip("공개되지 않은 색 축에 쓰는 표시. 색이 아니라 농도로 말해야 한다 — 무채색으로만 두면 은발(0.76,0.78,0.82)과 구분되지 않아 미공개가 실제 축 값 하나를 사칭하게 된다. 반투명이면 어느 색 값과도 겹치지 않는다")]
    [SerializeField] private Color m_unknownTint = new Color(0.72f, 0.72f, 0.74f, 0.35f);

    // 화질 저하로 만든 런타임 스프라이트 — 슬롯(Image)별로 하나씩만 들고, 다시 바인딩되거나
    // 뷰가 파괴될 때 여기서 Destroy한다. 원본(굽기 툴이 만든 자산)은 절대 여기 들어가지 않는다.
    private readonly Dictionary<Image, Sprite> m_runtimeSprites = new Dictionary<Image, Sprite>();

    public void Bind(in AppearanceProfile profile, RevealedAxisSet revealedAxes, AppearanceDatabase database)
    {
        if (database == null || database.MontageBase == null)
        {
            Clear();
            return;
        }

        MontageClarityStep clarity = ClarityStepOf(database);

        BindBase(profile, revealedAxes, database, clarity);
        BindHair(profile, revealedAxes, database, clarity);
        BindPropAxis(m_facialHairImage, AppearanceAxis.FacialHair, profile, revealedAxes, database, clarity);
        BindPropAxis(m_headwearImage, AppearanceAxis.Headwear, profile, revealedAxes, database, clarity);
        BindPropAxis(m_eyewearImage, AppearanceAxis.Eyewear, profile, revealedAxes, database, clarity);
    }

    private void OnDestroy()
    {
        foreach (Sprite sprite in m_runtimeSprites.Values)
            DestroyRuntimeSprite(sprite);
        m_runtimeSprites.Clear();
    }

    // 표가 없으면 원본 화질 그대로 — 표를 안 붙인 씬은 지금과 똑같이 돌아야 한다 (RoundQuotaTable과 같은 규약).
    private static MontageClarityStep ClarityStepOf(AppearanceDatabase database)
    {
        var noDegrade = new MontageClarityStep { PixelSize = int.MaxValue, Fade = 0f };
        if (database.ClarityTable == null)
            return noDegrade;

        RoundProgress progress = App.Game.RoundProgress;
        int round = progress != null ? progress.Current : RoundProgress.k_firstRound;
        return database.ClarityTable.GetStep(round, noDegrade);
    }

    /// <summary>
    /// 피부색이 미공개면 두상을 칠하지 않고 흰색으로 둔다.
    ///
    /// 다른 축과 달리 '안 그리는 것'으로 미상을 말할 수 없다 — 바닥은 늘 깔리고 늘 어떤 색이든 띤다.
    /// 반투명은 뒤의 어두운 패널이 비쳐 어두운 피부를 사칭하고, 어둡게 칠하면 그 위에 실제 프롭 색
    /// 그대로 얹히는 수염·안경(대부분 어둡다)이 묻힌다. 그래서 밝기를 지키는 흰색으로 두고,
    /// 미상이라는 말 자체는 몽타주 글이 한다 (AppearanceDatabase.BuildMontageText).
    /// </summary>
    private void BindBase(
        in AppearanceProfile profile,
        RevealedAxisSet revealedAxes,
        AppearanceDatabase database,
        in MontageClarityStep clarity
    )
    {
        Color tint = revealedAxes.Contains(AppearanceAxis.SkinColor)
            ? TintOf(AppearanceAxis.SkinColor, profile, revealedAxes, database)
            : Color.white;

        SetLayer(m_baseImage, database.MontageBase, tint, clarity);
    }

    /// <summary>머리는 스타일 축과 색 축이 한 레이어를 나눠 쓴다 — 스타일이 미공개면 색 공개 여부와
    /// 무관하게 형태 미상 머리를 깐다. 비워 두면 빈 정수리가 '미상'이 아니라 대머리로 읽힌다.
    /// 대머리는 스타일 축이 공개됐을 때 레이어가 없는 것으로 말한다 (안 그리는 것이 곧 그 값).</summary>
    private void BindHair(
        in AppearanceProfile profile,
        RevealedAxisSet revealedAxes,
        AppearanceDatabase database,
        in MontageClarityStep clarity
    )
    {
        Sprite sprite = revealedAxes.Contains(AppearanceAxis.HairStyle)
            ? database.GetOption(AppearanceAxis.HairStyle, profile.HairStyleIndex)?.MontageLayer
            : database.MontageUnknownHair;

        SetLayer(m_hairImage, sprite, TintOf(AppearanceAxis.HairColor, profile, revealedAxes, database), clarity);
    }

    // 색을 입히지 않는다 — 수염·모자·안경은 색 자체가 몽타주 축이 아니고, 레이어를 실제 프롭 색 그대로
    // 구워 두기 때문이다(노랑·검정 고글을 단색으로 칠하면 화면과 어긋난다).
    private void BindPropAxis(
        Image image,
        AppearanceAxis axis,
        in AppearanceProfile profile,
        RevealedAxisSet revealedAxes,
        AppearanceDatabase database,
        in MontageClarityStep clarity
    )
    {
        if (!revealedAxes.Contains(axis))
        {
            SetLayer(image, null, Color.white, clarity);
            return;
        }

        SetLayer(image, database.GetOption(axis, profile.GetIndex(axis))?.MontageLayer, Color.white, clarity);
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
        var noDegrade = default(MontageClarityStep);
        SetLayer(m_baseImage, null, Color.white, noDegrade);
        SetLayer(m_hairImage, null, Color.white, noDegrade);
        SetLayer(m_facialHairImage, null, Color.white, noDegrade);
        SetLayer(m_headwearImage, null, Color.white, noDegrade);
        SetLayer(m_eyewearImage, null, Color.white, noDegrade);
    }

    private void SetLayer(Image image, Sprite sprite, Color tint, in MontageClarityStep clarity)
    {
        if (image == null)
            return;

        Sprite degraded = Degrade(image, sprite, clarity);
        image.sprite = degraded;
        image.color = tint;
        image.enabled = degraded != null;
    }

    /// <summary>
    /// 원본 자산은 절대 건드리지 않는다 — 매 바인딩마다 화질 저하 버전을 새로 만들거나 원본을 그대로
    /// 돌려주고, 이 슬롯에 이전에 만들어 둔 런타임 버전이 있으면 여기서 Destroy한다.
    /// </summary>
    private Sprite Degrade(Image slot, Sprite source, in MontageClarityStep clarity)
    {
        if (m_runtimeSprites.TryGetValue(slot, out Sprite previous))
        {
            DestroyRuntimeSprite(previous);
            m_runtimeSprites.Remove(slot);
        }

        if (source == null)
            return null;

        int srcSize = source.texture.width;
        if (clarity.PixelSize >= srcSize && clarity.Fade <= 0f)
            return source;

        Color[] degraded = MontageDegrader.Apply(source.texture.GetPixels(), srcSize, clarity);
        var texture = new Texture2D(srcSize, srcSize, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        texture.SetPixels(degraded);
        texture.Apply();

        Sprite result = Sprite.Create(texture, new Rect(0, 0, srcSize, srcSize), new Vector2(0.5f, 0.5f), source.pixelsPerUnit);
        m_runtimeSprites[slot] = result;
        return result;
    }

    private static void DestroyRuntimeSprite(Sprite sprite)
    {
        if (sprite == null)
            return;

        Destroy(sprite.texture);
        Destroy(sprite);
    }
}
