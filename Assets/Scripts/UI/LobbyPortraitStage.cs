using UnityEngine;

/// <summary>
/// 로비 명단 카드에 넣을 <b>캐릭터 얼굴</b>을 만든다 — 무대에 캐릭터를 세우고 머리만 잡아
/// RenderTexture로 굽는다. (#598)
///
/// <b>왜 실시간 렌더인가</b> — 미리 구운 그림 한 장이면 지금은 충분하지만(플레이어 외형이 아직
/// 전원 같다), 로봇 색 커스터마이징(#432)이 들어오면 사람마다 얼굴이 갈린다. 그때 그림을 다시
/// 굽는 대신 무대에 세우는 캐릭터만 갈아 끼우면 되게 지금부터 렌더 경로로 둔다.
///
/// <b>지금은 한 장을 모두가 나눠 쓴다</b> — 외형 데이터가 개인별로 없어서다. 개인별로 갈리는
/// 순간 이 클래스가 사람 수만큼 무대를 세우고 <see cref="LobbyRosterRowView.SetPortrait"/>에
/// 각자의 텍스처를 넘기면 된다. 카드 쪽 배선은 이미 1인 1장 기준으로 돼 있다.
///
/// 무대는 씬 밖 먼 곳에 세운다 — 전용 레이어를 새로 파지 않으려는 것이다. 카메라 far clip이
/// 짧아 주변에 아무것도 안 잡히고, 로비에는 3D 씬 자체가 없어 가릴 것도 없다.
///
/// 실행 순서를 패널보다 앞에 둔다 — LobbyRosterPanel이 첫 그리기에서 <see cref="Portrait"/>를
/// 읽어가는데, 기본 순서로 두면 그때 아직 안 구워져 얼굴 없는 카드가 그려진다. 혼자 있으면
/// 명단이 바뀔 일이 없어 다시 그리지 않으므로 그대로 굳는다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIContent)]
public class LobbyPortraitStage : MonoBehaviour
{
    [Header("무대")]
    [Tooltip("얼굴을 딸 캐릭터 모델 — 플레이어와 같은 것을 쓴다 (SM_Gen_Chr_Robot_01)")]
    [SerializeField] private GameObject m_characterPrefab;

    [Tooltip("무대를 세울 위치 — 씬의 다른 것과 겹치지 않게 멀리 둔다")]
    [SerializeField] private Vector3 m_stageOrigin = new Vector3(0f, -500f, 0f);

    [Tooltip("머리 본 이름 — 이 본을 정면에서 잡는다")]
    [SerializeField] private string m_headBoneName = "Head";

    [Header("카메라")]
    [Tooltip("머리에서 이만큼(m) 떨어져서 잡는다 — 작을수록 얼굴이 꽉 찬다")]
    [SerializeField] private float m_distance = 0.55f;

    [Tooltip("머리 본 기준 위아래 보정(m) — 본이 목에 있으면 살짝 올려야 얼굴이 가운데 온다")]
    [SerializeField] private float m_heightOffset = 0.06f;

    [Tooltip("시야각 — 작을수록 왜곡이 적다")]
    [Range(10f, 60f)]
    [SerializeField] private float m_fieldOfView = 28f;

    [Tooltip("초상 텍스처 크기(px) — 카드 사진 창과 같은 세로 비율로 굽는다. 정사각으로 구워 창에 늘리면 얼굴이 눌린다")]
    [SerializeField] private Vector2Int m_textureSize = new Vector2Int(256, 348);

    private RenderTexture m_texture;
    private Camera m_camera;

    /// <summary>구워진 얼굴 — 카드가 이걸 받아 표시한다. 준비 전이면 null.</summary>
    public Texture Portrait => m_texture;

    private void Awake()
    {
        if (m_characterPrefab == null)
        {
            Debug.LogWarning($"[{nameof(LobbyPortraitStage)}] 캐릭터 모델이 연결되지 않았습니다 — 얼굴 없이 진행합니다.", this);
            return;
        }

        BuildStage();
    }

    private void OnDestroy()
    {
        // RenderTexture는 GC 대상이 아니다 — 씬을 오갈 때마다 쌓이지 않게 직접 놓는다.
        if (m_texture == null)
            return;

        m_texture.Release();
        Destroy(m_texture);
        m_texture = null;
    }

    private void BuildStage()
    {
        var stage = new GameObject("PortraitStage");
        stage.transform.SetParent(transform, false);
        stage.transform.position = m_stageOrigin;

        GameObject model = Instantiate(m_characterPrefab, m_stageOrigin, Quaternion.identity, stage.transform);
        model.name = "PortraitCharacter";

        Transform head = FindDeep(model.transform, m_headBoneName);
        if (head == null)
        {
            Debug.LogWarning($"[{nameof(LobbyPortraitStage)}] '{m_headBoneName}' 본을 찾지 못했습니다 — 모델 원점을 대신 잡습니다.", this);
            head = model.transform;
        }

        m_texture = new RenderTexture(m_textureSize.x, m_textureSize.y, 16, RenderTextureFormat.ARGB32)
        {
            name = "LobbyPortrait",
            antiAliasing = 2,
        };

        var camGo = new GameObject("PortraitCamera");
        camGo.transform.SetParent(stage.transform, false);
        m_camera = camGo.AddComponent<Camera>();
        m_camera.clearFlags = CameraClearFlags.SolidColor;
        m_camera.backgroundColor = new Color(0f, 0f, 0f, 0f); // 카드 종이색이 비치게 투명 배경
        m_camera.fieldOfView = m_fieldOfView;
        m_camera.nearClipPlane = 0.05f;
        m_camera.farClipPlane = 5f; // 무대 밖은 아무것도 안 잡힌다
        m_camera.targetTexture = m_texture;

        // 캐릭터 정면(모델 forward) 쪽에서 얼굴 높이로 바라본다
        Vector3 focus = head.position + Vector3.up * m_heightOffset;
        camGo.transform.position = focus + model.transform.forward * m_distance;
        camGo.transform.LookAt(focus);

        // 모델도 카메라도 움직이지 않으므로 한 번만 그린다 — 켜 둔 채로 두면 매 프레임 다시 그린다.
        m_camera.enabled = false;
        m_camera.Render();
    }

    // 본 이름은 계층 어디에 있을지 모른다 — 이름으로 깊이 우선 탐색한다.
    private static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name)
            return root;

        for (int i = 0; i < root.childCount; i++)
        {
            Transform hit = FindDeep(root.GetChild(i), name);
            if (hit != null)
                return hit;
        }

        return null;
    }
}
