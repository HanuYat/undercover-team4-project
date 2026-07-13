using UnityEngine;

/// <summary>
/// [테스트용] 대상 오브젝트 머리 위에 항상 카메라를 바라보는 월드 공간 텍스트를 띄운다.
/// 드롭존·범인 등 디버그 표시용이며, 정답이 노출되므로 데모 빌드 전에 제거할 것.
/// 폰트 에셋 의존을 피하려고 Unity 내장 폰트(LegacyRuntime.ttf)를 쓰는 TextMesh로 구현했다.
/// </summary>
public class TestWorldLabel : MonoBehaviour
{
    [Header("표시 텍스트")]
    [SerializeField] private string m_text = "LABEL";

    [SerializeField] private Color m_color = Color.yellow;

    [Header("대상 피벗 기준 높이(m)")]
    [SerializeField] private float m_heightOffset = 2.5f;

    [Header("글자 크기")]
    [SerializeField] private float m_characterSize = 0.2f;
    [SerializeField] private int m_fontSize = 64;

    private TextMesh m_textMesh;
    private Transform m_cam;

    private void Start()
    {
        BuildLabel();
    }

    // 런타임에 텍스트/색을 지정해 붙일 때 사용 (예: 범인 배정 시). Start 전이면 필드만 세팅되고 Start에서 반영된다.
    public void Configure(string text, Color color)
    {
        m_text = text;
        m_color = color;
        if (m_textMesh != null)
        {
            m_textMesh.text = text;
            m_textMesh.color = color;
        }
    }

    private void BuildLabel()
    {
        // 텍스트를 자식 오브젝트로 만들어 대상 위쪽에 띄운다
        var go = new GameObject("TestWorldLabel_Text");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, m_heightOffset, 0f);

        m_textMesh = go.AddComponent<TextMesh>();
        m_textMesh.text = m_text;
        m_textMesh.color = m_color;
        m_textMesh.anchor = TextAnchor.LowerCenter;
        m_textMesh.alignment = TextAlignment.Center;
        m_textMesh.characterSize = m_characterSize;
        m_textMesh.fontSize = m_fontSize;

        // 내장 폰트를 직접 지정 — 폰트 미지정 시 아무것도 렌더되지 않는 것을 방지
        Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (font != null)
        {
            m_textMesh.font = font;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        }
    }

    private void LateUpdate()
    {
        if (m_textMesh == null)
            return;

        // 메인 카메라(네트워크 플레이어 카메라 포함)를 매 프레임 다시 찾는다 — 스폰 타이밍 대응
        if (m_cam == null && Camera.main != null)
            m_cam = Camera.main.transform;
        if (m_cam == null)
            return;

        // 카메라와 같은 방향으로 정렬해 텍스트가 항상 정면으로 읽히게 한다 (빌보드)
        m_textMesh.transform.rotation = m_cam.rotation;
    }
}
