using UnityEngine;

// 빌드가 어느 커밋에서 나왔는지 진단용 값 (#622) — 참가 차단에는 절대 쓰지 않는다.
public static class BuildStamp
{
    public const string k_unknownSha = "?";

    private static string s_sha;

    public static string Sha => s_sha ??= Resolve();

    private static string Resolve()
    {
#if UNITY_EDITOR
        return ReadFromGit();
#else
        TextAsset stamp = Resources.Load<TextAsset>("BuildStamp");
        return stamp != null && !string.IsNullOrWhiteSpace(stamp.text)
            ? stamp.text.Trim()
            : k_unknownSha;
#endif
    }

#if UNITY_EDITOR
    public static string ReadFromGit()
    {
        string sha = RunGit("rev-parse --short HEAD");
        if (string.IsNullOrEmpty(sha))
            return k_unknownSha;

        string status = RunGit("status --porcelain");
        return string.IsNullOrEmpty(status) ? sha : sha + "-dirty";
    }

    private static string RunGit(string arguments)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("git", arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Application.dataPath,
            };

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process == null)
                return null;

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                process.Kill();
                return null;
            }

            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[BuildStamp] git 판독 실패(무시) / {e.Message}");
            return null;
        }
    }
#endif
}
