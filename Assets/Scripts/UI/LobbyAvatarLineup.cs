using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

/// <summary>
/// 로비에 대기 중인 <b>전원의 로봇을 늘어세워</b> 각자 고른 색을 보여준다. (#432)
///
/// 아바타는 <b>NetworkObject가 아니다</b> — 표시용 모델일 뿐이라 조작·물리·소유권이 없다.
/// 그래서 "로비에는 플레이어 오브젝트가 없다"(#214)는 결정을 건드리지 않는다. 색의 출처는
/// 세션 명부(<see cref="SessionRoster"/>)의 <see cref="LobbyPlayerEntry.ColorIndex"/> 하나이고,
/// 명단이 바뀔 때마다(입퇴장·색 변경) 다시 세운다.
///
/// 무대와 카메라를 코드로 세워 RenderTexture에 굽는 방식은 <see cref="LobbyPortraitStage"/>와 같다 —
/// 로비는 UI만 있는 씬이라 3D를 그대로 놓을 자리가 없고, 무대를 멀리 두면 전용 레이어도 필요 없다.
///
/// 카메라는 꺼 둔 채로 <b>바뀔 때만</b> 그린다. 모델도 카메라도 움직이지 않으므로 매 프레임 그릴
/// 이유가 없다.
/// </summary>
[DefaultExecutionOrder((int)EExecutionOrder.UIContent)]
public class LobbyAvatarLineup : MonoBehaviour
{
    [Header("무대")]
    [Tooltip("세울 로봇 모델 — 플레이어와 같은 것을 쓴다 (SM_Gen_Chr_Robot_01)")]
    [SerializeField] private GameObject m_characterPrefab;

    [Tooltip("색 팔레트 — Player 프리팹의 PlayerCosmetics와 같은 에셋을 물릴 것")]
    [SerializeField] private PlayerColorPalette m_palette;

    [Tooltip("무대를 세울 위치 — 씬의 다른 것과 겹치지 않게 멀리 둔다. 초상 무대와도 떨어뜨린다")]
    [SerializeField] private Vector3 m_stageOrigin = new Vector3(500f, -500f, 0f);

    [Tooltip("로봇 사이 간격(m)")]
    [SerializeField] private float m_spacing = 1.1f;

    [Header("카메라")]
    [Tooltip("줄 앞에서 이만큼(m) 떨어져 잡는다 — 인원이 늘면 자동으로 더 물러난다")]
    [SerializeField] private float m_distance = 3.2f;

    [Tooltip("카메라 높이(m) — 로봇 가슴 높이쯤")]
    [SerializeField] private float m_height = 1.1f;

    [Range(20f, 70f)]
    [SerializeField] private float m_fieldOfView = 40f;

    [Tooltip("줄 그림 크기(px) — 화면의 표시 창과 같은 비율로 구울 것")]
    [SerializeField] private Vector2Int m_textureSize = new Vector2Int(768, 384);

    [Header("표시")]
    [Tooltip("구운 줄 그림을 띄울 창. 비워 두면 아무것도 보이지 않는다")]
    [SerializeField] private RawImage m_display;

    private readonly List<GameObject> m_avatars = new List<GameObject>();
    private readonly List<BodyTint> m_tints = new List<BodyTint>();

    private Transform m_stage;
    private Camera m_camera;
    private RenderTexture m_texture;
    private SessionRoster m_roster;
    private bool m_lit; // 첫 프레임(조명 확정) 이후인가 — 그 전에 구우면 밝기가 들쭉날쭉하다

    private void Awake()
    {
        if (m_characterPrefab == null)
        {
            Debug.LogWarning($"[{nameof(LobbyAvatarLineup)}] 로봇 모델이 연결되지 않았습니다 — 줄 없이 진행합니다. (#432)", this);
            enabled = false;
            return;
        }

        BuildStage();
    }

    private void Start() => TryBindRoster();

    // 아직 못 잡았을 때만 도는 폴링 — 잡는 즉시 이벤트 구동으로 넘어간다 (LobbyRosterPanel과 같은 방식)
    private void Update()
    {
        if (m_roster == null)
            TryBindRoster();
    }

    private void OnEnable() => GameSettings.OnPlayerColorChanged += HandleLocalColorChanged;

    private void OnDisable() => GameSettings.OnPlayerColorChanged -= HandleLocalColorChanged;

    private void OnDestroy()
    {
        if (m_roster != null)
        {
            m_roster.OnListReady -= Rebuild;
            if (m_roster.Players != null)
                m_roster.Players.OnListChanged -= HandleListChanged;
        }

        // RenderTexture는 GC 대상이 아니다 — 로비를 오갈 때마다 쌓이지 않게 직접 놓는다.
        if (m_texture == null)
            return;

        m_texture.Release();
        Destroy(m_texture);
        m_texture = null;
    }

    private void TryBindRoster()
    {
        // 세션 없이 로비를 직접 Play하면 명부가 스폰되지 않아 계속 null이다 (App.Game.Roster 주석)
        SessionRoster roster = App.Game.Roster;
        if (roster == null || roster == m_roster)
            return;

        m_roster = roster;
        m_roster.OnListReady += Rebuild;
        m_roster.Players.OnListChanged += HandleListChanged;

        // 명부 스폰이 이 부품보다 빨랐으면 OnListReady를 놓쳤으므로 지금 그린다.
        Rebuild();
    }

    private void HandleListChanged(NetworkListEvent<LobbyPlayerEntry> _) => Rebuild();

    // 내 색은 명부를 한 바퀴 돌아오기 전에 이미 정해져 있다 — 고르는 즉시 내 로봇이 바뀌어야
    // 팔레트가 반응 없어 보이지 않는다. 명부가 도착하면 같은 값으로 한 번 더 칠해질 뿐이다.
    private void HandleLocalColorChanged(int _) => Rebuild();

    /// <summary>명단대로 로봇을 다시 세운다 — 인원이 바뀌면 늘리고 줄이며, 색은 매번 다시 칠한다.</summary>
    private void Rebuild()
    {
        if (m_stage == null)
            return;

        int count = m_roster != null && m_roster.Players != null ? m_roster.Players.Count : 0;

        // 혼자 있어도 자기 로봇은 보여야 한다 — 명부가 아직 안 잡힌 로비 직행 플레이도 마찬가지다.
        if (count == 0)
            count = 1;

        while (m_avatars.Count < count)
            AddAvatar();

        for (int i = 0; i < m_avatars.Count; i++)
        {
            bool used = i < count;
            m_avatars[i].SetActive(used);
            if (!used)
                continue;

            m_avatars[i].transform.localPosition = SlotPosition(i, count);
            m_tints[i].SetBase(ColorOf(i));
        }

        AimCamera(count);
        RenderAsync().Forget();
    }

    private Color ColorOf(int slot)
    {
        if (m_palette == null)
            return Color.white;

        // 명부가 없으면(로비 씬 직접 Play) 내 로컬 설정을 보여준다 — 색을 고르는 화면이 비면 안 된다
        int index =
            m_roster != null && m_roster.Players != null && slot < m_roster.Players.Count
                ? m_roster.Players[slot].ColorIndex
                : GameSettings.PlayerColorIndex;

        return m_palette.Get(index);
    }

    private void AddAvatar()
    {
        GameObject model = Instantiate(m_characterPrefab, m_stage);
        model.name = $"LobbyAvatar {m_avatars.Count}";
        model.transform.localRotation = Quaternion.identity;

        // 칠하기는 BodyTint 하나에 맡긴다 — 표시용 모델도 같은 규칙을 따른다 (#478)
        m_avatars.Add(model);
        m_tints.Add(model.AddComponent<BodyTint>());
    }

    // 가운데를 기준으로 좌우로 펼친다 — 인원이 늘어도 줄의 중심이 흔들리지 않는다
    private Vector3 SlotPosition(int slot, int count) =>
        new Vector3((slot - (count - 1) * 0.5f) * m_spacing, 0f, 0f);

    private void BuildStage()
    {
        var stage = new GameObject("AvatarStage");
        stage.transform.SetParent(transform, false);
        stage.transform.position = m_stageOrigin;
        m_stage = stage.transform;

        m_texture = new RenderTexture(m_textureSize.x, m_textureSize.y, 16, RenderTextureFormat.ARGB32)
        {
            name = "LobbyAvatarLineup",
            antiAliasing = 2,
        };

        // 굽기 전까지 창에 걸릴 텍스처다 — 새 RenderTexture의 내용은 보장되지 않아 명시적으로 비운다
        m_texture.Create();
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = m_texture;
        GL.Clear(true, true, Color.clear);
        RenderTexture.active = prev;

        var camGo = new GameObject("AvatarCamera");
        camGo.transform.SetParent(m_stage, false);
        m_camera = camGo.AddComponent<Camera>();
        m_camera.clearFlags = CameraClearFlags.SolidColor;
        m_camera.backgroundColor = new Color(0f, 0f, 0f, 0f); // 로비 배경이 비치게 투명 배경
        m_camera.fieldOfView = m_fieldOfView;
        m_camera.nearClipPlane = 0.05f;
        m_camera.farClipPlane = 20f; // 무대 밖은 아무것도 안 잡힌다
        m_camera.targetTexture = m_texture;
        m_camera.enabled = false;

        if (m_display != null)
            m_display.texture = m_texture;
    }

    // 줄이 길어지면 뒤로 물러나 전원이 화면에 들어오게 한다
    private void AimCamera(int count)
    {
        float span = Mathf.Max(1, count - 1) * m_spacing;
        float back = m_distance + span * 0.5f / Mathf.Tan(m_fieldOfView * 0.5f * Mathf.Deg2Rad);

        m_camera.transform.localPosition = new Vector3(0f, m_height, -back);
        m_camera.transform.localRotation = Quaternion.identity;
    }

    // 조명이 확정된 뒤에 굽는다 — 로드 직후에는 환경광·기본 반사가 아직 준비 전이라
    // 그때 구우면 실행할 때마다 밝기가 달라진다 (LobbyPortraitStage와 같은 이유).
    private async UniTaskVoid RenderAsync()
    {
        CancellationToken token = this.GetCancellationTokenOnDestroy();

        if (!m_lit)
        {
            await UniTask.WaitForEndOfFrame(token);
            DynamicGI.UpdateEnvironment();
            m_lit = true;
        }

        await UniTask.WaitForEndOfFrame(token);
        Render();
    }

    // SRP에서 Camera.Render()는 파이프라인 밖 경로다 — URP가 지원하는 렌더 요청을 먼저 쓴다.
    private void Render()
    {
        if (m_camera == null || m_texture == null)
            return;

        var request = new UniversalRenderPipeline.SingleCameraRequest { destination = m_texture };
        if (RenderPipeline.SupportsRenderRequest(m_camera, request))
            RenderPipeline.SubmitRenderRequest(m_camera, request);
        else
            m_camera.Render();
    }
}
