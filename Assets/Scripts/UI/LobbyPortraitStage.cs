using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 로비 명단 카드에 넣을 <b>얼굴</b>을 만든다 — 무대에 캐릭터를 세우고 머리만 잡아 RenderTexture로 굽는다. (#598)
///
/// 그림은 사람이 아니라 <b>색 조합 단위</b>로 굽는다 (#432) — 같은 색을 고른 두 사람은 같은 얼굴이다.
/// 무대는 씬 밖 먼 곳에 세운다: 전용 레이어 없이도 카메라 far clip이 짧아 아무것도 안 잡힌다.
///
/// 실행 순서를 패널보다 앞에 둔다 — 카드가 첫 그리기에서 얼굴을 받아 간다. 텍스처는 요청 즉시
/// 건네고 그림만 나중에 채운다.
///
/// <b>Awake에서 굽지 않는다</b> — URP 첫 프레임 전에는 환경광·반사가 준비되지 않아 실행할 때마다
/// 밝기가 달라진다. 첫 프레임이 끝난 뒤에 굽는다.
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

    [Header("전신 미리보기 (#432)")]
    [Tooltip("발끝에서 이만큼(m) 뒤로 물러나 전신을 잡는다")]
    [SerializeField] private float m_bodyDistance = 3.4f;

    [Tooltip("카메라 높이(m) — 몸 가운데쯤")]
    [SerializeField] private float m_bodyHeight = 0.95f;

    [Range(10f, 60f)]
    [SerializeField] private float m_bodyFieldOfView = 32f;

    [SerializeField] private Vector2Int m_bodyTextureSize = new Vector2Int(384, 640);

    // 색 조합(PlayerColorSet.Key) → 그 색으로 구운 얼굴
    private readonly Dictionary<int, RenderTexture> m_portraits = new Dictionary<int, RenderTexture>();
    private readonly Dictionary<int, PlayerColorSet> m_requested = new Dictionary<int, PlayerColorSet>();
    private readonly HashSet<int> m_pending = new HashSet<int>(); // 아직 그림이 안 채워진 것
    private readonly HashSet<int> m_live = new HashSet<int>(); // 지금 쓰이는 조합 — 나머지는 굽고 나서 버린다
    private readonly List<int> m_stale = new List<int>();

    private Camera m_camera;
    private Camera m_bodyCamera;
    private RenderTexture m_bodyTexture;
    private bool m_bodyDirty = true; // 내 색이 바뀌면 다시 그린다
    private BodyTint m_tint;
    private readonly Color[] m_tintBuffer = new Color[3]; // 인덱스 = EBodyPart
    private bool m_baking;
    private bool m_lit; // 첫 프레임(조명 확정)을 지났는가

    // 로비를 떠나도 살려 두는 얼굴들 — 게임 씬에서 다시 구우면 맵 조명을 타 어둡게 나오므로,
    // 로비에서 구운 것을 세션 내내 쓴다. 다음 로비 방문에서 새로 구울 때 놓아 준다. (#720)
    // 열쇠는 색 조합(PlayerColorSet.Key) — 게임 씬 상황판이 각 플레이어 색으로 찾아 간다. (#432)
    //
    // RenderTexture가 아니라 Texture2D인 것은 씬 너머로 들고 가야 해서다 — RenderTexture는
    // 오브젝트만 남고 GPU 쪽은 놓여, 게임 씬에서는 빈 칸이 그려졌다.
    private static readonly Dictionary<int, Texture2D> s_sessionPortraits = new Dictionary<int, Texture2D>();

    /// <summary>내 색으로 구운 얼굴. (#432)</summary>
    public Texture Portrait => GetPortrait(PlayerColorSet.FromSettings());

    /// <summary>내 색으로 그린 전신 — 색 고르는 창이 쓴다. 무대가 없으면 null. (#432)</summary>
    public Texture BodyPreview
    {
        get
        {
            if (m_bodyCamera == null)
                return null;

            m_bodyDirty = true;
            BakeAsync().Forget();
            return m_bodyTexture;
        }
    }

    /// <summary>로비에서 구워 세션 동안 유지되는 <b>내</b> 얼굴. 준비 전이면 null. (#720)</summary>
    public static Texture SessionPortrait => GetSessionPortrait(PlayerColorSet.FromSettings());

    // 도메인 리로드를 끄면 지난 플레이의 (이미 파괴된) 텍스처 참조가 static에 남는다 —
    // Unity 가짜 null이라 받는 쪽 null 검사도 통과한다. 플레이 시작마다 비운다. (GameSettings.Load와 같은 이유)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetStatics() => s_sessionPortraits.Clear();

    /// <summary>그 색 조합으로 로비에서 구워 둔 얼굴 — 게임 씬 UI가 읽는다. 없으면 null. (#432)</summary>
    public static Texture GetSessionPortrait(PlayerColorSet colors) =>
        s_sessionPortraits.TryGetValue(colors.Key, out Texture2D portrait) ? portrait : null;

    /// <summary>
    /// 그 색 조합으로 구운 얼굴 — 무대가 없으면 null. 처음 묻는 조합이면 빈 텍스처를 먼저 건네고
    /// 그림은 곧 채운다.
    /// </summary>
    public Texture GetPortrait(PlayerColorSet colors)
    {
        if (m_camera == null)
            return null;

        int key = colors.Key;
        if (m_portraits.TryGetValue(key, out RenderTexture cached))
            return cached;

        RenderTexture texture = CreateTexture(key);
        m_portraits[key] = texture;
        m_requested[key] = colors;
        m_pending.Add(key);
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

    // 내 색이 바뀌면 세션용 얼굴을 다시 챙긴다 — 게임 씬 상황판이 예전 색을 들고 가지 않게 (#432)
    private void OnEnable() => GameSettings.OnPlayerColorChanged += HandleOwnColorChanged;

    private void OnDisable() => GameSettings.OnPlayerColorChanged -= HandleOwnColorChanged;

    private void OnDestroy()
    {
        // RenderTexture는 GC 대상이 아니다 — 씬을 오갈 때마다 쌓이지 않게 직접 놓는다.
        // 세션용 얼굴은 별도 Texture2D라 여기서 놓는 것과 상관이 없다 (#720)
        foreach (RenderTexture texture in m_portraits.Values)
        {
            if (texture == null)
                continue;

            texture.Release();
            Destroy(texture);
        }

        m_portraits.Clear();

        if (m_bodyTexture == null)
            return;

        m_bodyTexture.Release();
        Destroy(m_bodyTexture);
        m_bodyTexture = null;
    }

    // 내 색 얼굴을 확보해 두면 굽기가 끝날 때 세션용으로 옮겨진다. 전신도 같이 다시 그린다.
    private void HandleOwnColorChanged(EBodyPart _)
    {
        m_bodyDirty = true;
        GetPortrait(PlayerColorSet.FromSettings());
    }

    private void BuildStage()
    {
        // 지난 로비 방문에서 구운 것은 여기서 놓는다 — 세션이 이어지는 동안만 살아 있으면 된다
        foreach (Texture2D stale in s_sessionPortraits.Values)
        {
            if (stale != null)
                Destroy(stale);
        }

        s_sessionPortraits.Clear();

        var stage = new GameObject("PortraitStage");
        stage.transform.SetParent(transform, false);
        stage.transform.position = m_stageOrigin;

        GameObject model = Instantiate(m_characterPrefab, m_stageOrigin, Quaternion.identity, stage.transform);
        model.name = "PortraitCharacter";

        m_tint = model.AddComponent<BodyTint>(); // 색은 BodyTint 하나에만 맡긴다 (#478)

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

        m_bodyTexture = CreateTexture(m_bodyTextureSize, "LobbyBodyPreview");

        var bodyGo = new GameObject("BodyCamera");
        bodyGo.transform.SetParent(stage.transform, false);
        m_bodyCamera = bodyGo.AddComponent<Camera>();
        m_bodyCamera.clearFlags = CameraClearFlags.SolidColor;
        m_bodyCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        m_bodyCamera.fieldOfView = m_bodyFieldOfView;
        m_bodyCamera.nearClipPlane = 0.05f;
        m_bodyCamera.farClipPlane = 20f;
        m_bodyCamera.enabled = false;

        Vector3 bodyFocus = model.transform.position + Vector3.up * m_bodyHeight;
        bodyGo.transform.position = bodyFocus + model.transform.forward * m_bodyDistance;
        bodyGo.transform.LookAt(bodyFocus);

        // 내 얼굴은 아무도 묻기 전에 챙겨 둔다 — 게임 씬으로 들고 갈 그림이라 로비에서 구워야 한다
        GetPortrait(PlayerColorSet.FromSettings());
    }

    private RenderTexture CreateTexture(int key) =>
        CreateTexture(m_textureSize, $"LobbyPortrait {key}");

    private RenderTexture CreateTexture(Vector2Int size, string name)
    {
        var texture = new RenderTexture(size.x, size.y, 16, RenderTextureFormat.ARGB32)
        {
            name = name,
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
        {
            BakeBody();
            EvictUnused();
            return;
        }

        foreach (int key in m_pending)
        {
            if (!m_portraits.TryGetValue(key, out RenderTexture texture) || texture == null)
                continue;

            Tint(m_requested[key]);
            m_camera.targetTexture = texture;
            RenderPortrait(texture);
            CaptureSession(key, texture);
        }

        m_pending.Clear();
        m_camera.targetTexture = null;

        BakeBody();
        EvictUnused();
    }

    // 쓰지 않는 조합을 버린다 — 팔레트를 눌러 볼 때마다 조합이 하나씩 늘어 그대로 두면
    // 텍스처가 세션 내내 쌓인다. 살아 있는 것은 명부에 있는 색과 내 색뿐이다. (#432)
    private void EvictUnused()
    {
        m_live.Clear();
        m_live.Add(PlayerColorSet.FromSettings().Key);

        SessionRoster roster = App.Game.Roster;
        if (roster != null && roster.IsSpawned)
        {
            for (int i = 0; i < roster.Players.Count; i++)
                m_live.Add(roster.Players[i].Colors.Key);
        }

        m_stale.Clear();
        foreach (int key in m_portraits.Keys)
        {
            if (!m_live.Contains(key))
                m_stale.Add(key);
        }

        for (int i = 0; i < m_stale.Count; i++)
        {
            int key = m_stale[i];

            if (m_portraits.TryGetValue(key, out RenderTexture texture) && texture != null)
            {
                texture.Release();
                Destroy(texture);
            }

            if (s_sessionPortraits.TryGetValue(key, out Texture2D captured) && captured != null)
                Destroy(captured);

            m_portraits.Remove(key);
            s_sessionPortraits.Remove(key);
            m_requested.Remove(key);
        }
    }

    // 창에 띄울 전신 — 내 색으로만 그린다
    private void BakeBody()
    {
        if (!m_bodyDirty || m_bodyCamera == null || m_bodyTexture == null)
            return;

        Tint(PlayerColorSet.FromSettings());
        m_bodyCamera.targetTexture = m_bodyTexture;
        Render(m_bodyCamera, m_bodyTexture);
        m_bodyCamera.targetTexture = null;
        m_bodyDirty = false;
    }

    // 무대 모델을 그 사람 색으로 갈아입힌다 — 얼굴만 잡지만 부위 색을 그대로 태운다 (#432)
    private void Tint(PlayerColorSet colors)
    {
        if (m_tint == null || m_palette == null)
            return;

        for (int i = 0; i < m_tintBuffer.Length; i++)
            m_tintBuffer[i] = m_palette.Get(colors[(EBodyPart)i]);

        m_tint.SetBase(m_tintBuffer, m_tintBuffer[(int)EBodyPart.Torso]);
    }

    // 씬 너머로 들고 갈 Texture2D로 옮겨 둔다 — RenderTexture는 씬을 넘기면 내용이 날아간다. (#720/#432)
    private void CaptureSession(int key, RenderTexture source)
    {
        // MSAA가 걸린 판은 그대로 읽을 수 없다 — 안티에일리어싱 없는 임시 판에 한 번 옮긴다
        RenderTexture resolved = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(source, resolved);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = resolved;

        var captured = new Texture2D(source.width, source.height, TextureFormat.ARGB32, false, false)
        {
            name = $"SessionPortrait {key}",
        };
        captured.ReadPixels(new Rect(0f, 0f, source.width, source.height), 0, 0);
        captured.Apply();

        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(resolved);

        if (s_sessionPortraits.TryGetValue(key, out Texture2D stale) && stale != null)
            Destroy(stale);

        s_sessionPortraits[key] = captured;
    }

    private void RenderPortrait(RenderTexture destination) => Render(m_camera, destination);

    // SRP에서 Camera.Render()는 파이프라인 밖 경로다 — URP가 지원하는 렌더 요청을 먼저 쓴다.
    private static void Render(Camera camera, RenderTexture destination)
    {
        if (camera == null || destination == null)
            return;

        var request = new UniversalRenderPipeline.SingleCameraRequest { destination = destination };
        if (RenderPipeline.SupportsRenderRequest(camera, request))
            RenderPipeline.SubmitRenderRequest(camera, request);
        else
            camera.Render();
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
