using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// ⚠ <b>임시 도구</b> — `feature/ragdoll-hotfix`의 시체 순간이동 검증용. 끝나면
/// <c>Assets/Scripts/Diagnostics/</c>와 함께 지운다.
///
/// <b>흩어진 진단 로그를 하나로 모은다.</b> MPPM은 가상 플레이어마다 별도 프로젝트 루트를 주므로
/// 로그가 이렇게 갈린다:
/// <list type="bullet">
///   <item>호스트 — <c>Logs/corpse-diag-호스트.log</c></item>
///   <item>클라 — <c>Library/VP/&lt;해시&gt;/Logs/corpse-diag-클라N.log</c></item>
/// </list>
/// 손으로 찾기 어렵고, 찾아도 <b>피어별 <c>Time.time</c> 원점이 달라</b> 나란히 놓으면 같은 순간이
/// 어긋난다. 그래서 <b>서버 틱</b>으로 정렬해 합친다 — 전 피어가 공유하는 유일한 시계다.
/// </summary>
public static class CorpseDiagCollector
{
    private const string k_mergedName = "corpse-diag-merged.log";

    // "틱=123" 을 뽑는다. 캡처 헤더처럼 틱이 없는 줄은 바로 다음 줄의 틱에 붙인다.
    private static readonly Regex s_tick = new Regex(@"틱=(-?\d+)", RegexOptions.Compiled);

    // 틱을 모르는 줄 — 틱 필드를 넣기 전에 쌓인 옛 세션이다. 새 세션에 섞이지 않게 <b>맨 앞</b>으로
    // 모은다(옛것이 먼저다). MaxValue로 뒤에 두면 최신 줄 뒤에 붙어 마치 나중 일처럼 읽힌다.
    private const long k_noTick = long.MinValue;

    private static long TickOf(string line)
    {
        Match match = s_tick.Match(line);
        return match.Success && long.TryParse(match.Groups[1].Value, out long parsed)
            ? parsed
            : k_noTick;
    }

    private sealed class Entry
    {
        public long Tick;
        public int Order; // 같은 틱 안에서 원래 순서를 지킨다
        public string Source;
        public string Line;
    }

    [MenuItem("Tools/Ragdoll/시체 진단 로그 모으기")]
    public static void Collect()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        List<string> files = FindLogs(projectRoot);

        if (files.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "시체 진단 로그",
                "corpse-diag-*.log 파일을 찾지 못했다.\n\n"
                    + "MPPM으로 한 번 플레이해 순간이동을 재현하면 생긴다.\n"
                    + "찾는 곳: Logs/ 와 Library/VP/*/Logs/",
                "확인"
            );
            return;
        }

        var entries = new List<Entry>();
        var summary = new StringBuilder();
        int order = 0;

        for (int i = 0; i < files.Count; i++)
        {
            string source = Path.GetFileNameWithoutExtension(files[i])
                .Replace("corpse-diag-", "");

            string[] lines;
            try
            {
                lines = File.ReadAllLines(files[i]);
            }
            catch (System.Exception e)
            {
                summary.AppendLine("  ⚠ 읽기 실패 " + files[i] + " — " + e.Message);
                continue;
            }

            // 틱은 <b>줄 자신의 것만</b> 쓴다. 예외는 캡처 헤더 하나 — 그 줄에는 틱이 없지만 바로
            // 다음 줄이 같은 캡처의 첫 표본이라 그것을 물려받아야 헤더가 자기 캡처 앞에 붙는다.
            //
            // ⚠ <b>물려받기를 여기서 끊는 것이 중요하다.</b> 틱 필드를 넣기 전에 쌓인 옛 세션 줄에는
            // 틱이 아예 없는데, 무제한으로 물려받게 두면 그것들이 <b>새 세션의 틱을 달고</b> 같은
            // 구간에 섞여 들어온다 — 실제로 그렇게 나왔다. 틱이 없으면 없는 대로 앞으로 모은다.
            var ticks = new long[lines.Length];
            for (int k = 0; k < lines.Length; k++)
            {
                Match match = s_tick.Match(lines[k]);
                if (match.Success && long.TryParse(match.Groups[1].Value, out long parsed))
                {
                    ticks[k] = parsed;
                    continue;
                }

                bool isHeader = lines[k].Contains("▶캡처");
                long next = k + 1 < lines.Length ? TickOf(lines[k + 1]) : k_noTick;
                ticks[k] = isHeader && next != k_noTick ? next : k_noTick;
            }

            for (int k = 0; k < lines.Length; k++)
            {
                if (lines[k].Length == 0)
                    continue;

                entries.Add(new Entry
                {
                    Tick = ticks[k],
                    Order = order++,
                    Source = source,
                    Line = lines[k],
                });
            }

            summary.AppendLine("  " + source + " — " + lines.Length + "줄  (" + Rel(projectRoot, files[i]) + ")");
        }

        // 틱을 모르는 줄은 맨 앞에 모인다(k_noTick 주석) — 새 세션 구간을 더럽히지 않는다.
        entries.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.Order.CompareTo(b.Order));

        var merged = new StringBuilder();
        merged.AppendLine("# 시체 진단 로그 병합 — 서버 틱 오름차순");
        merged.AppendLine("# 원본 " + files.Count + "개");
        merged.Append(summary);
        merged.AppendLine("# 열: [틱] <출처> <원본 줄>");
        merged.AppendLine();

        long lastTick = long.MinValue;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];

            // 틱이 바뀌면 빈 줄 — 같은 순간의 피어별 줄이 한 덩어리로 보인다.
            if (e.Tick != lastTick)
            {
                merged.AppendLine();
                lastTick = e.Tick;
            }

            string tick = e.Tick == k_noTick ? " 옛것 " : e.Tick.ToString().PadLeft(6);
            merged.AppendLine("[" + tick + "] " + e.Source.PadRight(8) + " " + e.Line);
        }

        string outPath = Path.Combine(projectRoot, "Logs", k_mergedName);
        Directory.CreateDirectory(Path.GetDirectoryName(outPath));
        File.WriteAllText(outPath, merged.ToString());

        Debug.Log(
            "[시체진단] 병합 완료 — " + entries.Count + "줄 / 원본 " + files.Count + "개 → " + outPath
        );
        EditorUtility.RevealInFinder(outPath);
    }

    [MenuItem("Tools/Ragdoll/시체 진단 로그 지우기")]
    public static void Clear()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        List<string> files = FindLogs(projectRoot);

        if (files.Count == 0)
        {
            Debug.Log("[시체진단] 지울 로그가 없다");
            return;
        }

        if (!EditorUtility.DisplayDialog(
                "시체 진단 로그 지우기",
                files.Count + "개 파일을 지운다. 되돌릴 수 없다.\n\n"
                    + "다음 재현이 깨끗한 로그로 시작하게 하려는 것이다.",
                "지운다",
                "취소"))
            return;

        int removed = 0;
        for (int i = 0; i < files.Count; i++)
        {
            try
            {
                File.Delete(files[i]);
                removed++;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[시체진단] 지우기 실패 " + files[i] + " — " + e.Message);
            }
        }

        Debug.Log("[시체진단] " + removed + "개 지웠다");
    }

    // 호스트(프로젝트 Logs/)와 가상 플레이어(Library/VP/*/Logs/)를 모두 훑는다.
    // 병합 결과물 자신은 제외한다 — 두 번 돌리면 스스로를 먹는다.
    private static List<string> FindLogs(string projectRoot)
    {
        var found = new List<string>();

        AddFrom(found, Path.Combine(projectRoot, "Logs"));

        string vpRoot = Path.Combine(projectRoot, "Library", "VP");
        if (Directory.Exists(vpRoot))
        {
            string[] vps = Directory.GetDirectories(vpRoot);
            for (int i = 0; i < vps.Length; i++)
                AddFrom(found, Path.Combine(vps[i], "Logs"));
        }

        return found;
    }

    private static void AddFrom(List<string> into, string directory)
    {
        if (!Directory.Exists(directory))
            return;

        string[] files = Directory.GetFiles(directory, "corpse-diag-*.log");
        for (int i = 0; i < files.Length; i++)
        {
            if (Path.GetFileName(files[i]) == k_mergedName)
                continue;
            into.Add(files[i]);
        }
    }

    private static string Rel(string root, string full) =>
        full.StartsWith(root) ? full.Substring(root.Length).TrimStart('\\', '/') : full;
}
