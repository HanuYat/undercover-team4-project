using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// <see cref="MapPalette"/>와 ASCII 레이아웃 한 장을 읽어 맵의 <b>바닥과 경계</b>만 격자로 깔아주는 1회용 에디터 도구. (#215)
///
/// 건물·프롭·조명은 손대지 않는다 — 풋프린트와 회전이 제각각이라 ASCII로 표현하면 오히려 손이 더 간다.
/// 생성된 것들은 평범한 씬 오브젝트라 이후 자유롭게 옮기고 지우면 된다. 런타임 시스템이 아니고
/// 저장되는 생성기 상태도 없다.
///
/// <b>무엇을 깔지는 전부 팔레트가 정한다.</b> 이 스크립트에는 배치 규칙만 있다 — 새 테마의 맵은
/// 팔레트를 하나 더 만들어 프리팹을 끌어다 넣으면 되고, 여기를 고칠 일은 없다.
///
/// <b>좌표 규약.</b> 셀 (col,row)는 x∈[col*c,(col+1)*c], z∈[row*c,(row+1)*c]를 차지한다(c = 팔레트의 칸 크기).
/// 레이아웃 텍스트의 첫 줄이 북쪽(z 최대)이라 에디터에서 위에서 내려다본 모양 그대로 읽힌다.
///
/// <b>사용법.</b> Project 창에서 팔레트 에셋을 고른 뒤 메뉴 Tools/맵 격자 생성.
/// 루트 이름은 팔레트가 물고 있는 레이아웃 파일명에서 "_Layout"을 뗀 것.
/// </summary>
public static class MapGridBuilder
{
    // 방향 0:+Z(북) 1:+X(동) 2:-Z(남) 3:-X(서)
    private static readonly int[] s_dirCol = { 0, 1, 0, -1 };
    private static readonly int[] s_dirRow = { 1, 0, -1, 0 };

    // 벽 조각은 로컬 +X가 두께, 로컬 -Z가 길이(한 칸)다. 두께가 경계 글자 칸 쪽을 향하도록 돌리고,
    // 조각이 뻗어나가는 반대쪽 끝에 피봇을 둔다 — 방향별로 붙는 모서리 끝점이 달라서 표로 박아둔다.
    private static readonly float[] s_wallYaw = { 90f, 180f, 270f, 0f };
    private static readonly int[] s_wallPivotCol = { 1, 1, 0, 0 };
    private static readonly int[] s_wallPivotRow = { 1, 0, 0, 1 };

    [MenuItem("Tools/맵 격자 생성")]
    private static void Build()
    {
        var palette = Selection.activeObject as MapPalette;
        if (palette == null)
        {
            Debug.LogError("MapGridBuilder: Project 창에서 MapPalette 에셋을 고른 뒤 실행할 것 (Create ▸ Map ▸ Map Palette)");
            return;
        }

        if (palette.Layout == null)
        {
            Debug.LogError($"MapGridBuilder: 팔레트 '{palette.name}'에 레이아웃 텍스트가 물려 있지 않다", palette);
            return;
        }

        if (palette.CellSize <= 0f)
        {
            Debug.LogError($"MapGridBuilder: 팔레트 '{palette.name}'의 칸 크기가 0 이하다", palette);
            return;
        }

        if (!TryParse(palette.Layout, out char[][] grid))
        {
            return;
        }

        string rootName = palette.Layout.name.Replace("_Layout", string.Empty);
        GameObject existing = GameObject.Find(rootName);
        if (existing != null)
        {
            bool replace = EditorUtility.DisplayDialog(
                "맵 격자 생성",
                $"씬에 이미 '{rootName}'이 있다. 지우고 다시 만들까?\n(직접 배치한 건물·프롭·스폰 포인트가 그 아래 있다면 함께 사라진다)",
                "지우고 생성",
                "취소"
            );

            if (!replace)
            {
                return;
            }
        }

        Random.InitState(palette.Seed);

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("맵 격자 생성");
        int undoGroup = Undo.GetCurrentGroup();

        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing);
        }

        var root = new GameObject(rootName);
        Undo.RegisterCreatedObjectUndo(root, "맵 격자 생성");

        var groups = new Dictionary<string, Transform>();
        int tiles = 0;
        var unknownSymbols = new HashSet<char>();

        for (int row = 0; row < grid.Length; row++)
        {
            for (int col = 0; col < grid[row].Length; col++)
            {
                char symbol = grid[row][col];
                if (symbol == ' ')
                {
                    continue;
                }

                if (palette.Wall.IsWallSymbol(symbol))
                {
                    continue; // 경계는 바닥을 다 깐 뒤 BuildBoundary가 한 번에 세운다
                }

                MapPalette.Entry entry = palette.Find(symbol);
                if (entry == null)
                {
                    unknownSymbols.Add(symbol);
                    continue;
                }

                if (PlaceCell(palette, entry, grid, Group(groups, root.transform, entry.Group), col, row))
                {
                    tiles++;
                }
            }
        }

        int walls = BuildBoundary(palette, grid, Group(groups, root.transform, "Boundary"), out int columns);

        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = root;

        float width2 = grid[0].Length * palette.CellSize;
        float depth = grid.Length * palette.CellSize;
        Debug.Log(
            $"MapGridBuilder: '{rootName}' 생성 완료 — {grid[0].Length}x{grid.Length}칸 ({width2}x{depth}m), "
                + $"바닥 {tiles}칸 · 벽 {walls}짝 · 기둥 {columns}개",
            root
        );

        if (unknownSymbols.Count > 0)
        {
            // 오타를 조용히 넘기면 맵에 구멍이 뚫린 채로 나온다 — 어떤 글자가 떴는지 알려준다.
            Debug.LogWarning(
                $"MapGridBuilder: 팔레트 '{palette.name}'에 규칙이 없는 글자를 건너뛰었다 — "
                    + $"'{string.Join("', '", unknownSymbols)}'",
                palette
            );
        }
    }

    /// <summary>
    /// <b>경계벽만</b> 다시 세운다 — 바닥·건물·프롭은 손대지 않는다.
    ///
    /// 벽 설정(게이트 프리팹·배율·Inset·두께)은 눈으로 맞춰가며 여러 번 고치게 되는데, 그때마다
    /// 전체 생성을 돌리면 루트 아래 손으로 배치한 건물·프롭·스폰 포인트가 전부 날아간다.
    /// 그 사고를 막으려고 따로 뒀다.
    /// </summary>
    [MenuItem("Tools/맵 경계만 다시 생성")]
    private static void RebuildBoundaryOnly()
    {
        var palette = Selection.activeObject as MapPalette;
        if (palette == null || palette.Layout == null)
        {
            Debug.LogError("MapGridBuilder: Project 창에서 레이아웃이 물린 MapPalette 에셋을 고른 뒤 실행할 것");
            return;
        }

        if (!TryParse(palette.Layout, out char[][] grid))
        {
            return;
        }

        string rootName = palette.Layout.name.Replace("_Layout", string.Empty);
        GameObject root = GameObject.Find(rootName);
        if (root == null)
        {
            Debug.LogError($"MapGridBuilder: 씬에 '{rootName}'이 없다 — 먼저 Tools/맵 격자 생성으로 만들 것", palette);
            return;
        }

        Undo.IncrementCurrentGroup();
        Undo.SetCurrentGroupName("맵 경계 재생성");
        int undoGroup = Undo.GetCurrentGroup();

        Transform existing = root.transform.Find("Boundary");
        if (existing != null)
        {
            Undo.DestroyObjectImmediate(existing.gameObject);
        }

        var boundaryGo = new GameObject("Boundary");
        Undo.RegisterCreatedObjectUndo(boundaryGo, "맵 경계 재생성");
        boundaryGo.transform.SetParent(root.transform, false);

        Random.InitState(palette.Seed);
        int walls = BuildBoundary(palette, grid, boundaryGo.transform, out int columns);

        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = boundaryGo;
        Debug.Log($"MapGridBuilder: '{rootName}' 경계 재생성 — 벽 {walls}짝 · 기둥 {columns}개", boundaryGo);
    }

    /// <summary>
    /// 경계 글자 칸을 훑어 벽 한 짝씩 세우고, 마지막에 기둥을 한 번만 세운다. 세운 짝 수를 돌려준다.
    /// </summary>
    private static int BuildBoundary(MapPalette palette, char[][] grid, Transform boundary, out int columns)
    {
        var columnSpots = new HashSet<Vector2Int>();
        var suppressedColumns = new HashSet<Vector2Int>();
        int walls = 0;

        if (palette.Wall.HasAnyPiece)
        {
            for (int row = 0; row < grid.Length; row++)
            {
                for (int col = 0; col < grid[row].Length; col++)
                {
                    if (palette.Wall.IsWallSymbol(grid[row][col]))
                    {
                        walls += PlaceWall(palette, grid, boundary, col, row, columnSpots, suppressedColumns);
                    }
                }
            }
        }

        // 게이트 개구부 안쪽 자리는 빼고 세운다 — 모으는 도중에 지우면 나중에 다시 채워질 수 있다.
        columnSpots.ExceptWith(suppressedColumns);

        if (palette.Wall.Column != null)
        {
            foreach (Vector2Int spot in columnSpots)
            {
                var at = new Vector3(spot.x, 0f, spot.y);
                GameObject column = PlaceRaw(palette.Wall.Column, boundary, at, 0f);

                if (!palette.Wall.UseBoxCollider)
                {
                    continue;
                }

                // 기둥은 벽면보다 안쪽으로 튀어나와 있다 — 상자를 따로 세우지 않으면 몸이 파묻힌다.
                DisableColliders(column);
                float width = palette.Wall.ColumnWidth;
                PlaceBlocker(
                    boundary,
                    at,
                    0f,
                    new Vector3(0f, palette.Wall.Height * 0.5f, 0f),
                    new Vector3(width, palette.Wall.Height, width)
                );
            }
        }

        columns = columnSpots.Count;
        return walls;
    }

    /// <summary>
    /// 주석·빈 줄을 걷어내고 남은 줄을 격자로 만든다. 줄 길이가 하나라도 어긋나면 만들지 않는다 —
    /// 어긋난 채로 깔면 어디가 밀렸는지 눈으로 찾기 어렵다.
    /// </summary>
    private static bool TryParse(TextAsset asset, out char[][] grid)
    {
        grid = null;

        var lines = new List<string>();
        foreach (string raw in asset.text.Split('\n'))
        {
            string line = raw.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith("//"))
            {
                continue;
            }

            lines.Add(line);
        }

        if (lines.Count == 0)
        {
            Debug.LogError($"MapGridBuilder: '{asset.name}'에 배치 줄이 하나도 없다", asset);
            return false;
        }

        int width = lines[0].Length;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length != width)
            {
                Debug.LogError(
                    $"MapGridBuilder: '{asset.name}' {i + 1}번째 배치 줄 길이가 {lines[i].Length} — 첫 줄({width})과 달라 중단한다",
                    asset
                );
                return false;
            }
        }

        // 첫 줄이 북쪽이므로 뒤집어 담아 row 0을 남쪽으로 만든다.
        grid = new char[lines.Count][];
        for (int i = 0; i < lines.Count; i++)
        {
            grid[lines.Count - 1 - i] = lines[i].ToCharArray();
        }

        return true;
    }

    /// <summary>
    /// 한 칸을 채운다 — 밑깔개가 있으면 먼저 깔고, 연석 프리팹이 채워져 있으면 접한 도로 쪽으로 돌려 놓는다.
    /// 놓을 프리팹이 하나도 없으면 false.
    /// </summary>
    private static bool PlaceCell(
        MapPalette palette,
        MapPalette.Entry entry,
        char[][] grid,
        Transform parent,
        int col,
        int row
    )
    {
        if (!HasAny(entry.Prefabs))
        {
            return false;
        }

        if (HasAny(entry.UnderlayPrefabs))
        {
            PlaceTile(palette, Pick(entry.UnderlayPrefabs), parent, col, row, 0f, 0f, 1f);
        }

        if (entry.UsesEdgeFacing && TryPlaceEdgeFacing(palette, entry, grid, parent, col, row))
        {
            return true;
        }

        float yaw = entry.RandomYaw ? Random.Range(0, 4) * 90f : 0f;
        PlaceTile(palette, Pick(entry.Prefabs), parent, col, row, yaw, entry.YOffset, entry.Scale);
        return true;
    }

    /// <summary>
    /// 접한 도로 방향을 보고 연석이 그쪽을 보도록 프리팹과 회전을 고른다 — 맞는 경우가 없으면 false.
    /// </summary>
    private static bool TryPlaceEdgeFacing(
        MapPalette palette,
        MapPalette.Entry entry,
        char[][] grid,
        Transform parent,
        int col,
        int row
    )
    {
        var facing = new bool[4];
        int count = 0;

        for (int d = 0; d < 4; d++)
        {
            if (palette.IsEdgeFacing(At(grid, col + s_dirCol[d], row + s_dirRow[d])))
            {
                facing[d] = true;
                count++;
            }
        }

        if (count == 1)
        {
            for (int d = 0; d < 4; d++)
            {
                if (facing[d])
                {
                    // 기본 연석이 로컬 +Z 쪽이므로 yaw = d*90이면 연석이 도로를 본다.
                    PlaceTile(palette, Pick(entry.EdgePrefabs), parent, col, row, d * 90f, entry.YOffset, entry.Scale);
                    return true;
                }
            }
        }
        else if (count == 2 && HasAny(entry.CornerPrefabs))
        {
            for (int d = 0; d < 4; d++)
            {
                if (facing[d] && facing[(d + 1) % 4])
                {
                    // 기본 모서리 프리팹은 +Z·+X 두 면이 도로 쪽이다.
                    PlaceTile(palette, Pick(entry.CornerPrefabs), parent, col, row, d * 90f, entry.YOffset, entry.Scale);
                    return true;
                }
            }
        }

        return false; // 도로가 없거나 마주보는 양쪽에 있는 경우 — 기본 프리팹으로 떨어진다
    }

    /// <summary>
    /// 경계벽 — 경계 글자 칸과 맞닿은 <b>안쪽 칸</b>마다 그 모서리에 한 짝을 세운다. 세운 짝 수를 돌려준다.
    /// 기둥 자리는 <paramref name="columnSpots"/>에 모아 두었다가 나중에 한 번만 세운다.
    /// </summary>
    private static int PlaceWall(
        MapPalette palette,
        char[][] grid,
        Transform parent,
        int col,
        int row,
        HashSet<Vector2Int> columnSpots,
        HashSet<Vector2Int> suppressedColumns
    )
    {
        MapPalette.WallSettings wall = palette.Wall;
        float cell = palette.CellSize;
        int placed = 0;

        for (int d = 0; d < 4; d++)
        {
            char neighbour = At(grid, col + s_dirCol[d], row + s_dirRow[d]);
            if (neighbour == '\0' || neighbour == ' ' || wall.IsWallSymbol(neighbour))
            {
                continue;
            }

            float yaw = s_wallYaw[d];
            Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
            var edge = new Vector3((col + s_wallPivotCol[d]) * cell, 0f, (row + s_wallPivotRow[d]) * cell);

            // 벽면을 칸 경계보다 안쪽으로 당긴다 — 경계에 딱 세우면 얇은 바닥 타일의 옆면 너머로
            // 바닥 밑이 비친다. 로컬 +X가 바깥(경계 글자 칸) 방향이라 -X가 안쪽이다.
            Vector3 pivot = edge + rotation * new Vector3(-wall.Inset, 0f, 0f);

            // 도로가 닿는 자리는 벽 조각 대신 게이트를 세운다 — 길이 벽에 막혀 끊기는 대신 관문으로 읽힌다.
            // 도로는 보통 두 칸 폭이므로 칸마다 하나씩 세우면 작은 문이 둘 생긴다. 개구부 전체를 한 짝이
            // 덮도록, 이어진 칸 중 <b>시작 칸만</b> 게이트를 세우고 나머지 칸은 물리(블로커)만 남긴다.
            // 벽 조각은 게이트 자리에도 그대로 둔다 — 게이트는 벽을 뚫는 것이 아니라 벽 앞에 세우는 관문이다.
            GameObject bottom = PlaceRaw(wall.Bottom, parent, pivot, yaw);
            GameObject top = PlaceRaw(wall.Top, parent, pivot + Vector3.up * wall.TopY, yaw);
            GameObject cap = PlaceRaw(wall.Cap, parent, pivot + Vector3.up * wall.CapY, yaw);

            // 도로가 닿는 자리에는 개구부 전체를 덮는 게이트를 한 짝만 세운다 —
            // 칸마다 세우면 작은 문이 둘 생기고, 가운데 기둥이 문을 가로지른다.
            Vector3 extend = rotation * Vector3.back; // 조각이 뻗어나가는 방향 (로컬 -Z)
            if (wall.Gate != null
                && palette.IsEdgeFacing(neighbour)
                && IsGateRunStart(palette, grid, col, row, d, cell, extend, out int runCells))
            {
                float runLength = runCells * cell;
                float gateLength = cell * wall.GateScale.z;
                Vector3 gateAt = pivot
                    + extend * ((runLength - gateLength) * 0.5f)          // 개구부 한가운데로
                    + rotation * new Vector3(-wall.GateInset, 0f, 0f);    // 벽면보다 더 안쪽으로

                GameObject gate = PlaceRaw(wall.Gate, parent, gateAt, yaw);
                if (gate != null)
                {
                    gate.transform.localScale = wall.GateScale;
                }

                // 개구부 안쪽 기둥은 세우지 않는다 — 문을 가로질러 서 버린다. 양 끝 기둥은 남긴다.
                for (int i = 1; i < runCells; i++)
                {
                    Vector3 inner = edge + extend * (cell * i);
                    suppressedColumns.Add(new Vector2Int(Mathf.RoundToInt(inner.x), Mathf.RoundToInt(inner.z)));
                }
            }

            if (wall.UseBoxCollider)
            {
                DisableColliders(bottom);
                DisableColliders(top);
                DisableColliders(cap);

                // 조각들의 콜리전을 대신하는 상자 하나 — 로컬 +X가 두께, -Z가 길이라 조각들과 축이 같다.
                PlaceBlocker(
                    parent,
                    pivot,
                    yaw,
                    new Vector3(wall.Thickness * 0.5f, wall.Height * 0.5f, -cell * 0.5f),
                    new Vector3(wall.Thickness, wall.Height, cell)
                );
            }

            if (wall.Column != null)
            {
                // 기둥은 당기지 않고 칸 모서리에 그대로 둔다 — 모서리에서 두 벽이 서로 다른 방향으로
                // 당겨지므로, 같이 당기면 기둥이 둘로 어긋나 겹쳐 선다. (WallSettings.Inset 툴팁 참고)
                Vector3 far = edge + rotation * new Vector3(0f, 0f, -cell);
                columnSpots.Add(new Vector2Int(Mathf.RoundToInt(edge.x), Mathf.RoundToInt(edge.z)));
                columnSpots.Add(new Vector2Int(Mathf.RoundToInt(far.x), Mathf.RoundToInt(far.z)));
            }

            placed++;
        }

        return placed;
    }

    /// <summary>
    /// 이 칸이 도로 개구부의 <b>시작 칸</b>인가 — 게이트를 세울 칸 하나를 고른다.
    /// 개구부에 이어진 칸 수를 <paramref name="runCells"/>로 돌려준다.
    ///
    /// '시작'은 조각이 뻗어나가는 방향(<paramref name="extend"/>) 기준으로 가장 뒤쪽 칸이다 —
    /// 벽 방향마다 피봇이 붙는 끝이 달라서 col·row의 대소로 정하면 절반은 반대로 뻗는다.
    /// </summary>
    private static bool IsGateRunStart(
        MapPalette palette,
        char[][] grid,
        int col,
        int row,
        int d,
        float cell,
        Vector3 extend,
        out int runCells
    )
    {
        // 벽이 이어지는 축 — 남북 벽은 열을 따라, 동서 벽은 행을 따라 이어진다.
        int stepCol = (d == 0 || d == 2) ? 1 : 0;
        int stepRow = (d == 0 || d == 2) ? 0 : 1;

        int back = 0;
        while (FrontsRoad(palette, grid, col - stepCol * (back + 1), row - stepRow * (back + 1), d))
        {
            back++;
        }

        int forward = 0;
        while (FrontsRoad(palette, grid, col + stepCol * (forward + 1), row + stepRow * (forward + 1), d))
        {
            forward++;
        }

        runCells = back + forward + 1;

        // 뻗는 방향으로 한 칸 뒤가 같은 개구부면 그쪽이 시작이다.
        var stepWorld = new Vector3(stepCol * cell, 0f, stepRow * cell);
        int behindCol = Vector3.Dot(stepWorld, extend) > 0f ? col - stepCol : col + stepCol;
        int behindRow = Vector3.Dot(stepWorld, extend) > 0f ? row - stepRow : row + stepRow;
        return !FrontsRoad(palette, grid, behindCol, behindRow, d);
    }

    /// <summary>그 칸이 경계 글자이면서 <paramref name="d"/> 쪽 이웃이 도로인가.</summary>
    private static bool FrontsRoad(MapPalette palette, char[][] grid, int col, int row, int d)
    {
        if (!palette.Wall.IsWallSymbol(At(grid, col, row)))
        {
            return false;
        }

        return palette.IsEdgeFacing(At(grid, col + s_dirCol[d], row + s_dirRow[d]));
    }

    /// <summary>
    /// 벽 조각의 콜라이더를 끈다 — 물리는 <see cref="PlaceBlocker"/>가 세운 상자가 대신 든다.
    ///
    /// <b>왜 콜리전 메시를 그대로 두지 않는가.</b> Synty 팩의 콜리전 껍질은 렌더 메시에서 구운 볼록 껍질이라
    /// 벽의 '평평한' 면이 수직이 아니다 — 격리벽 하단 패널은 실측 88.0도(법선 y=+0.036)로 2도 눕어 있다.
    /// 그 2도 때문에 CharacterController가 옆면 접촉을 지면으로 쳐서 플레이어가 수직 벽면 위에 서고,
    /// 거기서 다시 뛰면 더 높이 얹혀 벽을 타고 올라갔다.
    ///
    /// 판정 자체는 <c>PlayerMovement.IsStablyGrounded</c>가 고쳤다. 여기서 상자로 바꾸는 것은 별개 이유다 —
    /// 맵 경계는 넘으면 안 되는 선이라 이중으로 막을 값어치가 있고, 상자는 물리 껍질이 눈에 보이는 면과
    /// 정확히 일치해 벽 너머 조준(<c>AimOcclusion</c>)도 어긋나지 않는다. 콜라이더 수도 줄어든다.
    /// </summary>
    private static void DisableColliders(GameObject instance)
    {
        if (instance == null)
        {
            return;
        }

        foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true))
        {
            collider.enabled = false;
        }
    }

    /// <summary>콜라이더만 있는 상자를 세운다 — center·size는 로컬 기준.</summary>
    private static void PlaceBlocker(Transform parent, Vector3 position, float yaw, Vector3 center, Vector3 size)
    {
        var blocker = new GameObject("WallBlocker");
        blocker.transform.SetParent(parent, false);
        blocker.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));
        blocker.isStatic = true;

        BoxCollider box = blocker.AddComponent<BoxCollider>();
        box.center = center;
        box.size = size;
    }

    /// <summary>
    /// 한 칸에 타일을 앉힌다. 팩마다 피봇 규칙이 제각각이고(모서리 피봇이 흔하다) 흙처럼 칸보다 큰 것도 있어,
    /// 피봇이 아니라 <b>렌더러 경계의 중심</b>을 칸 중심에 맞춘다.
    /// </summary>
    private static void PlaceTile(
        MapPalette palette,
        GameObject prefab,
        Transform parent,
        int col,
        int row,
        float yaw,
        float y,
        float scale
    )
    {
        GameObject instance = PlaceRaw(prefab, parent, Vector3.zero, yaw);
        if (instance == null)
        {
            return;
        }

        if (!Mathf.Approximately(scale, 1f))
        {
            instance.transform.localScale = Vector3.one * scale; // 경계를 재기 전에 키운다
        }

        float cell = palette.CellSize;
        Bounds bounds = RendererBounds(instance);
        Vector3 position = instance.transform.position;
        position.x += (col + 0.5f) * cell - bounds.center.x;
        position.z += (row + 0.5f) * cell - bounds.center.z;
        position.y = y;
        instance.transform.position = position;
    }

    private static GameObject PlaceRaw(GameObject prefab, Transform parent, Vector3 position, float yaw)
    {
        if (prefab == null)
        {
            return null;
        }

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
        instance.transform.SetPositionAndRotation(position, Quaternion.Euler(0f, yaw, 0f));

        // Synty 팩엔 LODGroup이 하나도 없다 — 정적 배칭·오클루전 컬링에 기대야 해서 미리 표시해 둔다.
        GameObjectUtility.SetStaticEditorFlags(
            instance,
            StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic
        );

        return instance;
    }

    /// <summary>이름별 자식 묶음 — 없으면 만든다.</summary>
    private static Transform Group(Dictionary<string, Transform> groups, Transform root, string name)
    {
        if (groups.TryGetValue(name, out Transform existing))
        {
            return existing;
        }

        var child = new GameObject(name);
        child.transform.SetParent(root, false);
        groups[name] = child.transform;
        return child.transform;
    }

    private static Bounds RendererBounds(GameObject instance)
    {
        Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return new Bounds(instance.transform.position, Vector3.zero);
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds;
    }

    private static char At(char[][] grid, int col, int row)
    {
        if (row < 0 || row >= grid.Length || col < 0 || col >= grid[row].Length)
        {
            return '\0';
        }

        return grid[row][col];
    }

    private static bool HasAny(GameObject[] pool)
    {
        if (pool == null)
        {
            return false;
        }

        foreach (GameObject prefab in pool)
        {
            if (prefab != null)
            {
                return true;
            }
        }

        return false;
    }

    // 비어 있는 칸이 섞여 있어도(인스펙터에서 None으로 남겨둔 칸) 실제 프리팹만 뽑는다.
    private static GameObject Pick(GameObject[] pool)
    {
        int candidates = 0;
        foreach (GameObject prefab in pool)
        {
            if (prefab != null)
            {
                candidates++;
            }
        }

        if (candidates == 0)
        {
            return null;
        }

        int index = Random.Range(0, candidates);
        foreach (GameObject prefab in pool)
        {
            if (prefab == null)
            {
                continue;
            }

            if (index-- == 0)
            {
                return prefab;
            }
        }

        return null;
    }
}
