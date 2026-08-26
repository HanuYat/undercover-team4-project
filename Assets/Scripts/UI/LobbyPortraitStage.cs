using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

/// <summary>
/// 로비 명단 카드에 넣을 <b>얼굴</b>을 만든다 — 무대에 캐릭터를 세우고 머리만 잡아 RenderTexture로 굽는다. (#598)
///
/// 무대는 <b>로비와 상점 두 곳</b>에 있다 (#863) — 외형은 상점에서도 바뀌므로 로비에서 구운 것만으로는
/// 맵의 팀 상황판이 옛 조합을 찾게 된다. 두 곳 모두에서 <b>명부 전원</b>의 조합을 굽고, 루프가
/// Shop ↔ Game이라 맵으로 넘어가는 것은 언제나 상점에서 구운 그림이다.
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

    [Tooltip("치장 카탈로그 — Player 프리팹의 PlayerAccessories와 같은 에셋을 물릴 것 (#818)")]
    [SerializeField] private AccessoryCatalog m_accessoryCatalog;

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

    /// <summary>
    /// 얼굴 한 장을 가리키는 열쇠 — <b>색 + 치장</b>이다 (#432 · #818).
    /// 색만으로 잡으면 같은 색을 고른 두 사람이 같은 얼굴을 쓰는데, 모자가 생긴 뒤로는
    /// 그 둘의 머리가 서로 다르다. 같은 조합이면 여전히 한 장을 나눠 쓴다.
    /// </summary>
    public readonly struct PortraitKey : System.IEquatable<PortraitKey>
    {
        public readonly PlayerColorSet Colors;
        public readonly AccessorySet Accessories;

        public PortraitKey(PlayerColorSet colors, AccessorySet accessories)
        {
            Colors = colors;
            Accessories = accessories;
        }

        public static PortraitKey Mine =>
            new PortraitKey(PlayerColorSet.FromSettings(), AccessorySet.FromSettings());

        public bool Equals(PortraitKey other) =>
            Colors.Equals(other.Colors) && Accessories.Equals(other.Accessories);

        public override bool Equals(object obj) => obj is PortraitKey other && Equals(other);

        public override int GetHashCode() => (Colors.Key * 397) ^ Accessories.GetHashCode();
    }

    // 색·치장 조합 → 그 조합으로 구운 얼굴
    private readonly Dictionary<PortraitKey, RenderTexture> m_portraits =
        new Dictionary<PortraitKey, RenderTexture>();
    private readonly HashSet<PortraitKey> m_pending = new HashSet<PortraitKey>(); // 아직 그림이 안 채워진 것
    private readonly HashSet<PortraitKey> m_live = new HashSet<PortraitKey>(); // 지금 쓰이는 조합
    private readonly List<PortraitKey> m_stale = new List<PortraitKey>();

    private Camera m_camera;
    private Camera m_bodyCamera;
    private RenderTexture m_bodyTexture;
    private bool m_bodyDirty = true; // 내 색이 바뀌면 다시 그린다
    private BodyTint m_tint;
    private Transform m_stageHead; // 치장을 붙일 무대 모델의 머리 본 (#818)
    private readonly GameObject[] m_accessories = new GameObject[System.Enum.GetValues(
        typeof(EAccessorySlot)
    ).Length];
    private readonly GameObject[] m_worn = new GameObject[System.Enum.GetValues(
        typeof(EAccessorySlot)
    ).Length]; // 슬롯마다 지금 붙어 있는 원본 프리팹 — 같은 것이면 다시 만들지 않는다
    private readonly Color[] m_tintBuffer = new Color[3]; // 인덱스 = EBodyPart
    private bool m_baking;
    private bool m_lit; // 첫 프레임(조명 확정)을 지났는가
    private SessionRoster m_roster; // 전원의 조합을 굽는 근거 (#863)

    // 무대가 있는 씬을 떠나도 살려 두는 얼굴들 — 게임 씬에서 다시 구우면 맵 조명을 타 어둡게
    // 나오므로, 직전에 거친 무대(=상점)에서 구운 것을 쓴다. 명부에서 사라진 조합은 EvictUnused가
    // 놓아 준다. (#720 · #863)
    // 열쇠는 색·치장 조합 — 게임 씬 상황판이 각 플레이어의 조합으로 찾아 간다. (#432 · #818)
    //
    // RenderTexture가 아니라 Texture2D인 것은 씬 너머로 들고 가야 해서다 — RenderTexture는
    // 오브젝트만 남고 GPU 쪽은 놓여, 게임 씬에서는 빈 칸이 그려졌다.
    private static readonly Dictionary<PortraitKey, Texture2D> s_sessionPortraits =
        new Dictionary<PortraitKey, Texture2D>();

    /// <summary>내 색·치장으로 구운 얼굴. (#432 · #818)</summary>
    public Texture Portrait => GetPortrait(PortraitKey.Mine);

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
    public static Texture SessionPortrait => GetSessionPortrait(PortraitKey.Mine);

    // 도메인 리로드를 끄면 지난 플레이의 (이미 파괴된) 텍스처 참조가 static에 남는다 —
    // Unity 가짜 null이라 받는 쪽 null 검사도 통과한다. 플레이 시작마다 비운다. (GameSettings.Load와 같은 이유)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void ResetStatics() => s_sessionPortraits.Clear();

    /// <summary>그 조합으로 로비에서 구워 둔 얼굴 — 게임 씬 UI가 읽는다. 없으면 null. (#432)</summary>
    public static Texture GetSessionPortrait(PlayerColorSet colors, AccessorySet accessories) =>
        GetSessionPortrait(new PortraitKey(colors, accessories));

    public static Texture GetSessionPortrait(PortraitKey key) =>
        s_sessionPortraits.TryGetValue(key, out Texture2D portrait) ? portrait : null;

    /// <summary>
    /// 그 색 조합으로 구운 얼굴 — 무대가 없으면 null. 처음 묻는 조합이면 빈 텍스처를 먼저 건네고
    /// 그림은 곧 채운다.
    /// </summary>
    public Texture GetPortrait(PlayerColorSet colors, AccessorySet accessories) =>
        GetPortrait(new PortraitKey(colors, accessories));

    public Texture GetPortrait(PortraitKey key)
    {
        if (m_camera == null)
            return null;

        if (m_portraits.TryGetValue(key, out RenderTexture cached))
            return cached;

        RenderTexture texture = CreateTexture(key);
        m_portraits[key] = texture;
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
    private void OnEnable()
    {
        GameSettings.OnPlayerColorChanged += HandleOwnColorChanged;
        GameSettings.OnAccessoryChanged += HandleOwnAccessoryChanged;
        TryBindRoster();
    }

    private void OnDisable()
    {
        GameSettings.OnPlayerColorChanged -= HandleOwnColorChanged;
        GameSettings.OnAccessoryChanged -= HandleOwnAccessoryChanged;
        UnbindRoster();
    }

    // 아직 못 잡았을 때만 도는 폴링 — 잡는 즉시 이벤트 구동으로 넘어간다 (LobbyRosterPanel과 같은 방식)
    private void Update()
    {
        if (m_roster == null)
            TryBindRoster();
    }

    // 명부는 세션 상주라 씬에 없다 — App 경유로 잡는다. 세션 없이 씬을 직접 Play하면 끝내 null이다.
    private void TryBindRoster()
    {
        SessionRoster roster = App.Game.Roster;
        if (roster == null)
            return;

        m_roster = roster;
        m_roster.Players.OnListChanged += HandleRosterChanged;
        m_roster.OnListReady += BakeRoster; // late-join은 초기 내용을 OnListChanged로 받지 못한다
        BakeRoster();
    }

    private void UnbindRoster()
    {
        if (m_roster == null)
            return;

        m_roster.Players.OnListChanged -= HandleRosterChanged;
        m_roster.OnListReady -= BakeRoster;
        m_roster = null;
    }

    private void HandleRosterChanged(NetworkListEvent<LobbyPlayerEntry> _) => BakeRoster();

    /// <summary>
    /// 명부에 있는 사람 전부의 얼굴을 확보한다 (#863) — 누가 색·모자를 바꾸면 명부가 갱신되므로
    /// 바뀐 조합이 그때마다 여기로 들어온다. 이미 구운 조합은 <see cref="GetPortrait"/>가 걸러 낸다.
    /// </summary>
    private void BakeRoster()
    {
        if (m_roster == null || !m_roster.IsSpawned)
            return;

        for (int i = 0; i < m_roster.Players.Count; i++)
            GetPortrait(new PortraitKey(m_roster.Players[i].Colors, m_roster.Players[i].Accessories));
    }

    private void OnDestroy()
    {
        // RenderTexture는 GC 대상이 아니다 — 씬을 오갈 때마다 쌓이지 않게 직접 놓는다.
        // 세션용 얼굴은 별도 Texture2D라 여기서 놓지 않는다 — 다음 씬이 그것을 쓴다 (#720 · #863)
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
        GetPortrait(PortraitKey.Mine);
    }

    // 치장은 전신 미리보기에만 태운다 — 얼굴 카드는 색 조합 단위로 캐시되므로(같은 색이면 같은 그림)
    // 여기에 내 모자를 태우면 남의 카드에도 내 모자가 붙는다. (#818)
    private void HandleOwnAccessoryChanged(EAccessorySlot _)
    {
        m_bodyDirty = true;
        GetPortrait(PortraitKey.Mine);
        BakeAsync().Forget(); // 이미 구워 둔 조합이면 GetPortrait가 굽기를 걸지 않는다
    }

    /// <summary>
    /// 무대 모델에 그 사람의 치장을 갈아 끼운다 (#818) — <c>PlayerAccessories.Apply</c>와 같은 규칙이다.
    /// 색(<see cref="Tint"/>)과 마찬가지로 굽기 직전에 갈아입히므로 무대 하나로 모두의 얼굴을 굽는다.
    /// <b>즉시 파괴</b>해야 한다 — 굽기는 한 프레임 안에서 조합마다 도는데, 지연 파괴면 앞사람 모자가
    /// 다음 얼굴에 함께 찍힌다.
    /// </summary>
    private void Wear(AccessorySet accessories)
    {
        if (m_stageHead == null || m_accessoryCatalog == null)
            return;

        EAccessorySlotMask hidden = m_accessoryCatalog.HiddenSlots(accessories);

        foreach (EAccessorySlot slot in System.Enum.GetValues(typeof(EAccessorySlot)))
        {
            int i = (int)slot;
            GameObject prefab = AccessoryCatalog.IsHidden(hidden, slot)
                ? null
                : m_accessoryCatalog.Get(slot, accessories[slot]);
            if (m_worn[i] == prefab && (prefab == null) == (m_accessories[i] == null))
                continue; // 같은 것을 이미 쓰고 있다

            if (m_accessories[i] != null)
            {
                DestroyImmediate(m_accessories[i]);
                m_accessories[i] = null;
            }

            m_worn[i] = prefab;
            if (prefab == null)
                continue;

            m_accessories[i] = Instantiate(prefab, m_stageHead, false);
            m_accessories[i].name = prefab.name;
        }
    }

    private void BuildStage()
    {
        // 세션용 얼굴은 여기서 비우지 않는다 — 상점 무대가 로비에서 구운 것을 지워 버렸다 (#863)
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

        m_stageHead = head;
        Wear(AccessorySet.FromSettings());

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
        GetPortrait(PortraitKey.Mine);
    }

    private RenderTexture CreateTexture(PortraitKey key) =>
        CreateTexture(m_textureSize, $"LobbyPortrait {key.GetHashCode()}");

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

        foreach (PortraitKey key in m_pending)
        {
            if (!m_portraits.TryGetValue(key, out RenderTexture texture) || texture == null)
                continue;

            Tint(key.Colors);
            Wear(key.Accessories);
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
        m_live.Add(PortraitKey.Mine);

        SessionRoster roster = m_roster != null ? m_roster : App.Game.Roster;
        bool rosterReady = roster != null && roster.IsSpawned;

        if (rosterReady)
        {
            for (int i = 0; i < roster.Players.Count; i++)
                m_live.Add(new PortraitKey(roster.Players[i].Colors, roster.Players[i].Accessories));
        }

        Sweep(m_portraits);

        // 세션용은 명부를 읽을 수 있을 때만 쓸어 낸다 — 못 읽는 동안 버리면 직전 씬에서 구운 것이 통째로 날아간다 (#863)
        if (rosterReady)
            Sweep(s_sessionPortraits);
    }

    // 명부에 없는 조합의 텍스처를 놓는다 — RenderTexture는 GPU 쪽도 함께 반납한다
    private void Sweep<TTexture>(Dictionary<PortraitKey, TTexture> cache) where TTexture : Texture
    {
        m_stale.Clear();
        foreach (PortraitKey key in cache.Keys)
        {
            if (!m_live.Contains(key))
                m_stale.Add(key);
        }

        for (int i = 0; i < m_stale.Count; i++)
        {
            if (cache.TryGetValue(m_stale[i], out TTexture texture) && texture != null)
            {
                if (texture is RenderTexture render)
                    render.Release();

                Destroy(texture);
            }

            cache.Remove(m_stale[i]);
        }
    }

    // 창에 띄울 전신 — 내 색으로만 그린다
    private void BakeBody()
    {
        if (!m_bodyDirty || m_bodyCamera == null || m_bodyTexture == null)
            return;

        Tint(PlayerColorSet.FromSettings());
        Wear(AccessorySet.FromSettings()); // 얼굴을 굽느라 남의 것을 입고 있을 수 있다
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
    private void CaptureSession(PortraitKey key, RenderTexture source)
    {
        // MSAA가 걸린 판은 그대로 읽을 수 없다 — 안티에일리어싱 없는 임시 판에 한 번 옮긴다
        RenderTexture resolved = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(source, resolved);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = resolved;

        var captured = new Texture2D(source.width, source.height, TextureFormat.ARGB32, false, false)
        {
            name = $"SessionPortrait {key.GetHashCode()}",
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
