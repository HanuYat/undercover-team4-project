using UnityEngine;

/// <summary>
/// 페널티 NPC 식별 — 머리 위 앵그리 마크(💢) 표시. (#280)
/// 오검거 페널티에 얽힌 시민(수용·추격·호송, #277~#279)을 다른 시민과 구분한다.
///
/// NpcAnimationDriver가 동기화된 상태 전이(모든 피어에서 발생, #56)에 맞춰 SetVisible을 호출하므로
/// 씬·프리팹 배선 없이 모든 클라이언트에서 같은 시점에 켜지고 꺼진다.
///
/// 표시는 90도로 교차한 스프라이트 쿼드 2장 — 빌보드(카메라 지향)를 쓰지 않는 이유는 화면이 하나가
/// 아니기 때문이다: 플레이어 1인칭과 본부 CCTV(다른 카메라)가 동시에 렌더하므로, 한 카메라를 향해
/// 돌리면 다른 카메라에서 옆면(안 보임)이 된다. 교차 쿼드는 어느 방향에서든 읽힌다
/// (Sprites/Default는 양면 렌더라 뒷면도 보인다). 스케일 펄스로 원거리 가독성을 더한다.
///
/// 스프라이트는 런타임 절차 생성(붉은 4분할 링 — 만화식 분노 표시의 근사) — 아트 에셋이 생기면
/// GetSprite만 교체하면 된다.
/// </summary>
public class NpcPenaltyMark : MonoBehaviour
{
    private const float k_height = 2.35f; // NPC 발밑 기준 표시 높이(m) — 머리 위
    private const float k_baseScale = 0.55f; // 마크 기본 크기(m)
    private const float k_pulseAmount = 0.12f; // 펄스 진폭(크기 비율)
    private const float k_pulseSpeed = 5f; // 펄스 속도(rad/s)

    private static Sprite s_sprite; // 절차 생성 스프라이트 — 전 NPC가 공유

    /// <summary>
    /// 마크 표시/숨김 — NpcAnimationDriver(모든 피어)가 페널티 상태 여부에 맞춰 호출한다.
    /// 처음 표시할 때 마크 오브젝트를 지연 생성한다 — 페널티에 얽힌 적 없는 NPC에는 아무것도 안 만든다.
    /// </summary>
    public static void SetVisible(Component npc, bool visible)
    {
        if (npc == null)
            return;

        NpcPenaltyMark mark = npc.GetComponentInChildren<NpcPenaltyMark>(true);
        if (mark == null)
        {
            if (!visible)
                return;
            mark = Create(npc.transform);
        }

        mark.gameObject.SetActive(visible);
    }

    private static NpcPenaltyMark Create(Transform parent)
    {
        var root = new GameObject("PenaltyMark");
        root.transform.SetParent(parent, false);
        root.transform.localPosition = new Vector3(0f, k_height, 0f);
        root.transform.localScale = Vector3.one * k_baseScale;

        // 교차 쿼드 2장 — 0도/90도. 양면 렌더라 4방향 모두에서 보인다
        for (int i = 0; i < 2; i++)
        {
            var quad = new GameObject(i == 0 ? "Quad0" : "Quad90");
            quad.transform.SetParent(root.transform, false);
            quad.transform.localRotation = Quaternion.Euler(0f, i * 90f, 0f);
            quad.AddComponent<SpriteRenderer>().sprite = GetSprite();
        }

        return root.AddComponent<NpcPenaltyMark>();
    }

    private void Update()
    {
        // 콩닥콩닥 펄스 — 군중 속·CCTV 원경에서도 눈에 띄게
        float scale = k_baseScale * (1f + Mathf.Sin(Time.time * k_pulseSpeed) * k_pulseAmount);
        transform.localScale = new Vector3(scale, scale, scale);
    }

    // 붉은 4분할 링을 절차 생성한다 — 대각선 방향 네 조각(만화 분노 마크 근사). [임시 — 아트 교체 지점]
    private static Sprite GetSprite()
    {
        if (s_sprite != null)
            return s_sprite;

        const int size = 128;
        const float innerRadius = 30f;
        const float outerRadius = 44f;
        const float segmentHalfAngle = 33f; // 조각 반각(도) — 대각선 중심 기준, 사이 틈이 십자로 남는다
        const float soft = 1.5f; // 가장자리 안티에일리어싱 폭(px)

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        var red = new Color(0.92f, 0.18f, 0.14f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x - (size - 1) * 0.5f;
                float dy = y - (size - 1) * 0.5f;
                float r = Mathf.Sqrt(dx * dx + dy * dy);

                // 대각선(45도 계열) 중심에서의 각도 편차 — 90도 주기라 네 조각이 한 식으로 잡힌다
                float angle = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
                float offCenter = Mathf.Abs(Mathf.Repeat(angle, 90f) - 45f);

                float radial =
                    Mathf.InverseLerp(innerRadius - soft, innerRadius + soft, r)
                    * (1f - Mathf.InverseLerp(outerRadius - soft, outerRadius + soft, r));
                float angular =
                    1f - Mathf.InverseLerp(segmentHalfAngle - 2f, segmentHalfAngle + 2f, offCenter);

                float alpha = radial * angular;
                pixels[y * size + x] = new Color32(
                    (byte)(red.r * 255f),
                    (byte)(red.g * 255f),
                    (byte)(red.b * 255f),
                    (byte)(alpha * 255f)
                );
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, true); // 더 안 고칠 텍스처 — CPU 사본을 버려 메모리 절약

        // pixelsPerUnit = size → 스프라이트가 1m — 실제 크기는 루트 localScale(k_baseScale)로 조절
        s_sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        return s_sprite;
    }
}
