using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 날씨 하늘 리그 — 먹구름과 강수를 함께 매다는 <b>월드 정렬</b> 컨테이너. (#227)
/// 눈·비·안개 뷰가 공유한다: 하늘에 뭔가를 띄우는 방식이 세 곳에 각각 복사돼 있으면 같은 버그를 세 번 고친다.
///
/// <b>카메라의 자식으로 붙이지 않는다.</b> 자식이 되면 회전까지 물려받아, 고개를 드는 순간 강수 볼륨이
/// 함께 기울어 비·눈이 옆으로 흐르고 먹구름이 머리 위가 아니라 앞쪽으로 휜다. 그래서 <b>위치만</b>
/// 따라가고 회전은 손대지 않는다 — 강수가 언제나 월드 -Y로 떨어지는 근거가 이것이다.
///
/// <b>카메라는 매 프레임 다시 확인한다.</b> 이 프로젝트는 Cinemachine과 본부 CCTV로 시점을 갈아타므로
/// 켜지는 순간 한 번만 잡으면 그 뒤로는 죽은(또는 꺼진) 카메라를 따라가고, 화면에는 아무것도 보이지 않는다.
/// 카메라가 아직 없어도 켜기를 포기하지 않는다 — 다음 프레임에 다시 본다.
///
/// <b>구름과 강수를 한 리그에 매다는 것이 "먹구름에서 떨어진다"의 근거다</b> — 강수 방출 지점이 구름
/// 바로 아래로 고정되므로 둘이 서로 무관하게 떠 있지 않는다. 예전에는 구름이 카메라 위 20m, 비가 위 5m에
/// 각각 독립으로 떠 있어 비가 구름에서 나오는 그림이 아니었다.
///
/// 표현 계층이라 서버 권위와 무관하다 — 각 피어가 자기 화면에 자기 리그를 만든다. 이벤트의 on/off만
/// 동기화되면 되고(각 <c>XxxEvent</c>), 리그는 복제하지 않는다.
/// </summary>
public class WeatherSkyRig : MonoBehaviour
{
    // 리그를 만든 쪽이 넘긴 값 — 런타임 생성이라 SerializeField가 아니다(씬 배선 없이 뷰가 만든다)
    private float m_cloudHeight;

    // 따라갈 기준 — 로컬 플레이어(1순위) 또는 Camera.main(폴백). ResolveView 참고.
    private Transform m_view;

    /// <summary>먹구름을 매다는 자리 — 카메라 위 <c>cloudHeight</c>.</summary>
    public Transform CloudAnchor { get; private set; }

    /// <summary>강수를 매다는 자리 — 구름에서 <c>precipitationDrop</c>만큼 내려온 지점.</summary>
    public Transform PrecipitationAnchor { get; private set; }

    /// <summary>
    /// 리그를 만든다 — <b>씬 배선이 필요 없다</b>. 뷰가 날씨를 켤 때 만들고 끌 때 없앤다.
    /// </summary>
    /// <param name="label">하이어라키에 보일 이름 — 어느 날씨의 리그인지 구분하려고 받는다.</param>
    /// <param name="cloudHeight">카메라 기준 구름층 높이(m) — 하늘로 읽히려면 높아야 한다.</param>
    /// <param name="precipitationHeight">
    /// 카메라 기준 강수 방출 높이(m) — <b>구름 높이와 따로 준다</b>.
    ///
    /// 예전에는 "구름에서 낙차만큼 내린 지점"으로 묶여 있었는데, 그러면 구름을 하늘까지 올리는 순간
    /// 강수도 함께 올라가 눈·비가 머리 위 수십 미터에서 뿌려진다 — 화면에 닿기까지 오래 걸려 안 내리는 것처럼 보인다.
    /// 구름은 하늘에, 강수는 플레이어 근처에 두는 것이 맞다: "구름에서 떨어진다"는 느낌은 둘이 붙어 있어서가
    /// 아니라 위에서 아래로 흐르는 그림이 이어져서 생긴다.
    /// </param>
    public static WeatherSkyRig Create(string label, float cloudHeight, float precipitationHeight)
    {
        GameObject root = new GameObject(label);
        WeatherSkyRig rig = root.AddComponent<WeatherSkyRig>();
        rig.m_cloudHeight = cloudHeight;

        rig.CloudAnchor = CreateAnchor(root.transform, "Cloud", cloudHeight);
        rig.PrecipitationAnchor = CreateAnchor(root.transform, "Precipitation", Mathf.Max(0f, precipitationHeight));

        rig.SnapToCamera(); // 첫 프레임부터 제자리 — 원점에서 날아오는 것이 보이지 않게
        return rig;
    }

    private static Transform CreateAnchor(Transform parent, string label, float height)
    {
        Transform anchor = new GameObject(label).transform;
        anchor.SetParent(parent, false);
        anchor.localPosition = new Vector3(0f, height, 0f);
        return anchor;
    }

    // 구름층을 월드 격자에 스냅하는 칸 크기(m). 0이면 스냅하지 않는다.
    private float m_cloudSnap;

    // 강수를 시야 앞으로 밀어내는 거리(m). 0이면 카메라 위에서 그대로 뿌린다.
    private float m_precipitationForward;
    private bool m_precipitationFacesView;

    /// <summary>
    /// 강수를 <b>보고 있는 쪽</b>으로 붙인다 — 맵 전체에 뿌리는 대신 시야에만 내리게 하는 방식.
    ///
    /// 날씨는 표현 계층이라 각 피어가 자기 화면에만 만든다(복제하지 않는다). 그래서 월드를 넓게 채울
    /// 이유가 없고, 시야 앞 한 덩이만 있으면 어디를 보든 내리는 것처럼 보인다 — 파티클 수도 훨씬 적다.
    ///
    /// <b>yaw만 맞춘다.</b> 카메라 회전을 전부 물려받으면 고개를 드는 순간 강수 볼륨이 기울어 비가
    /// 옆으로 흐른다(원래 코드의 문제였다). 수평 방향만 돌리면 낙하는 월드 -Y로 남고, 방출 상자는
    /// 늘 보는 쪽을 덮는다.
    /// </summary>
    /// <param name="forwardOffset">시야 앞으로 밀 거리(m) — 카메라 바로 앞에서 뿌리면 입자가 눈앞에 붙는다.</param>
    public void SetPrecipitationFacesView(bool facesView, float forwardOffset)
    {
        m_precipitationFacesView = facesView;
        m_precipitationForward = Mathf.Max(0f, forwardOffset);
    }

    /// <summary>
    /// 구름층을 월드 격자에 스냅한다 — <b>하늘이 맵에 걸려 있는 느낌</b>을 만든다.
    ///
    /// 스냅이 없으면 구름이 카메라를 그대로 따라와, 어디로 가도 같은 구름이 머리 위에 붙어 있다.
    /// 그러면 "구름 한 덩이가 나를 쫓아온다"로 읽히고 하늘이 넓게 덮인 느낌이 안 난다. 격자에 스냅하면
    /// 구름은 월드에 고정된 채 있고 칸을 넘을 때만 재배치되므로, 걸어갈 때 하늘이 지나간다.
    /// 칸 크기가 구름 배치 간격의 배수라 이음매가 눈에 띄지 않는다 (IcePatch 격자와 같은 수법).
    /// </summary>
    public void SetCloudSnap(float snapSize) => m_cloudSnap = Mathf.Max(0f, snapSize);

    // 카메라가 움직인 뒤에 맞춘다 — Cinemachine이 Update 구간에서 카메라를 옮기므로,
    // Update에서 맞추면 리그가 한 프레임 뒤처져 빠르게 돌 때 하늘이 따라오다 밀리는 것이 보인다.
    private void LateUpdate()
    {
        SnapToCamera();
        SnapCloudLayer();
        FacePrecipitationToView();
        TickShelter();
    }

    // ---- 실내 차단 (2026-08-12 확정) ----
    //
    // 강수는 시야 앞 볼륨에서 그냥 쏟아지므로 <b>지붕을 모른다</b> — 건물 안에 서 있어도 천장 위에서
    // 생성돼 그대로 뚫고 내려온다(실측 증상: 실내에서도 비가 온다).
    //
    // 파티클 충돌(Collision 모듈)로 지붕에 맞혀 없애는 방법도 있는데, 지붕마다 콜라이더가 정확해야 하고
    // 입자 수만큼 비용이 붙는다. 대신 <b>머리 위로 레이 하나</b>를 쏴 하늘이 막혔는지만 보고 방출을
    // 여닫는다 — 리그당 한 번이라 사실상 공짜다.
    //
    // 원점을 시야가 아니라 <b>방출 지점의 수평 위치</b>로 잡는 것이 중요하다: 문간에 서서 밖을 볼 때
    // 시야 기준이면 머리 위 처마에 걸려 밖에도 비가 그친다. 방출 지점은 시야 앞으로 밀려 있으므로
    // 그 자리를 보면 "실제로 비가 내릴 곳이 뚫려 있는가"를 묻게 된다.

    private LayerMask m_shelterMask;
    private float m_shelterProbeHeight;
    private float m_shelterFadeSeconds;
    private bool m_shelterEnabled;

    // 1 = 하늘이 뚫려 있다, 0 = 지붕 아래. 문을 드나들 때 툭 끊기지 않게 보간한다.
    private float m_shelterFactor = 1f;

    // 방출 배율의 기준값 — Boost가 이미 곱해 둔 값이라 여기서 다시 계산하지 않고 붙잡아 둔다
    private readonly List<ParticleSystem> m_precipitationSystems = new List<ParticleSystem>();
    private readonly List<float> m_precipitationBaseRates = new List<float>();
    private bool m_precipitationCached;

    /// <summary>
    /// 지붕 아래에서는 강수를 그치게 한다 — 뷰가 켤 때 한 번 부른다. (2026-08-12 확정)
    /// </summary>
    /// <param name="blockMask">하늘을 막는 것으로 칠 레이어 — 건물은 <c>Default</c>다.</param>
    /// <param name="probeHeight">머리 위로 이만큼(m) 안에 뭔가 있으면 실내로 본다. 건물 높이보다 넉넉히.</param>
    /// <param name="fadeSeconds">여닫는 데 걸리는 시간(초). 0이면 즉시.</param>
    public void SetShelterProbe(LayerMask blockMask, float probeHeight, float fadeSeconds)
    {
        m_shelterEnabled = probeHeight > 0f;
        m_shelterMask = blockMask;
        m_shelterProbeHeight = Mathf.Max(0f, probeHeight);
        m_shelterFadeSeconds = Mathf.Max(0f, fadeSeconds);
    }

    private void TickShelter()
    {
        if (!m_shelterEnabled || m_view == null)
            return;

        CachePrecipitationSystems();

        float target = IsSheltered() ? 0f : 1f;
        m_shelterFactor =
            m_shelterFadeSeconds <= 0f
                ? target
                : Mathf.MoveTowards(m_shelterFactor, target, Time.deltaTime / m_shelterFadeSeconds);

        for (int i = 0; i < m_precipitationSystems.Count; i++)
        {
            ParticleSystem ps = m_precipitationSystems[i];
            if (ps == null)
                continue;

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTimeMultiplier = m_precipitationBaseRates[i] * m_shelterFactor;
        }
    }

    // 방출 지점의 수평 위치에서 시야 높이로 위를 본다 (위 주석의 문간 사례).
    // 판정 자체는 낙뢰와 공유한다 — 둘이 다르게 답하면 "비는 그쳤는데 벼락은 떨어진다"가 된다.
    private bool IsSheltered()
    {
        Vector3 origin = m_view.position;
        if (PrecipitationAnchor != null)
        {
            Vector3 anchor = PrecipitationAnchor.position;
            origin = new Vector3(anchor.x, origin.y, anchor.z);
        }

        return WeatherShelter.IsSheltered(origin, m_shelterMask, m_shelterProbeHeight);
    }

    // 붙은 파티클을 한 번만 훑는다 — Boost까지 끝난 뒤인 첫 LateUpdate에 잡아야 기준값이 맞다.
    private void CachePrecipitationSystems()
    {
        if (m_precipitationCached || PrecipitationAnchor == null)
            return;

        m_precipitationCached = true;
        foreach (ParticleSystem ps in PrecipitationAnchor.GetComponentsInChildren<ParticleSystem>(true))
        {
            m_precipitationSystems.Add(ps);
            m_precipitationBaseRates.Add(ps.emission.rateOverTimeMultiplier);
        }
    }

    // 강수 방출 지점을 시야 앞으로 밀고 수평 방향만 맞춘다 — 낙하 방향은 건드리지 않는다.
    private void FacePrecipitationToView()
    {
        if (PrecipitationAnchor == null || !m_precipitationFacesView || m_view == null)
            return;

        // 보는 쪽의 수평 성분만 뽑는다. 위를 보고 있으면 forward가 하늘을 가리키므로
        // 그대로 쓰면 방출 지점이 머리 위로 솟는다 — y를 버려야 시야 '앞'이 된다.
        // 기준이 플레이어면 몸통 정면인데, 시점 회전이 몸통 yaw를 돌리므로(PlayerLook) 결과가 같다.
        Vector3 flatForward = m_view.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.001f)
            flatForward = Vector3.forward; // 정수리를 보고 있다 — 방향이 없으니 기본값
        flatForward.Normalize();

        PrecipitationAnchor.localPosition =
            new Vector3(0f, PrecipitationAnchor.localPosition.y, 0f)
            + transform.InverseTransformDirection(flatForward) * m_precipitationForward;

        // yaw만 — 낙하는 월드 -Y로 남는다
        PrecipitationAnchor.rotation = Quaternion.LookRotation(flatForward, Vector3.up);
    }

    // 구름층만 월드 격자로 되돌린다 — 리그(=강수)는 카메라를 부드럽게 따라가고 하늘만 고정된다
    private void SnapCloudLayer()
    {
        if (CloudAnchor == null || m_cloudSnap <= 0f)
            return;

        Vector3 world = transform.position;
        CloudAnchor.position = new Vector3(
            Mathf.Round(world.x / m_cloudSnap) * m_cloudSnap,
            world.y + m_cloudHeight,
            Mathf.Round(world.z / m_cloudSnap) * m_cloudSnap
        );
    }

    private void SnapToCamera()
    {
        ResolveView();

        if (m_view == null)
            return; // 아직 기준이 없다 — 다음 프레임에 다시 본다

        // <b>위치만</b> 따라간다. 회전을 건드리지 않아 월드 정렬로 남는다.
        transform.position = m_view.position;
    }

    /// <summary>
    /// 따라갈 기준을 정한다 — <b>로컬 플레이어의 시점 카메라가 1순위, Camera.main은 폴백이다.</b>
    ///
    /// ⚠ <c>Camera.main</c>만 믿으면 안 된다: <c>Player.prefab</c>의 시점 카메라는 <b>Untagged</b>라
    /// Camera.main으로 잡히지 않는다. 그러면 씬에 놓인 고정 <c>Main Camera</c>가 잡혀 리그가 그 자리에
    /// 굳고, 비·눈이 <b>맵의 한 지점에서만 내린다</b>. 그래서 세션의 로컬 플레이어를 먼저 쓴다.
    ///
    /// <b>몸통이 아니라 그 아래 카메라를 잡는다</b> (2026-08-12 확정). 몸통 yaw는 시점을 <b>따라오는</b>
    /// 값이라 빠르게 돌리면 한 박자 늦고, 강수 볼륨이 시야 앞이 아니라 옆에 남아 "눈이 안 내린다"로
    /// 보인다(<see cref="FacePrecipitationToView"/>가 이 기준의 forward를 쓴다). 카메라를 기준으로
    /// 삼으면 그 지연이 원천에서 사라진다.
    ///
    /// 카메라가 꺼지면(관전 전환·CCTV) 다시 찾는다 — 한 번 잡고 끝내면 꺼진 카메라를 따라간다.
    /// </summary>
    private void ResolveView()
    {
        // 파괴된 참조는 null로 비교된다. 꺼진 것은 살아 있어도 화면을 그리지 않으므로 함께 본다.
        if (m_view != null && m_view.gameObject.activeInHierarchy)
            return;

        m_view = null;

        Unity.Netcode.NetworkManager manager = Unity.Netcode.NetworkManager.Singleton;
        if (manager != null && manager.IsListening && manager.LocalClient.PlayerObject != null)
        {
            Transform player = manager.LocalClient.PlayerObject.transform;
            m_view = FindActiveCamera(player) ?? player; // 카메라가 아직 없으면 몸통으로 버틴다
            return;
        }

        if (Camera.main != null)
            m_view = Camera.main.transform;
    }

    // 켜져 있는 카메라를 고른다 — 관전 오빗(#590) 등으로 여러 대가 달려 있을 수 있다.
    private static Transform FindActiveCamera(Transform root)
    {
        foreach (Camera camera in root.GetComponentsInChildren<Camera>(true))
        {
            if (camera.isActiveAndEnabled)
                return camera.transform;
        }

        return null;
    }

    /// <summary>
    /// 하늘에 붙인다 — 스케일 모드를 <see cref="ParticleSystemScalingMode.Local"/>로 맞춘다.
    ///
    /// 예전에는 <c>Hierarchy</c>로 뒀는데, 그 모드는 파티클 크기뿐 아니라 <b>속도와 이동 거리까지</b>
    /// 부모 스케일로 곱한다 — 구름 스케일 10을 주면 눈이 10배 빠르게 10배 멀리 날아가 원래 연출이 남지 않는다.
    /// 리그가 스케일 1이라 지금은 어느 쪽이든 같지만, 나중에 리그를 키워도 연출이 안 망가지게 못박아 둔다.
    /// </summary>
    /// <param name="fx">붙일 FX 인스턴스 — 이미 생성된 오브젝트를 받는다.</param>
    /// <param name="anchor"><see cref="CloudAnchor"/> 또는 <see cref="PrecipitationAnchor"/>.</param>
    /// <param name="scale">FX 자체의 크기 배율.</param>
    public static void Attach(GameObject fx, Transform anchor, float scale)
    {
        if (fx == null || anchor == null)
            return;

        fx.transform.SetParent(anchor, false);
        fx.transform.localPosition = Vector3.zero;
        fx.transform.localRotation = Quaternion.identity;
        fx.transform.localScale = new Vector3(scale, scale, scale);

        foreach (ParticleSystem ps in fx.GetComponentsInChildren<ParticleSystem>())
        {
            ParticleSystem.MainModule main = ps.main;
            main.scalingMode = ParticleSystemScalingMode.Local;
        }
    }

    /// <summary>
    /// FX를 <paramref name="tiles"/>×<paramref name="tiles"/> 격자로 여러 장 깐다 — <b>하늘을 넓게 덮는 용도</b>.
    /// 한 장을 크게 키우는 것과 다르다: 파티클 프리팹은 방출 볼륨이 정해져 있어 스케일만 올리면 입자가
    /// 같이 커져 구름 한 덩이가 부풀 뿐이고, 여러 장을 벌려 깔아야 하늘이 이어진 것처럼 보인다.
    /// </summary>
    /// <param name="tiles">한 변의 장수. 3이면 9장, 5면 25장.</param>
    /// <param name="spacing">장 사이 간격(m).</param>
    public static void AttachTiled(GameObject prefab, Transform anchor, float scale, int tiles, float spacing)
    {
        if (prefab == null || anchor == null)
            return;

        int radius = Mathf.Max(0, tiles / 2);
        for (int x = -radius; x <= radius; x++)
        {
            for (int z = -radius; z <= radius; z++)
            {
                GameObject fx = Instantiate(prefab);
                Attach(fx, anchor, scale);
                fx.transform.localPosition = new Vector3(x * spacing, 0f, z * spacing);
            }
        }
    }

    /// <summary>
    /// 방출량·입자 크기·낙하 속도·방출 볼륨을 배율로 키운다 — 프리팹 원본을 건드리지 않고 이 인스턴스만 바꾼다.
    /// Synty FX는 근거리 연출 기준으로 만들어져 하늘을 덮는 용도로는 전부 부족하다(눈이 잘 안 보이는 원인).
    /// </summary>
    /// <param name="speedMultiplier">낙하 속도 배율 — 성기게 흩날리던 눈을 <b>쏟아지는</b> 눈으로 바꾸는 값이다.
    /// 빨라지면 같은 높이를 더 빨리 지나가 화면에 남는 수가 줄므로 <paramref name="rateMultiplier"/>도 함께 올려야 한다.</param>
    /// <param name="volumeMultiplier">방출 볼륨(shape) 배율 — 시야 앞 한 덩이만 뿌리는 구성에서
    /// (<see cref="SetPrecipitationFacesView"/>) 볼륨이 좁으면 <b>시점을 빠르게 돌릴 때 그 덩이가 화면 밖으로
    /// 밀려난다</b>. 화각보다 넓게 잡아 두면 돌리는 도중에도 눈이 끊기지 않는다.</param>
    public static void Boost(
        GameObject fx,
        float sizeMultiplier,
        float rateMultiplier,
        float speedMultiplier,
        float volumeMultiplier
    )
    {
        if (fx == null)
            return;

        foreach (ParticleSystem ps in fx.GetComponentsInChildren<ParticleSystem>())
        {
            ParticleSystem.MainModule main = ps.main;
            main.startSizeMultiplier *= sizeMultiplier;
            main.startSpeedMultiplier *= speedMultiplier;
            main.gravityModifierMultiplier *= speedMultiplier; // 중력으로 떨어지는 프리팹도 함께 빨라진다

            // 상한도 함께 올린다 — 방출을 늘리면 기본 상한(보통 1000)에 걸려 조용히 잘린다
            main.maxParticles = Mathf.Max(main.maxParticles, (int)(main.maxParticles * rateMultiplier));

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTimeMultiplier *= rateMultiplier;

            // 방출 상자를 넓힌다 — radius/scale 중 어느 쪽을 쓰는 shape인지 프리팹마다 달라 둘 다 건드린다
            // (안 쓰는 쪽은 무해하게 무시된다).
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.radius *= volumeMultiplier;
            shape.scale = new Vector3(
                shape.scale.x * volumeMultiplier,
                shape.scale.y,
                shape.scale.z * volumeMultiplier
            );
        }
    }

    /// <summary>
    /// 방출을 멈추고 <paramref name="fadeSeconds"/> 뒤에 사라진다 — 공중에 있던 입자가 끝까지 떨어진다.
    /// 즉시 Destroy하면 내리던 눈·비가 한 프레임에 통째로 사라져 "그쳤다"가 아니라 "끊겼다"로 보인다.
    /// </summary>
    public void StopAndDispose(float fadeSeconds)
    {
        foreach (ParticleSystem ps in GetComponentsInChildren<ParticleSystem>())
            ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);

        Destroy(gameObject, Mathf.Max(0f, fadeSeconds));
    }
}
