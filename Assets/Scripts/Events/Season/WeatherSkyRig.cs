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

    private Camera m_camera;

    /// <summary>먹구름을 매다는 자리 — 카메라 위 <c>cloudHeight</c>.</summary>
    public Transform CloudAnchor { get; private set; }

    /// <summary>강수를 매다는 자리 — 구름에서 <c>precipitationDrop</c>만큼 내려온 지점.</summary>
    public Transform PrecipitationAnchor { get; private set; }

    /// <summary>
    /// 리그를 만든다 — <b>씬 배선이 필요 없다</b>. 뷰가 날씨를 켤 때 만들고 끌 때 없앤다.
    /// </summary>
    /// <param name="label">하이어라키에 보일 이름 — 어느 날씨의 리그인지 구분하려고 받는다.</param>
    /// <param name="cloudHeight">카메라 기준 구름 높이(m).</param>
    /// <param name="precipitationDrop">구름에서 강수 방출 지점까지의 낙차(m). 구름 두께만큼 내려 방출한다.</param>
    public static WeatherSkyRig Create(string label, float cloudHeight, float precipitationDrop)
    {
        GameObject root = new GameObject(label);
        WeatherSkyRig rig = root.AddComponent<WeatherSkyRig>();
        rig.m_cloudHeight = cloudHeight;

        rig.CloudAnchor = CreateAnchor(root.transform, "Cloud", cloudHeight);
        // 구름 아래에서 방출한다 — 음수로 내려가지 않게 막는다(낙차가 높이보다 크면 카메라 아래에서 비가 솟는다)
        rig.PrecipitationAnchor = CreateAnchor(
            root.transform,
            "Precipitation",
            Mathf.Max(0f, cloudHeight - precipitationDrop)
        );

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
        // 파괴됐거나(리스폰) 꺼졌으면(CCTV 전환) 다시 찾는다 — Camera.main은 활성 카메라를 돌려준다
        if (m_camera == null || !m_camera.isActiveAndEnabled)
            m_camera = Camera.main;

        if (m_camera == null)
            return; // 아직 카메라가 없다 — 다음 프레임에 다시 본다

        // <b>위치만</b> 따라간다. 회전을 건드리지 않아 월드 정렬로 남는다.
        transform.position = m_camera.transform.position;
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
    /// 방출량·입자 크기를 배율로 키운다 — 프리팹 원본을 건드리지 않고 이 인스턴스만 바꾼다.
    /// Synty FX는 근거리 연출 기준으로 만들어져 하늘을 덮는 용도로는 양이 부족하다(눈이 잘 안 보이는 원인).
    /// </summary>
    public static void Boost(GameObject fx, float sizeMultiplier, float rateMultiplier)
    {
        if (fx == null)
            return;

        foreach (ParticleSystem ps in fx.GetComponentsInChildren<ParticleSystem>())
        {
            ParticleSystem.MainModule main = ps.main;
            main.startSizeMultiplier *= sizeMultiplier;
            // 상한도 함께 올린다 — 방출을 늘리면 기본 상한(보통 1000)에 걸려 조용히 잘린다
            main.maxParticles = Mathf.Max(main.maxParticles, (int)(main.maxParticles * rateMultiplier));

            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTimeMultiplier *= rateMultiplier;
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
