using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 로비 명단 카드에 넣을 <b>캐릭터 얼굴</b>을 만든다 — 무대에 캐릭터를 세우고 머리만 잡아
/// RenderTexture로 굽는다. (#598)
///
/// <b>왜 실시간 렌더인가</b> — 미리 구운 그림 한 장이면 외형이 전원 같을 때는 충분하지만,
/// 로봇 색 커스터마이징(#432)이 들어오며 사람마다 얼굴이 갈렸다. 무대에 세운 모델의 색만 갈아
/// 끼워 다시 굽는다.
///
/// <b>그림은 사람이 아니라 색 단위로 굽는다</b> — 같은 색을 고른 두 사람은 같은 얼굴이라
/// 두 번 구울 이유가 없다. 6인 방에서도 실제로 굽는 횟수는 서로 다른 색의 수만큼이다.
/// 카드 쪽 배선은 처음부터 1인 1장 기준이라 그대로다.
///
/// 무대는 씬 밖 먼 곳에 세운다 — 전용 레이어를 새로 파지 않으려는 것이다. 카메라 far clip이
/// 짧아 주변에 아무것도 안 잡히고, 로비에는 3D 씬 자체가 없어 가릴 것도 없다.
///
/// 실행 순서를 패널보다 앞에 둔다 — LobbyRosterPanel이 첫 그리기에서 얼굴을 받아 가는데,
/// 기본 순서로 두면 그때 무대가 아직 없어 얼굴 없는 카드가 그려진다. 텍스처 자체는 요청 즉시
/// 만들어 건네고 그림만 나중에 채우므로, 굽는 시점을 늦춰도 카드 배선은 그대로다.
///
/// <b>Awake에서 굽지 않는다</b> — URP가 첫 프레임을 그리기 전에는 조명/환경 상수와 스카이박스
/// 환경광·기본 반사가 아직 준비되지 않아, 그때 구우면 빌드에서 실행할 때마다 얼굴 밝기와 색이
/// 달라진다(에디터는 이미 그려 둔 상태라 티가 안 난다). 첫 프레임이 끝난 뒤에 굽는다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIContent)]
public class LobbyPortraitStage : MonoBehaviour
{
    [Header("무대")]
    [Tooltip("얼굴을 딸 캐릭터 모델 — 플레이어와 같은 것을 쓴다 (SM_Gen_Chr_Robot_01)")]
    [SerializeField] private GameObject m_characterPrefab;

    [Tooltip("색 팔레트 — Player 프리팹의 PlayerCosmetics와 같은 에셋을 물릴 것 (#432)")]
    [SerializeField] private PlayerColorPalette m_palette;

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

    // 색 인덱스 → 그 색으로 구운 얼굴. 카드에는 만들자마자 건네고 그림은 나중에 채운다.
    private readonly Dictionary<int, RenderTexture> m_portraits = new Dictionary<int, RenderTexture>();

    // 아직 그림이 안 채워졌거나 다시 구워야 하는 색들
    private readonly HashSet<int> m_pending = new HashSet<int>();

    private Camera m_camera;
    private BodyTint m_tint;
    private bool m_baking;
    private bool m_lit; // 첫 프레임(조명 확정)을 지났는가

    // 로비를 떠나도 살려 두는 얼굴 — 게임 씬에서 다시 구우면 맵 조명을 타 어둡게 나오므로,
    // 로비에서 구운 것을 세션 내내 그대로 쓴다. 다음 로비 방문에서 새로 구울 때 놓아 준다. (#720)
    //
    // RenderTexture가 아니라 Texture2D인 것은 그림을 씬 너머로 들고 가야 해서다 — RenderTexture는
    // 오브젝트만 남고 GPU 쪽은 놓여, 게임 씬에서는 IsCreated()가 false인 빈 칸이 그려졌다.
    private static Texture2D s_sessionPortrait;

    /// <summary>내 색으로 구운 얼굴 — 색을 고르는 화면이 자기 얼굴을 보여줄 때 쓴다. (#432)</summary>
    public Texture Portrait => GetPortrait(GameSettings.PlayerColorIndex);

    /// <summary>로비에서 구워 세션 동안 유지되는 <b>내</b> 얼굴 — 게임 씬 UI가 이걸 읽는다. 준비 전이면 null. (#720)</summary>
    public static Texture SessionPortrait => s_sessionPortrait;

    /// <summary>
    /// 그 색으로 구운 얼굴 — 카드가 이걸 받아 표시한다. 무대가 없으면 null.
    /// 처음 묻는 색이면 빈 텍스처를 먼저 건네고 그림은 곧 채운다 — 카드가 기다리지 않게 하려는 것이다.
    /// </summary>
    public Texture GetPortrait(int colorIndex)
    {
        if (m_camera == null)
            return null;

        if (m_portraits.TryGetValue(colorIndex, out RenderTexture cached))
            return cached;

        RenderTexture texture = CreateTexture(colorIndex);
        m_portraits[colorIndex] = texture;
        m_pending.Add(colorIndex);
        BakeAsync().Forget();

        return texture;
    }

    private void Awake()
    {
        if (m_characterPrefab == null)
        {
            Debug.LogWarning($"[{nameof(LobbyPortraitStage)}] 캐릭터 모델이 연결되지 않았습니다 — 얼굴 없이 진행합니다.", this);
            return;
        }

        BuildStage();
    }

    // 내 색이 바뀌면 세션용 얼굴을 다시 챙긴다 — 게임 씬 상황판이 예전 색을 들고 가지 않게. (#432)
    private void OnEnable() => GameSettings.OnPlayerColorChanged += HandleOwnColorChanged;

    private void OnDisable() => GameSettings.OnPlayerColorChanged -= HandleOwnColorChanged;

    private void OnDestroy()
    {
        // RenderTexture는 GC 대상이 아니다 — 씬을 오갈 때마다 쌓이지 않게 직접 놓는다.
        // 세션용 얼굴(s_sessionPortrait)은 별도 Texture2D라 여기서 놓는 것과 상관이 없다. (#720)
        foreach (RenderTexture texture in m_portraits.Values)
        {
            if (texture == null)
                continue;

            texture.Release();
            Destroy(texture);
        }

        m_portraits.Clear();
    }

    private void HandleOwnColorChanged(int colorIndex)
    {
        // 내 색 얼굴을 확보해 두면 굽기가 끝날 때 세션용으로 옮겨진다
        GetPortrait(colorIndex);
    }

    private void BuildStage()
    {
        var stage = new GameObject("PortraitStage");
        stage.transform.SetParent(transform, false);
        stage.transform.position = m_stageOrigin;

        GameObject model = Instantiate(m_characterPrefab, m_stageOrigin, Quaternion.identity, stage.transform);
        model.name = "PortraitCharacter";

        // 색은 BodyTint 하나에만 맡긴다 — 무대 모델도 게임 속 로봇과 같은 규칙이다 (#478/#432)
        m_tint = model.AddComponent<BodyTint>();

        Transform head = FindDeep(model.transform, m_headBoneName);
        if (head == null)
        {
            Debug.LogWarning($"[{nameof(LobbyPortraitStage)}] '{m_headBoneName}' 본을 찾지 못했습니다 — 모델 원점을 대신 잡습니다.", this);
            head = model.transform;
        }

        var camGo = new GameObject("PortraitCamera");
        camGo.transform.SetParent(stage.transform, false);
        m_camera = camGo.AddComponent<Camera>();
        m_camera.clearFlags = CameraClearFlags.SolidColor;
        m_camera.backgroundColor = new Color(0f, 0f, 0f, 0f); // 카드 종이색이 비치게 투명 배경
        m_camera.fieldOfView = m_fieldOfView;
        m_camera.nearClipPlane = 0.05f;
        m_camera.farClipPlane = 5f; // 무대 밖은 아무것도 안 잡힌다

        // 캐릭터 정면(모델 forward) 쪽에서 얼굴 높이로 바라본다
        Vector3 focus = head.position + Vector3.up * m_heightOffset;
        camGo.transform.position = focus + model.transform.forward * m_distance;
        camGo.transform.LookAt(focus);

        // 모델도 카메라도 움직이지 않으므로 필요할 때만 그린다 — 켜 둔 채로 두면 매 프레임 다시 그린다.
        m_camera.enabled = false;

        // 내 얼굴은 아무도 묻기 전에 챙겨 둔다 — 게임 씬으로 들고 갈 그림이라 로비에서 반드시 구워야 한다
        GetPortrait(GameSettings.PlayerColorIndex);
    }

    private RenderTexture CreateTexture(int colorIndex)
    {
        var texture = new RenderTexture(m_textureSize.x, m_textureSize.y, 16, RenderTextureFormat.ARGB32)
        {
            name = $"LobbyPortrait {colorIndex}",
            antiAliasing = 2,
        };

        // 굽기 전까지 카드에 걸릴 텍스처다 — 새 RenderTexture의 내용은 보장되지 않아 명시적으로 비운다
        texture.Create();
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = texture;
        GL.Clear(true, true, Color.clear);
        RenderTexture.active = prev;

        return texture;
    }

    // 조명이 확정된 뒤에 굽는다 (클래스 주석 참고). 여러 색이 한꺼번에 밀려도 굽기는 하나만 돈다 —
    // 명단이 바뀔 때마다 카드 전부가 얼굴을 다시 물어 오기 때문이다.
    private async UniTaskVoid BakeAsync()
    {
        if (m_baking)
            return;

        m_baking = true;
        CancellationToken token = this.GetCancellationTokenOnDestroy();

        try
        {
            // URP가 첫 프레임을 온전히 끝낼 때까지 기다린다 — 그래야 조명/환경 셰이더 상수가 채워져 있다
            await UniTask.WaitForEndOfFrame(token);

            if (!m_lit)
            {
                // 스카이박스 환경광과 기본 반사를 지금 확정한다 — 로드 직후엔 아직 갱신 전일 수 있다.
                // 갱신은 프레임 끝에 반영되므로 한 프레임 더 기다린 뒤 굽는다.
                DynamicGI.UpdateEnvironment();
                await UniTask.WaitForEndOfFrame(token);
                m_lit = true;
            }

            BakePending();
        }
        finally
        {
            m_baking = false;
        }
    }

    private void BakePending()
    {
        if (m_pending.Count == 0)
            return;

        foreach (int colorIndex in m_pending)
        {
            if (!m_portraits.TryGetValue(colorIndex, out RenderTexture texture) || texture == null)
                continue;

            if (m_tint != null && m_palette != null)
                m_tint.SetBase(m_palette.Get(colorIndex));

            m_camera.targetTexture = texture;
            RenderPortrait(texture);
        }

        m_pending.Clear();
        m_camera.targetTexture = null;

        CaptureSessionPortrait();
    }

    // 구운 그림을 씬 너머로 들고 갈 Texture2D로 옮겨 둔다 — RenderTexture는 씬을 넘기면 내용이
    // 날아가 게임 씬 상황판에 빈 칸이 뜬다. 옮기는 것은 내 색 하나뿐이다. (#720/#432)
    private void CaptureSessionPortrait()
    {
        if (!m_portraits.TryGetValue(GameSettings.PlayerColorIndex, out RenderTexture mine) || mine == null)
            return;

        // MSAA가 걸린 판은 그대로 읽을 수 없다 — 안티에일리어싱 없는 임시 판에 한 번 옮긴다.
        RenderTexture resolved = RenderTexture.GetTemporary(mine.width, mine.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(mine, resolved);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = resolved;

        // 직전 것은 새 그림이 나온 뒤에 놓는다 — 굽는 도중에 카드가 읽으면 얼굴이 한 번 비게 된다.
        Texture2D stale = s_sessionPortrait;

        s_sessionPortrait = new Texture2D(mine.width, mine.height, TextureFormat.ARGB32, false, false) { name = "SessionPortrait" };
        s_sessionPortrait.ReadPixels(new Rect(0f, 0f, mine.width, mine.height), 0, 0);
        s_sessionPortrait.Apply();

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(resolved);

        if (stale != null)
            Destroy(stale);
    }

    // SRP에서 Camera.Render()는 파이프라인 밖 경로다 — URP가 지원하는 렌더 요청을 먼저 쓴다.
    private void RenderPortrait(RenderTexture destination)
    {
        if (m_camera == null || destination == null)
            return;

        var request = new UniversalRenderPipeline.SingleCameraRequest { destination = destination };
        if (RenderPipeline.SupportsRenderRequest(m_camera, request))
            RenderPipeline.SubmitRenderRequest(m_camera, request);
        else
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
