using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 테스트용 베이스 씬 생성기 — 복사해서 쓰는 '플레이하면 바로 움직여지는' 최소 씬을 만든다.
/// 메뉴: Tools/Test Scene/Create Base Test Scene → Assets/Scenes/TestBase.unity
///
/// 테스트 씬에 <b>Player 프리팹을 직접 놓으면 조작되지 않는다</b>: PlayerInputHandler는 입력 액션
/// 구독을 전부 OnNetworkSpawn에서 하는데, 씬에 배치한 인스턴스는 NGO가 스폰하지 않아 그 콜백이
/// 오지 않는다. 그래서 Main Scene처럼 NetworkManager + PlayerSpawnManager를 두고 런타임에
/// 스폰시킨다 — 플레이어는 NetworkManager.PlayerPrefab에서 나온다.
///
/// AppBootstrap(세션·Auth·Vivox)은 일부러 넣지 않는다 — DevAutoHost가 Relay 없이 로컬
/// StartHost만 하므로 필요 없고, AuthBootstrap의 자동 로그인(UGS)까지 붙으면 Play가 느려진다.
/// 세션·음성이 필요한 테스트라면 Assets/Prefabs/AppBootstrap.prefab을 직접 얹으면 된다.
///
/// 만드는 것은 뼈대뿐이다. NPC가 필요한 테스트는 NavMeshSurface를, 라운드 흐름이 필요한 테스트는
/// 인게임 매니저(RoundManager 등)를 각자 얹는다 — 그건 Main Scene을 복제하는 편이 빠르다.
/// </summary>
public static class TestBaseSceneBuilder
{
    private const string k_scenePath = "Assets/Scenes/TestBase.unity";
    private const string k_networkManagerPath = "Assets/Prefabs/NetworkManager.prefab";

    [MenuItem("Tools/Test Scene/Create Base Test Scene")]
    public static void Create()
    {
        GameObject networkManagerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            k_networkManagerPath
        );
        if (networkManagerPrefab == null)
        {
            Debug.LogError($"[TestBaseSceneBuilder] {k_networkManagerPath} 를 찾지 못했다.");
            return;
        }

        // 지금 열린 씬의 변경사항 저장 여부를 먼저 묻는다 — 취소하면 아무 것도 하지 않는다
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            return;

        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // 루트 그룹 — Main Scene과 같은 구분자 이름을 쓴다
        Transform environment = new GameObject("=== ENVIRONMENT ===").transform;
        Transform systems = new GameObject("=== SYSTEMS ===").transform;
        new GameObject("=== WORLD ===");

        CreateLight(environment);
        CreateFallbackCamera(environment);
        CreateFloor(environment);

        Transform spawnPoint = CreateSpawnPoint(systems);
        InstantiateNetworkManager(networkManagerPrefab, systems);
        CreateSpawnManager(systems, spawnPoint);
        CreateDevAutoHost(systems);

        EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), k_scenePath);
        Debug.Log(
            $"[TestBaseSceneBuilder] {k_scenePath} 생성 완료 — 복제(Ctrl+D)해서 테스트별로 쓸 것. "
                + "테스트 대상은 === WORLD === 아래에 배치한다."
        );
    }

    private static void CreateLight(Transform parent)
    {
        var go = new GameObject("Directional light");
        go.transform.SetParent(parent);
        go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

        Light light = go.AddComponent<Light>();
        light.type = LightType.Directional;
        light.shadows = LightShadows.Soft;
    }

    // 스폰 전까지 화면을 채우는 폴백 카메라 — depth -1이라 플레이어 카메라(0)가 위에 그려진다
    // (Main Scene의 Main Camera와 같은 방침). AudioListener는 씬에 하나 있어야 해서 여기 둔다.
    private static void CreateFallbackCamera(Transform parent)
    {
        var go = new GameObject("Main Camera") { tag = "MainCamera" };
        go.transform.SetParent(parent);
        go.transform.SetPositionAndRotation(
            new Vector3(0f, 3f, -8f),
            Quaternion.Euler(10f, 0f, 0f)
        );

        go.AddComponent<Camera>().depth = -1f;
        go.AddComponent<AudioListener>();
    }

    private static void CreateFloor(Transform parent)
    {
        GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
        floor.name = "Floor";
        floor.transform.SetParent(parent);
        floor.transform.localScale = new Vector3(4f, 1f, 4f); // 40m x 40m
    }

    private static Transform CreateSpawnPoint(Transform parent)
    {
        var go = new GameObject("SpawnPoint");
        go.transform.SetParent(parent);
        go.transform.position = new Vector3(0f, 1f, 0f); // 바닥에 끼지 않게 살짝 띄운다
        return go.transform;
    }

    private static void InstantiateNetworkManager(GameObject prefab, Transform parent)
    {
        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
        instance.transform.SetParent(parent);
    }

    // 직렬화 필드가 private이라 SerializedObject로 밀어 넣는다
    private static void CreateSpawnManager(Transform parent, Transform spawnPoint)
    {
        var go = new GameObject("PlayerSpawnManager");
        go.transform.SetParent(parent);

        var spawnManager = go.AddComponent<PlayerSpawnManager>();
        var serialized = new SerializedObject(spawnManager);
        serialized.FindProperty("m_spawnPlayers").boolValue = true;
        serialized.FindProperty("m_spawnPoint").objectReferenceValue = spawnPoint;
        serialized.ApplyModifiedPropertiesWithoutUndo();
    }

    // 에디터 전용 컴포넌트 — Play 시 로컬 호스트를 띄운다. 이게 없으면 플레이어가 스폰되지 않는다.
    private static void CreateDevAutoHost(Transform parent)
    {
        var go = new GameObject("DevAutoHost");
        go.transform.SetParent(parent);
        go.AddComponent<DevAutoHost>();
    }
}
